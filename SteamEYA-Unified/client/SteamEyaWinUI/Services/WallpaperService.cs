using System.Net.Http;
using System.Text.Json;

namespace SteamEyaWinUI.Services;

/// <summary>服务端壁纸库中的一张壁纸。</summary>
internal sealed class WallpaperItem
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;

    /// <summary>服务端返回的相对地址，如 /uploads/wallpapers/xxx.jpg。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>补全协议与域名后的完整地址，用于预览和下载。</summary>
    public string FullUrl => Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
        ? Url
        : SessionState.BaseUrl.TrimEnd('/') + Url;
}

/// <summary>
/// 服务端壁纸库：后台统一上传，客户端设置页拉列表、下载到本地后复用原有的自定义背景流程。
/// 接口与 /uploads 静态目录都在当前网关（在线域名或离线本机）下。
/// </summary>
internal static class WallpaperService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>拉取壁纸列表；服务端不可用时返回空列表。</summary>
    public static async Task<List<WallpaperItem>> ListAsync(CancellationToken cancellationToken = default)
    {
        var url = SessionState.BaseUrl.TrimEnd('/') + "/api/wallpapers";
        using var response = await Http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        var items = new List<WallpaperItem>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("wallpapers", out var arr))
        {
            return items;
        }
        foreach (var el in arr.EnumerateArray())
        {
            items.Add(new WallpaperItem
            {
                Id = el.TryGetProperty("id", out var id) ? id.GetInt32() : 0,
                Title = el.TryGetProperty("title", out var t) ? (t.GetString() ?? string.Empty) : string.Empty,
                Url = el.TryGetProperty("url", out var u) ? (u.GetString() ?? string.Empty) : string.Empty
            });
        }
        return items;
    }

    /// <summary>下载到临时文件，返回本地路径（由调用方导入后自行清理）。</summary>
    public static async Task<string> DownloadAsync(WallpaperItem item, CancellationToken cancellationToken = default)
    {
        var bytes = await Http.GetByteArrayAsync(item.FullUrl, cancellationToken);
        var extension = Path.GetExtension(new Uri(item.FullUrl).AbsolutePath);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 6)
        {
            extension = ".jpg";
        }
        var tempPath = Path.Combine(Path.GetTempPath(), "eya-wallpaper-" + Guid.NewGuid().ToString("N") + extension);
        await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken);
        return tempPath;
    }
}
