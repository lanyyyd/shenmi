namespace SteamEyaWinUI.Models;

/// <summary>
/// 自有服务器下发的更新信息（GET /api/update/latest）。
/// <see cref="Url"/> 为相对路径（如 /api/update/download/12），下载时由客户端拼上服务器地址。
/// </summary>
internal sealed record AppUpdateInfo(
    string CurrentVersion,
    string Version,
    string Notes,
    long FileSize,
    long UpdatedAt,
    string FileName,
    string Url,
    string Sha256,
    bool Available,
    bool HasUpdate,
    DateTimeOffset CheckedAt)
{
    /// <summary>本次下载使用的加速源前缀（含尾斜杠）；null 表示自有服务器或直连 GitHub。</summary>
    public string? Mirror { get; init; }

    /// <summary>加速源主机名（用于下载提示文案，如 gh.dayiu.cn）。</summary>
    public string? MirrorLabel { get; init; }
}
