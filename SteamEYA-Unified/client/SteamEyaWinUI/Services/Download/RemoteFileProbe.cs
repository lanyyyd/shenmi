using System.Net;
using System.Net.Http.Headers;

namespace SteamEyaWinUI.Services;

/// <summary>探测到的远端文件信息。</summary>
internal sealed record RemoteFileInfo(
    string FinalUrl,
    string FileName,
    long TotalBytes,
    bool SupportsRange,
    string? ETag,
    string? LastModified,
    string? ContentType);

/// <summary>
/// 下载前的远端探测：用 <c>GET Range: bytes=0-0</c> 拿最终 URL、大小、是否支持分片与文件名。
///
/// 为什么不用 HEAD：相当多站点对 HEAD 返回 405/403，而 Range 请求与真实下载行为一致；
/// 响应体只有 1 字节且用 ResponseHeadersRead，不会把整个文件拉下来。
/// </summary>
internal static class RemoteFileProbe
{
    /// <summary>与下载引擎共用连接池：分片下载需要远超默认的并发连接数。</summary>
    public static readonly HttpClient Http = CreateClient();

    private static readonly Dictionary<string, string> ExtensionByContentType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/zip"] = ".zip",
        ["application/x-zip-compressed"] = ".zip",
        ["application/x-7z-compressed"] = ".7z",
        ["application/x-rar-compressed"] = ".rar",
        ["application/vnd.rar"] = ".rar",
        ["application/x-msdownload"] = ".exe",
        ["application/octet-stream"] = ".bin",
        ["application/pdf"] = ".pdf",
        ["application/vnd.android.package-archive"] = ".apk",
        ["application/json"] = ".json",
        ["application/x-tar"] = ".tar",
        ["application/gzip"] = ".gz",
        ["video/mp4"] = ".mp4",
        ["video/x-matroska"] = ".mkv",
        ["audio/mpeg"] = ".mp3",
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/gif"] = ".gif",
        ["text/plain"] = ".txt"
    };

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            // 64 线程下载必须放开单服务器连接上限（默认值虽然很大，但显式设定避免策略变化）。
            MaxConnectionsPerServer = 512,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.None   // 分片偏移必须对应原始字节
        };

        var client = new HttpClient(handler)
        {
            // 只约束到响应头阶段；响应体的空闲超时由下载引擎自己看门（见 DownloadEngine）。
            Timeout = TimeSpan.FromSeconds(30)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) SteamEYA/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }

    public static async Task<RemoteFileInfo> ProbeAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("InvalidUrl");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new RangeHeaderValue(0, 0);

        using var response = await Http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // 416 通常意味着文件为空或服务端拒绝 Range；再发一次不带 Range 的请求确认大小。
            return await ProbeWithoutRangeAsync(uri, cancellationToken);
        }

        response.EnsureSuccessStatusCode();

        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
        var contentRange = response.Content.Headers.ContentRange;
        var totalBytes = contentRange?.Length
                         ?? response.Content.Headers.ContentLength
                         ?? -1;

        // 只有返回 206 且拿到完整长度才认为支持分片；200 表示服务端忽略了 Range。
        var supportsRange = response.StatusCode == HttpStatusCode.PartialContent &&
                            contentRange?.Length is > 0;

        var contentType = response.Content.Headers.ContentType?.MediaType;
        var fileName = ResolveFileName(
            response.Content.Headers.ContentDisposition,
            finalUrl,
            contentType);

        return new RemoteFileInfo(
            finalUrl,
            fileName,
            totalBytes,
            supportsRange,
            response.Headers.ETag?.Tag,
            response.Content.Headers.LastModified?.ToString("R"),
            contentType);
    }

    private static async Task<RemoteFileInfo> ProbeWithoutRangeAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? uri.ToString();
        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        var contentType = response.Content.Headers.ContentType?.MediaType;

        return new RemoteFileInfo(
            finalUrl,
            ResolveFileName(response.Content.Headers.ContentDisposition, finalUrl, contentType),
            totalBytes,
            false,
            response.Headers.ETag?.Tag,
            response.Content.Headers.LastModified?.ToString("R"),
            contentType);
    }

    /// <summary>文件名优先级：Content-Disposition → 最终 URL 路径 → Content-Type 推断 → 时间戳。</summary>
    private static string ResolveFileName(
        ContentDispositionHeaderValue? disposition,
        string finalUrl,
        string? contentType)
    {
        // .NET 已按 RFC 5987 解析 filename*（中文名会正确解出），FileName 是去引号后的结果。
        var fromHeader = disposition?.FileNameStar ?? disposition?.FileName;
        if (!string.IsNullOrWhiteSpace(fromHeader))
        {
            var cleaned = fromHeader.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                return DownloadPaths.SanitizeFileName(Uri.UnescapeDataString(cleaned));
            }
        }

        if (Uri.TryCreate(finalUrl, UriKind.Absolute, out var uri))
        {
            var segment = uri.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault();
            if (!string.IsNullOrWhiteSpace(segment))
            {
                var decoded = Uri.UnescapeDataString(segment);
                if (decoded.Contains('.'))
                {
                    return DownloadPaths.SanitizeFileName(decoded);
                }

                // 路径里没有扩展名时用 Content-Type 补，避免得到一个无后缀文件。
                return DownloadPaths.SanitizeFileName(decoded + GuessExtension(contentType));
            }
        }

        return DownloadPaths.SanitizeFileName(null, GuessExtension(contentType));
    }

    private static string GuessExtension(string? contentType) =>
        contentType is not null && ExtensionByContentType.TryGetValue(contentType, out var extension)
            ? extension
            : ".bin";
}
