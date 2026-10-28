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

    /// <summary>查「最新 tag」用：不许自动跟随 302，否则会顺着跳转去下载整页 HTML。</summary>
    private static readonly HttpClient NoRedirectClient = CreateNoRedirectClient();

    private const string RepoSlug = "lanyyyd/shenmi";

    private const string GitHubApiUrl = "https://api.github.com/repos/lanyyyd/shenmi/releases?per_page=100";

    /// <summary>
    /// 内置 GitHub 加速源前缀：用法是直接拼在原始链接前面，
    /// 例 https://gh.dayiu.cn/https://github.com/.../TeaTT-1.0.30-win-x64-setup.exe
    /// 用户在设置页可增删/拖拽排序，见 <see cref="GetConfiguredMirrors"/>。
    /// </summary>
    internal static readonly string[] DefaultMirrors =
    [
        "https://gh.dayiu.cn/",
        "https://github.tbap.top/",
        "https://gh-proxy.com/"
    ];

    /// <summary>测速用的探针地址（各加速源前缀都会拼上它）。</summary>
    internal const string GitHubReleaseLatestUrl = "https://github.com/lanyyyd/shenmi/releases/latest";

    /// <summary>
    /// 能代理 api.github.com 的加速源（gh.dayiu.cn 只代理下载，实测 API 返回 404）。
    /// 国内直连 api.github.com 基本不通，所以检查更新也要走加速，否则永远发现不了 GitHub 新版本。
    /// </summary>
    private static readonly string[] ApiMirrors =
    [
        "https://gh-proxy.com/",
        "https://github.tbap.top/"
    ];

    /// <summary>探测加速源时最多等多久。</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>多路并发查 API 的总窗口：超过就改用「最新 tag」兜底，避免启动检查卡住。</summary>
    private static readonly TimeSpan ApiRaceTimeout = TimeSpan.FromSeconds(8);

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

    /// <summary>把下载地址换成按用户排序选出的加速源（只对 github.com 链接生效），并记录源主机名。</summary>
    private static async Task<AppUpdateInfo> UseGitHubAsync(AppUpdateInfo github, CancellationToken cancellationToken)
    {
        var (url, mirror) = await ChooseMirrorAsync(github.Url, cancellationToken);
        return string.Equals(url, github.Url, StringComparison.Ordinal)
            ? github
            : github with
            {
                Url = url,
                Mirror = mirror,
                MirrorLabel = mirror is null ? null : MirrorHost(mirror)
            };
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

    /// <summary>
    /// 读 GitHub Releases，取桌面端（tag 形如 v1.0.30）里版本最高的一个。
    /// API 优先（能拿说明/大小/sha256），但**并发走加速源**；全部失败时退回「最新 tag」探测。
    /// </summary>
    private static async Task<AppUpdateInfo?> CheckGitHubAsync(CancellationToken cancellationToken)
    {
        var releases = await FetchReleasesAsync(cancellationToken);
        if (releases is not null)
        {
            return BuildFromReleases(releases);
        }

        // API 不可用（国内直连不通 / 加速源被限流）时的兜底：
        // 加速源上的 releases/latest 会 302 到 tag 页，只读 Location 头就能拿到最新版本号。
        var tag = await ProbeLatestTagAsync(cancellationToken);
        if (tag is not { } latest)
        {
            return null;
        }

        var version = NormalizeVersion(latest.Tag);
        // 资产名由 CI 固定生成：TeaTT-<版本>-win-x64-setup.exe
        var fileName = $"TeaTT-{version}-win-x64-setup.exe";
        return new AppUpdateInfo(
            CurrentVersion,
            version,
            string.Empty,
            0,
            0,
            fileName,
            $"https://github.com/{RepoSlug}/releases/download/{latest.Tag}/{fileName}",
            string.Empty,
            true,
            IsNewerVersion(version, CurrentVersion),
            DateTimeOffset.Now);
    }

    /// <summary>直连 + 各 API 加速源并发请求，谁先成功用谁；都失败返回 null。</summary>
    private static async Task<List<GitHubReleaseDto>?> FetchReleasesAsync(CancellationToken cancellationToken)
    {
        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        raceCts.CancelAfter(ApiRaceTimeout);

        var urls = new List<string> { GitHubApiUrl };
        urls.AddRange(ApiMirrors.Select(m => m + GitHubApiUrl));

        var pending = urls.Select(u => FetchReleasesOneAsync(u, raceCts.Token)).ToList();
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending);
            pending.Remove(done);
            var list = await done;
            if (list is { Count: > 0 })
            {
                return list;
            }
        }

        return null;
    }

    private static async Task<List<GitHubReleaseDto>?> FetchReleasesOneAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await GitHubClient.SendAsync(req, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync(
                stream, GitHubJsonContext.Default.ListGitHubReleaseDto, cancellationToken);
        }
        catch
        {
            // 超时 / 断网 / 403 限流返回的对象结构不同：都交给下一条线路。
            return null;
        }
    }

    /// <summary>并发探测各加速源的 releases/latest，取最先返回 302 的那个 tag。</summary>
    private static async Task<(string Tag, long ElapsedMs)?> ProbeLatestTagAsync(CancellationToken cancellationToken)
    {
        var pending = GetConfiguredMirrors()
            .Select(m => ProbeTagAsync(m + GitHubReleaseLatestUrl, cancellationToken))
            .ToList();

        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending);
            pending.Remove(done);
            if (await done is { } result)
            {
                return result;
            }
        }

        return null;
    }

    private static async Task<(string Tag, long ElapsedMs)?> ProbeTagAsync(string url, CancellationToken cancellationToken)
    {
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCts.CancelAfter(ProbeTimeout);

        var watch = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await NoRedirectClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, probeCts.Token);
            watch.Stop();

            if ((int)resp.StatusCode is not (301 or 302 or 303 or 307 or 308))
            {
                return null;
            }

            // Location 形如 https://gh.dayiu.cn/https://github.com/.../releases/tag/v1.0.31
            var location = resp.Headers.Location;
            var segments = location?.ToString().TrimEnd('/').Split('/');
            if (segments is null || segments.Length == 0)
            {
                return null;
            }

            var tag = segments[^1];
            return tag.Length >= 2 && (tag[0] is 'v' or 'V') ? (tag, watch.ElapsedMilliseconds) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>从 API 返回的 release 列表里挑版本最高的桌面端，并解析出安装包信息。</summary>
    private static AppUpdateInfo? BuildFromReleases(List<GitHubReleaseDto> releases)
    {
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

    /// <summary>
    /// 按用户排序挑加速源：并发探测，取**顺序最靠前**的可达源（测速后最快的已排到最前）。
    /// 全部失败时退回列表首位。
    /// </summary>
    private static async Task<(string Url, string? Mirror)> ChooseMirrorAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return (url, null);
        }

        var mirrors = GetConfiguredMirrors();
        var probes = await Task.WhenAll(mirrors.Select(m => ProbeAsync(m + url, cancellationToken)));
        for (var i = 0; i < probes.Length; i++)
        {
            if (probes[i].Ok)
            {
                return (probes[i].Url, mirrors[i]);
            }
        }

        return (mirrors[0] + url, mirrors[0]);
    }

    /// <summary>测速：HEAD 一次 releases/latest，返回耗时；不可达返回 null。</summary>
    internal static async Task<long?> ProbeMirrorLatencyAsync(string mirrorPrefix, CancellationToken cancellationToken)
    {
        var result = await ProbeAsync(mirrorPrefix + GitHubReleaseLatestUrl, cancellationToken);
        return result.Ok ? result.ElapsedMs : null;
    }

    /// <summary>读取用户配置的镜像源（按排序）；未配置或全部非法时回退内置默认列表。</summary>
    internal static IReadOnlyList<string> GetConfiguredMirrors()
    {
        try
        {
            var configured = NormalizeMirrors(AppState.SettingsService.Load().UpdateMirrors);
            return configured.Count > 0 ? configured : DefaultMirrors;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取镜像源配置失败，改用默认列表：{ex.Message}");
            return DefaultMirrors;
        }
    }

    /// <summary>规整镜像源：去空白、补尾斜杠、只保留 http(s)、按忽略大小写去重。</summary>
    internal static List<string> NormalizeMirrors(IEnumerable<string>? raw)
    {
        var result = new List<string>();
        if (raw is null)
        {
            return result;
        }

        foreach (var item in raw)
        {
            var value = item?.Trim();
            if (string.IsNullOrEmpty(value) ||
                (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                 !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!value.EndsWith('/'))
            {
                value += "/";
            }

            if (!Uri.TryCreate(value, UriKind.Absolute, out _))
            {
                continue;
            }

            if (result.Any(existing => string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            result.Add(value);
        }

        return result;
    }

    /// <summary>取镜像源主机名，用于「正在使用哪个源」的提示文案。</summary>
    internal static string MirrorHost(string mirrorPrefix) =>
        Uri.TryCreate(mirrorPrefix, UriKind.Absolute, out var uri) ? uri.Host : mirrorPrefix;

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

    private static HttpClient CreateNoRedirectClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
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
