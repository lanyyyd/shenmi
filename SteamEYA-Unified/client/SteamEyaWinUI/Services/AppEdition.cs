namespace SteamEyaWinUI.Services;

/// <summary>
/// 安装形态：发布目录里带 <c>nojiyu</c> 标记文件的是「无极域版」。
/// 无极域版由 CI 用同一份 Native AOT 产物、剔除 jiyu 目录后单独打包，
/// 因此启动后不拉起/不注册极域注入、导航栏不显示「小工具」，
/// 并且检查更新只走 GitHub，下载 nojiyu 安装包。
/// </summary>
internal static class AppEdition
{
    /// <summary>无极域版标记文件名（安装后位于安装目录根下）。</summary>
    private const string NoJiYuMarker = "nojiyu";

    public static bool IsNoJiYu { get; } =
        File.Exists(Path.Combine(AppContext.BaseDirectory, NoJiYuMarker));
}
