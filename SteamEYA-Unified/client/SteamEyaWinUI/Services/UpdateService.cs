using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 检查更新：从**自有服务器** <c>GET /api/update/latest</c> 取最新版本。
/// 私有仓库的 Actions 产物需要登录才能下载且会过期，无法直接对用户分发，故更新源改为自建服务器。
/// </summary>
internal sealed class UpdateService
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>当前程序版本（取程序集 InformationalVersion，构建时由 CI 注入 1.0.x）。</summary>
    public static string CurrentVersion { get; } = GetCurrentVersion();

    public async Task<AppUpdateInfo> CheckLatestAsync(CancellationToken cancellationToken = default)
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
    {
        return TryParseVersion(latestVersion, out var latest) &&
            TryParseVersion(currentVersion, out var current) &&
            latest.CompareTo(current) > 0;
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
