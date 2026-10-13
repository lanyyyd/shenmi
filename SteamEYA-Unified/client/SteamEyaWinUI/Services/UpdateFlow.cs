using Microsoft.UI.Xaml.Controls;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 「立即更新」流程：下载安装包 → 启动安装器 → 关闭自身进程。
/// 安装包**保留**在 %TEMP%\SteamEYA\updates\&lt;版本&gt;\，方便用户以后再次手动运行。
/// </summary>
internal static class UpdateFlow
{
    public static async Task StartAsync(AppUpdateInfo info, Action closeApp)
    {
        AppState.ShowStatus(Loc.T("About_Update_Downloading"), InfoBarSeverity.Informational);
        var progress = new Progress<UpdateDownloadProgress>(
            p => AppState.ShowStatus(Describe(p), InfoBarSeverity.Informational));

        try
        {
            var path = await AppState.UpdateInstallerService.DownloadInstallerAsync(info, progress);
            AppState.ShowStatus(Loc.T("About_Update_Downloaded"), InfoBarSeverity.Success);
            if (!AppState.UpdateInstallerService.LaunchInstaller(path))
            {
                AppState.ShowStatus(Loc.T("About_Update_InstallerLaunchFailed"), InfoBarSeverity.Error);
                return;
            }
            // 安装包已启动（Inno 参数会让其自动关闭本进程占用的文件）；随后关闭自身让安装器接管。
            closeApp();
        }
        catch (Exception ex)
        {
            AppState.ShowStatus(Loc.Tf("About_Update_DownloadFailed_Format", ex.Message), InfoBarSeverity.Error);
        }
    }

    private static string Describe(UpdateDownloadProgress p) => p.TotalBytes is > 0
        ? Loc.Tf("About_Update_DownloadProgress_Format",
            (int)(p.BytesReceived * 100 / p.TotalBytes.Value),
            FormatBytes(p.BytesReceived), FormatBytes(p.TotalBytes.Value))
        : Loc.T("About_Update_DownloadProgress_Unknown");

    internal static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }

    internal static string FormatTime(long ms)
    {
        return ms > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : "--";
    }
}
