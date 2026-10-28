using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 检查更新：先看**自有服务器** <c>GET /api/update/latest</c>，再看 **GitHub Releases**。
/// 两边版本一致时优先走服务器下载（更稳、有 sha256）；GitHub 版本更高时才走 GitHub，
/// 并把下载链接接到自动测速选出的加速源上（国内直连 github.com 经常不通）。
/// </summary>
internal sealed class UpdateService
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static readonly HttpClient GitHubClient = CreateGitHubClient();

    private const string GitHubApiUrl = "https://api.github.com/repos/lanyyyd/shenmi/releases?per_page=100";

    /// <summary>
    /// GitHub 加速源前缀：用法是直接拼在原始下载链接前面，
    /// 例 https://gh.dayiu.cn/https://github.com/.../TeaTT-1.0.30-win-x64-setup.exe
    /// </summary>
    private static readonly string[] Mirrors =
    [
        "https://gh.dayiu.cn/",
        "https://github.tbap.top/",
        "https://gh-proxy.com/"
    ];

    /// <summary>探测加速源时最多等多久。</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>当前程序版本（取程序集 InformationalVersion，构建时由 CI 注入 1.0.x）。</summary>
    public static string CurrentVersion { get; } = GetCurrentVersion();

    public async Task<AppUpdateInfo> CheckLatestAsync(CancellationToken cancellationToken = default)
    {
        // 顺序按需求：先查服务器，再查 GitHub。
        Exception? serverError = null;
        AppUpdateInfo? server = null;
        try
        {
            server = await CheckServerAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            serverError = ex;
        }

        AppUpdateInfo? github = null;
        try
        {
            github = await CheckGitHubAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn($"检查 GitHub 版本失败：{ex.Message}");
        }

        if (server is null && github is null)
        {
            throw serverError ?? new InvalidOperationException(Loc.T("Update_EmptyResponse"));
        }

        if (server is null)
        {
            return await UseGitHubAsync(github!, cancellationToken);
        }

        // 版本一致（或服务器更高）都用服务器，GitHub 只补一条更新说明。
        if (github is null || CompareVersions(github.Version, server.Version) <= 0)
        {
            return string.IsNullOrWhiteSpace(server.Notes) && github is not null
                ? server with { Notes = github.Notes }
                : server;
        }

        return await UseGitHubAsync(github, cancellationToken);
    }

    /// <summary>把下载地址换成测速选出的加速源（只对 github.com 链接生效）。</summary>
    private static async Task<AppUpdateInfo> UseGitHubAsync(AppUpdateInfo github, CancellationToken cancellationToken)
    {
        var url = await ChooseFastestMirrorAsync(github.Url, cancellationToken);
        return string.Equals(url, github.Url, StringComparison.Ordinal) ? github : github with { Url = url };
    }

    private static async Task<AppUpdateInfo> CheckServerAsync(CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Get, SessionState.OnlineBaseUrl.TrimEnd('/') + "/api/update/latest");
        if (!string.IsNullOrWhiteSpace(SessionState.Token))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SessionState.Token);
        }

        using var resp = await HttpClient.SendAsync(req, cancellationToken);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        var dto = await JsonSerializer.DeserializeAsync(stream, CloudApiJsonContext.Default.UpdateLatestDto, cancellationToken)
                  ?? throw new InvalidOperationException(Loc.T("Update_EmptyResponse"));

        var currentVersion = CurrentVersion;
        var latestVersion = NormalizeVersion(dto.Version);
        var hasUpdate = dto.Available
                       && !string.IsNullOrWhiteSpace(dto.Version)
                       && IsNewerVersion(latestVersion, currentVersion);

        return new AppUpdateInfo(
            currentVersion,
            latestVersion,
            dto.Notes ?? string.Empty,
            dto.FileSize,
            dto.UpdatedAt,
            dto.FileName ?? string.Empty,
            dto.Url ?? string.Empty,
            dto.Sha256 ?? string.Empty,
            dto.Available,
            hasUpdate,
            DateTimeOffset.Now);
    }

    /// <summary>读 GitHub Releases，取桌面端（tag 形如 v1.0.30）里版本最高的一个。</summary>
    private static async Task<AppUpdateInfo?> CheckGitHubAsync(CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, GitHubApiUrl);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await GitHubClient.SendAsync(req, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            // 403 限流 / 网络不通：交给服务器源，不影响检查更新。
            return null;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        var releases = await JsonSerializer.DeserializeAsync(
            stream, GitHubJsonContext.Default.ListGitHubReleaseDto, cancellationToken);
        if (releases is null)
        {
            return null;
        }

        GitHubReleaseDto? best = null;
        Version? bestVersion = null;
        foreach (var release in releases)
        {
            // 安卓端 tag 是 android-v1.0.x，桌面端才是 v1.0.x，这里只认后者。
            if (release.TagName.Length < 2 || (release.TagName[0] != 'v' && release.TagName[0] != 'V'))
            {
                continue;
            }
            if (!TryParseVersion(release.TagName, out var version))
            {
                continue;
            }
            if (bestVersion is null || version.CompareTo(bestVersion) > 0)
            {
                bestVersion = version;
                best = release;
            }
        }

        if (best is null)
        {
            return null;
        }

        var asset = best.Assets.FirstOrDefault(a =>
                        a.Name.EndsWith("-win-x64-setup.exe", StringComparison.OrdinalIgnoreCase))
                    ?? best.Assets.FirstOrDefault(a =>
                        a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        if (asset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
        {
            return null;
        }

        var latestVersion = NormalizeVersion(best.TagName);

        // GitHub 从 2024 起给资产附带 digest: "sha256:..."，有就照常校验。
        var sha256 = asset.Digest is { Length: > 7 } digest &&
                     digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? digest[7..]
            : string.Empty;

        return new AppUpdateInfo(
            CurrentVersion,
            latestVersion,
            best.Body ?? string.Empty,
            asset.Size,
            ParseIsoToUnixMs(best.PublishedAt),
            asset.Name,
            asset.BrowserDownloadUrl,
            sha256,
            true,
            IsNewerVersion(latestVersion, CurrentVersion),
            DateTimeOffset.Now);
    }

    /// <summary>三路并发探测，取响应最快的加速源；全失败就用主源（直连 GitHub 在国内基本不可用）。</summary>
    private static async Task<string> ChooseFastestMirrorAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var probes = await Task.WhenAll(Mirrors.Select(m => ProbeAsync(m + url, cancellationToken)));
        var fastest = probes.Where(p => p.Ok).MinBy(p => p.ElapsedMs);
        return fastest.Ok ? fastest.Url : Mirrors[0] + url;
    }

    private static async Task<(bool Ok, long ElapsedMs, string Url)> ProbeAsync(string url, CancellationToken cancellationToken)
    {
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCts.CancelAfter(ProbeTimeout);

        // HEAD 只测「多久能拿到响应头」，不会真的拉安装包。
        var watch = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await GitHubClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, probeCts.Token);
            watch.Stop();

            // 有的加速源不支持 HEAD（405/403），但真实 GET 是通的，也算可达。
            var ok = resp.IsSuccessStatusCode
                     || resp.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.Forbidden;
            return (ok, watch.ElapsedMilliseconds, url);
        }
        catch
        {
            return (false, long.MaxValue, url);
        }
    }

    private static HttpClient CreateGitHubClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SteamEYA-Updater");
        return client;
    }

    /// <summary>把更新包下载地址补成绝对 URL（服务器返回的是相对路径）。</summary>
    public static string ResolveDownloadUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }
        return url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? url
            : SessionState.OnlineBaseUrl.TrimEnd('/') + url;
    }

    private static string GetCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            return NormalizeVersion(informational);
        }

        var version = assembly.GetName().Version;
        return version is null
            ? "0.0.0"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static bool IsNewerVersion(string latestVersion, string currentVersion)
        => CompareVersions(latestVersion, currentVersion) > 0;

    private static int CompareVersions(string left, string right)
    {
        return TryParseVersion(left, out var a) && TryParseVersion(right, out var b)
            ? a.CompareTo(b)
            : 0;
    }

    private static long ParseIsoToUnixMs(string? value)
    {
        return DateTimeOffset.TryParse(value, out var time) ? time.ToUnixTimeMilliseconds() : 0;
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        version = new Version(0, 0, 0);
        var normalized = NormalizeVersion(value);
        var parts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        while (parts.Length < 3)
        {
            parts = [.. parts, "0"];
        }

        return Version.TryParse(string.Join('.', parts.Take(4)), out version!);
    }

    private static string NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "0.0.0";
        }

        var normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        var suffixIndex = normalized.IndexOfAny(['+', '-']);
        if (suffixIndex >= 0)
        {
            normalized = normalized[..suffixIndex];
        }

        return string.IsNullOrWhiteSpace(normalized) ? "0.0.0" : normalized;
    }
}

/// <summary>GitHub Releases API 里用到的字段（snake_case 需要显式映射）。</summary>
internal sealed class GitHubReleaseDto
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("published_at")]
    public string? PublishedAt { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAssetDto> Assets { get; set; } = [];
}

internal sealed class GitHubAssetDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("digest")]
    public string? Digest { get; set; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(List<GitHubReleaseDto>))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;
