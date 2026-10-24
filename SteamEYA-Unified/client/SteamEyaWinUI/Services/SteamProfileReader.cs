using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.UI.Xaml.Media.Imaging;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 只读的 Steam 资料读取器：按 SteamID 取昵称与头像。
///
/// 数据来源按可用性依次降级：
///   1. 进程内缓存（5 分钟）
///   2. steamcommunity.com 的 ?xml=1 接口（实时）
///   3. %AppData%\SteamEYA\profile-cache.json（上一次成功抓取的结果）
///   4. 本地头像文件（avatars\ 与历史遗留的 cached-avatars\）
///   5. Steam 本地 userdata\&lt;id3&gt;\config\localconfig.vdf 里的 PersonaName
///
/// 为什么要这么多层：国内网络访问 steamcommunity.com 经常直接连不上（DNS 能解析、TCP 超时），
/// 而昵称/头像一旦拿不到，登录页右侧面板就只剩账号名，看起来像「功能坏了」。
/// 有了 3~5 层兜底，即使完全断网，老账号也能显示上次的昵称和头像。
/// </summary>
internal static class SteamProfileReader
{
    private const string AppFolderName = "SteamEYA";
    private const string AvatarFolderName = "avatars";

    /// <summary>已缓存账号页用的旧头像目录，历史版本下过文件，留着当兜底。</summary>
    private const string LegacyAvatarFolderName = "cached-avatars";

    private const string CacheFileName = "profile-cache.json";

    /// <summary>SteamID64 的个人账号基数，减去它得到 userdata 目录名（32 位 ID）。</summary>
    private const ulong IndividualAccountBase = 76561197960265728UL;

    /// <summary>内存缓存有效期：同一账号短时间内重复上号不再重复请求 Steam 社区。</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>失败后的负缓存：避免连点重试把社区页打爆。</summary>
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromSeconds(30);

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, SteamProfileInfo? Info)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object AvatarGate = new();
    private static readonly object DiskCacheGate = new();

    private static Dictionary<string, ProfileCacheEntry>? _diskCache;

    public static string AvatarFolderPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppFolderName,
        AvatarFolderName);

    private static string LegacyAvatarFolderPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppFolderName,
        LegacyAvatarFolderName);

    private static string CacheFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppFolderName,
        CacheFileName);

    /// <summary>读取某账号的昵称与头像；失败也尽量返回部分信息，完全拿不到才返回 null。</summary>
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

        // 网络这条路没拿到东西：用本地缓存/Steam 自有配置兜底，保证面板不至于空着。
        info ??= await ReadOfflineAsync(steamId, cancellationToken);

        Cache[steamId] = (DateTimeOffset.UtcNow, info);
        return info;
    }

    /// <summary>网络失败时的兜底：磁盘缓存 → 本地头像文件 → Steam 本地昵称。</summary>
    private static Task<SteamProfileInfo?> ReadOfflineAsync(string steamId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entry = TryGetCacheEntry(steamId);
        var persona = !string.IsNullOrWhiteSpace(entry?.Persona)
            ? entry!.Persona
            : TryReadPersonaFromSteamConfig(steamId);

        // 缓存里记的路径可能已被用户清掉（或换过盘符），文件不存在就再按文件名找一遍。
        string? avatarPath = null;
        if (!string.IsNullOrWhiteSpace(entry?.AvatarPath) && File.Exists(entry!.AvatarPath))
        {
            avatarPath = entry.AvatarPath;
        }

        avatarPath ??= FindLocalAvatar(steamId);

        if (string.IsNullOrWhiteSpace(persona) && avatarPath is null)
        {
            AppLog.Debug($"Steam 资料无网络也无本地缓存（{steamId}）。");
            return Task.FromResult<SteamProfileInfo?>(null);
        }

        AppLog.Debug($"Steam 资料走本地兜底（{steamId}）：昵称={(string.IsNullOrWhiteSpace(persona) ? "无" : persona)}，头像={(avatarPath is null ? "无" : "有")}");
        return Task.FromResult<SteamProfileInfo?>(new SteamProfileInfo(steamId, persona, avatarPath));
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

        // 抓到就落盘：下次网络不通时能直接拿来用。
        SaveCacheEntry(steamId, personaName, avatarPath);

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

    // ---------------- 本地兜底 ----------------

    private static string? FindLocalAvatar(string steamId)
    {
        var key = GetSafeAvatarKey(steamId);
        var candidates = new[]
        {
            Path.Combine(AvatarFolderPath, key + ".jpg"),
            Path.Combine(LegacyAvatarFolderPath, key + ".jpg")
        };

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // 权限问题当作不存在。
            }
        }

        return null;
    }

    /// <summary>
    /// 从 Steam 自己的 userdata\&lt;id3&gt;\config\localconfig.vdf 读 PersonaName。
    /// 这份文件由 Steam 客户端维护，不联网也准确，是断网时唯一可靠的昵称来源。
    /// </summary>
    private static string? TryReadPersonaFromSteamConfig(string steamId)
    {
        try
        {
            var installPath = SteamPathCoordinator.TryResolveInstallPath();
            if (string.IsNullOrWhiteSpace(installPath))
            {
                return null;
            }

            if (!ulong.TryParse(steamId, out var id64) || id64 < IndividualAccountBase)
            {
                return null;
            }

            var id3 = (id64 - IndividualAccountBase).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var path = Path.Combine(installPath, "userdata", id3, "config", "localconfig.vdf");
            if (!File.Exists(path))
            {
                return null;
            }

            var document = VdfDocument.LoadOrEmpty(path);
            var store = VdfDocument.GetObject(document, "UserLocalConfigStore");
            if (store is null)
            {
                return null;
            }

            var friends = VdfDocument.GetObject(store, "friends");
            var name = friends is null ? null : VdfDocument.GetString(friends, "PersonaName");
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch (Exception ex)
        {
            AppLog.Debug($"读取 localconfig.vdf 昵称失败（{steamId}）：{ex.Message}");
            return null;
        }
    }

    // ---------------- 磁盘缓存 ----------------

    private static ProfileCacheEntry? TryGetCacheEntry(string steamId)
    {
        lock (DiskCacheGate)
        {
            var cache = EnsureDiskCacheLoaded();
            return cache.TryGetValue(steamId, out var entry) ? entry : null;
        }
    }

    private static void SaveCacheEntry(string steamId, string? personaName, string? avatarPath)
    {
        if (string.IsNullOrWhiteSpace(personaName) && string.IsNullOrWhiteSpace(avatarPath))
        {
            return;
        }

        lock (DiskCacheGate)
        {
            var cache = EnsureDiskCacheLoaded();
            cache[steamId] = new ProfileCacheEntry
            {
                Persona = personaName,
                AvatarPath = avatarPath,
                UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CacheFilePath)!);
                var json = JsonSerializer.Serialize(cache, ProfileCacheJsonContext.Default.DictionaryStringProfileCacheEntry);
                File.WriteAllText(CacheFilePath, json);
            }
            catch (Exception ex)
            {
                // 缓存写失败不影响本次展示。
                AppLog.Debug($"写入资料缓存失败：{ex.Message}");
            }
        }
    }

    private static Dictionary<string, ProfileCacheEntry> EnsureDiskCacheLoaded()
    {
        if (_diskCache is not null)
        {
            return _diskCache;
        }

        try
        {
            if (File.Exists(CacheFilePath))
            {
                var json = File.ReadAllText(CacheFilePath);
                _diskCache = JsonSerializer.Deserialize(json, ProfileCacheJsonContext.Default.DictionaryStringProfileCacheEntry) ?? [];
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug($"读取资料缓存失败：{ex.Message}");
        }

        _diskCache ??= new Dictionary<string, ProfileCacheEntry>(StringComparer.OrdinalIgnoreCase);
        return _diskCache;
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

/// <summary>落盘的一条资料：昵称与头像路径，供网络不可用时兜底。</summary>
internal sealed class ProfileCacheEntry
{
    [JsonPropertyName("persona")]
    public string? Persona { get; set; }

    [JsonPropertyName("avatar")]
    public string? AvatarPath { get; set; }

    [JsonPropertyName("at")]
    public long UpdatedAt { get; set; }
}

[JsonSerializable(typeof(Dictionary<string, ProfileCacheEntry>))]
internal sealed partial class ProfileCacheJsonContext : JsonSerializerContext;

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
