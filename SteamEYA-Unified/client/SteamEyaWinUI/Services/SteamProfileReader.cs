using System.Collections.Concurrent;
using System.Xml.Linq;
using Microsoft.UI.Xaml.Media.Imaging;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 只读的 Steam 资料读取器：按 SteamID 抓昵称与头像，结果缓存在内存里（短 TTL），
/// 头像落盘到 %AppData%\SteamEYA\avatars。
///
/// 与 AccountHistoryService 的关系：这段逻辑原先以私有方法存在于账号历史服务中，
/// 删除历史功能后抽成独立只读组件——不依赖历史库、不写任何账号记录，
/// 只服务于登录页右侧「账号信息」面板。
/// </summary>
internal static class SteamProfileReader
{
    private const string AppFolderName = "SteamEYA";
    private const string AvatarFolderName = "avatars";

    /// <summary>内存缓存有效期：同一账号短时间内重复上号不再重复请求 Steam 社区。</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    /// <summary>steamId → (抓取时间, 资料)。资料为 null 表示上次抓取失败，用短 TTL 负缓存避免连点重试打爆社区页。</summary>
    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, SteamProfileInfo? Info)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromSeconds(30);

    private static readonly object AvatarGate = new();

    public static string AvatarFolderPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppFolderName,
        AvatarFolderName);

    /// <summary>读取某账号的昵称与头像；失败一律返回 null（面板保持占位，不阻断上号）。</summary>
    public static async Task<SteamProfileInfo?> ReadAsync(string? steamId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(steamId))
        {
            return null;
        }

        steamId = steamId.Trim();

        if (Cache.TryGetValue(steamId, out var cached))
        {
            var ttl = cached.Info is null ? NegativeCacheTtl : CacheTtl;
            if (DateTimeOffset.UtcNow - cached.At < ttl)
            {
                return cached.Info;
            }
        }

        SteamProfileInfo? info = null;
        try
        {
            info = await FetchAsync(steamId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取 Steam 资料失败（{steamId}）：{ex.Message}");
        }

        Cache[steamId] = (DateTimeOffset.UtcNow, info);
        return info;
    }

    private static async Task<SteamProfileInfo?> FetchAsync(string steamId, CancellationToken cancellationToken)
    {
        // nc 一次性参数穿透边缘缓存：?xml=1 会被 CDN 缓存最长 1 小时（缓存键含查询串，唯一参数必回源），
        // 否则改名/换头像后会抓到旧快照。
        var url = "https://steamcommunity.com/profiles/" + Uri.EscapeDataString(steamId) +
                  "?xml=1&nc=" + DateTimeOffset.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);

        string xml;
        using (var response = await Http.GetAsync(url, cancellationToken))
        {
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            xml = await response.Content.ReadAsStringAsync(cancellationToken);
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            // 不存在/被封禁的账号返回 200 + HTML 错误页，非 XML。
            AppLog.Warn($"Steam 资料响应非 XML（{steamId}），已跳过。");
            return null;
        }

        var root = document.Root;
        if (root is null)
        {
            return null;
        }

        var personaName = root.Element("steamID")?.Value;
        var avatarUrl = root.Element("avatarFull")?.Value ?? root.Element("avatarMedium")?.Value;
        var avatarPath = string.IsNullOrWhiteSpace(avatarUrl)
            ? null
            : await TryDownloadAvatarAsync(steamId, avatarUrl!, cancellationToken);

        return new SteamProfileInfo(steamId, personaName, avatarPath);
    }

    private static async Task<string?> TryDownloadAvatarAsync(
        string steamId,
        string avatarUrl,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(avatarUrl, UriKind.Absolute, out var avatarUri))
        {
            return null;
        }

        string? tempPath = null;
        try
        {
            using var response = await Http.GetAsync(avatarUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length == 0 || !LooksLikeImage(bytes))
            {
                // 拦下捕获门户/劫持网络返回的 200+HTML「头像」：这类坏文件落盘后 UI 解码必失败。
                return null;
            }

            Directory.CreateDirectory(AvatarFolderPath);
            var avatarPath = Path.Combine(AvatarFolderPath, $"{GetSafeAvatarKey(steamId)}.jpg");

            lock (AvatarGate)
            {
                // 原子写：先写临时文件再整体替换，避免 UI 线程在覆盖期间读到半截 JPEG。
                tempPath = avatarPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(tempPath, bytes);
                File.Move(tempPath, avatarPath, overwrite: true);
            }

            return avatarPath;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            TryDeleteFile(tempPath);
            return null;
        }
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // 清理临时文件失败不影响主流程。
        }
    }

    // JPEG(FF D8 FF) / PNG(89 50 4E 47) / GIF(47 49 46)——头像 CDN 只会返回这三类图片。
    private static bool LooksLikeImage(byte[] bytes) =>
        bytes.Length >= 4 &&
        ((bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) ||
         (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) ||
         (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46));

    private static string GetSafeAvatarKey(string steamId)
    {
        var value = steamId;
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(value) ? "unknown" : value;
    }
}

/// <summary>一次抓取到的账号资料。头像按需从磁盘解码（不长期持有文件句柄）。</summary>
internal sealed class SteamProfileInfo
{
    private const int AvatarDecodePixelWidth = 96;

    private BitmapImage? _avatarImage;
    private bool _avatarLoaded;

    public SteamProfileInfo(string steamId, string? personaName, string? avatarPath)
    {
        SteamId = steamId;
        PersonaName = personaName;
        AvatarPath = avatarPath;
    }

    public string SteamId { get; }

    public string? PersonaName { get; }

    public string? AvatarPath { get; }

    public BitmapImage? AvatarImage
    {
        get
        {
            if (_avatarLoaded)
            {
                return _avatarImage;
            }

            _avatarLoaded = true;
            _avatarImage = LoadAvatar();
            return _avatarImage;
        }
    }

    private BitmapImage? LoadAvatar()
    {
        var path = AvatarPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            // 从字节解码而非 new BitmapImage(Uri)：后者会长期持有文件句柄。
            // FileShare.ReadWrite：即便文件正被替换也不抛共享冲突导致头像闪失。
            byte[] bytes;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
            }

            var bitmap = new BitmapImage { DecodePixelWidth = AvatarDecodePixelWidth };
            using var memory = new MemoryStream(bytes);
            bitmap.SetSource(memory.AsRandomAccessStream());
            return bitmap;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"解码账号头像失败：{ex.Message}");
            return null;
        }
    }
}
