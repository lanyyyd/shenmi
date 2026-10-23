using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 应用级设置（语言、主题）的持久化。存于 %AppData%\SteamEYA\settings.json，与账号历史同目录。
/// 读写以同步小文件为主，调用方不多（启动读一次、设置页改动时写），故用简单锁而非账号历史那套文件门。
/// </summary>
internal sealed class SettingsService
{
    private const string AppFolderName = "SteamEYA";
    private const string SettingsFileName = "settings.json";

    private readonly string _settingsFilePath;
    private readonly object _gate = new();

    public SettingsService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        AppFolderPath = Path.Combine(appData, AppFolderName);
        _settingsFilePath = Path.Combine(AppFolderPath, SettingsFileName);
    }

    /// <summary>数据根目录（%AppData%\SteamEYA），供“打开数据目录”使用。</summary>
    public string AppFolderPath { get; }

    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    var json = File.ReadAllText(_settingsFilePath);
                    var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
                    if (settings is not null)
                    {
                        return settings;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("读取应用设置失败，按默认设置处理。", ex);
            }

            return new AppSettings();
        }
    }

    // 自定义背景可选类型：图片（含 GIF）+ 视频（由主窗口用 MediaPlayerElement 静音循环播放）。
    private static readonly HashSet<string> AllowedCustomBackgroundExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp",
            ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv"
        };

    /// <summary>解析可用的自定义背景路径；未设置、文件缺失或设置被篡改时返回 null。</summary>
    public string? GetCustomBackgroundImagePath(AppSettings settings)
    {
        var storedName = settings.CustomBackgroundImageFileName;
        if (string.IsNullOrWhiteSpace(storedName))
        {
            return null;
        }

        var fileName = Path.GetFileName(storedName);
        if (!string.Equals(fileName, storedName, StringComparison.Ordinal) ||
            !AllowedCustomBackgroundExtensions.Contains(Path.GetExtension(fileName)))
        {
            return null;
        }

        var path = Path.Combine(AppFolderPath, "personalization", fileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>把选择器返回的背景文件复制到数据目录，返回持久化的唯一文件名。</summary>
    public string ImportCustomBackgroundImage(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The selected background file does not exist.", sourcePath);
        }

        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!AllowedCustomBackgroundExtensions.Contains(extension))
        {
            throw new InvalidDataException($"Unsupported background file type: {extension}");
        }

        var folder = Path.Combine(AppFolderPath, "personalization");
        Directory.CreateDirectory(folder);
        var fileName = "custom-background-" + Guid.NewGuid().ToString("N") + extension;
        var destinationPath = Path.Combine(folder, fileName);
        var tempPath = destinationPath + "." + Path.GetRandomFileName() + ".tmp";

        try
        {
            File.Copy(sourcePath, tempPath, overwrite: true);
            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // 清理临时文件失败不影响已复制背景的可用性。
            }
        }

        return fileName;
    }

    /// <summary>删除除当前背景外的旧文件；文件被媒体解码器占用时留待下次清理。</summary>
    public void DeleteOtherCustomBackgroundImages(string? keepFileName)
    {
        var folder = Path.Combine(AppFolderPath, "personalization");
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(folder, "custom-background-*"))
        {
            if (!string.IsNullOrWhiteSpace(keepFileName) &&
                string.Equals(Path.GetFileName(path), Path.GetFileName(keepFileName), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            TryDeleteBackgroundFile(path);
        }
    }

    /// <summary>删除所有已保存的自定义背景文件。</summary>
    public void DeleteCustomBackgroundImages()
    {
        var folder = Path.Combine(AppFolderPath, "personalization");
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(folder, "custom-background-*"))
        {
            TryDeleteBackgroundFile(path);
        }
    }

    private static void TryDeleteBackgroundFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"删除自定义背景失败：{ex.Message}");
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            // 先写临时文件再原子替换，避免写入中断留下半截 settings.json。
            var tempPath = _settingsFilePath + "." + Path.GetRandomFileName() + ".tmp";
            try
            {
                Directory.CreateDirectory(AppFolderPath);
                var json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);

                File.WriteAllText(tempPath, json);
                if (File.Exists(_settingsFilePath))
                {
                    File.Replace(tempPath, _settingsFilePath, null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, _settingsFilePath);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("保存应用设置失败。", ex);
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // 清理残留临时文件失败无需上报。
                }
            }
        }
    }
}

internal sealed class AppSettings
{
    /// <summary>界面语言代码：zh-Hans / en / zh-Hant。null 表示尚未选择（首次启动按系统语言推断）。</summary>
    public string? Language { get; set; }

    /// <summary>主题：Default（跟随系统）/ Light / Dark。</summary>
    public string Theme { get; set; } = "Default";

    /// <summary>
    /// 已解析并持久化的 Steam 安装目录（含 steam.exe 的根目录）。首次启动自动检测后写入，之后直接复用，
    /// 不必每次上号重新检测。null/空表示尚未解析；失效（目录里找不到 steam.exe）时会重新自动检测，
    /// 仍找不到则弹框让用户手动选择。详见 <see cref="SteamPathCoordinator"/>。
    /// </summary>
    public string? SteamInstallPath { get; set; }

    /// <summary>
    /// 百宝箱·多线程下载的默认保存目录。null/空表示用安装目录下的 Downloads。
    /// </summary>
    public string? DownloadDirectory { get; set; }

    /// <summary>百宝箱·多线程下载的默认线程数（分片并发上限），范围 1–256，默认 64。</summary>
    public int DownloadThreadCount { get; set; } = DefaultDownloadThreadCount;

    /// <summary>同时进行的下载任务数，默认 1（避免多个任务同时各开 64 连接）。</summary>
    public int DownloadConcurrentTasks { get; set; } = 1;

    public const int DefaultDownloadThreadCount = 64;

    /// <summary>自定义界面主题色，格式 #RRGGBB 或 #AARRGGBB；null 表示使用默认 Aurora 主题色。</summary>
    public string? UiColor { get; set; }

    /// <summary>是否每秒自动旋转界面主题色。</summary>
    public bool UiColorAnimated { get; set; }

    /// <summary>
    /// 聊天室运行模式：Auto（自动：优先完整版，加载失败回退原生）/ Web（完整版，内嵌网页版）/ Native（原生版）。
    /// 系统内嵌网页版（WebView2）不可用时，可在设置里强制改用原生版，避免初始化卡死。
    /// </summary>
    public string ChatMode { get; set; } = "Auto";

    /// <summary>自定义背景默认不透明度。</summary>
    public const double DefaultCustomBackgroundOpacity = 0.4;

    /// <summary>是否启用用户自定义图片/视频背景。</summary>
    public bool CustomBackgroundEnabled { get; set; }

    /// <summary>数据目录 personalization 文件夹内的自定义背景文件名；null 表示尚未设置。</summary>
    public string? CustomBackgroundImageFileName { get; set; }

    /// <summary>自定义背景不透明度，范围 0.1～0.9。</summary>
    public double CustomBackgroundOpacity { get; set; } = DefaultCustomBackgroundOpacity;

    /// <summary>点右上角关闭时的行为："tray"（隐藏到托盘，默认）或 "exit"（直接退出）。</summary>
    public string CloseAction { get; set; } = "tray";

    /// <summary>是否记住关闭选择、不再每次询问。</summary>
    public bool RememberCloseAction { get; set; }

    /// <summary>
    /// 通知发送方式："auto"（默认，浮窗总是弹，窗口不在前台时叠加系统通知）、
    /// "inapp"（只弹应用内浮窗）、"system"（只发系统通知）、"off"（关闭）。
    /// </summary>
    public string NotificationMode { get; set; } = "auto";

    /// <summary>调试模式：开启后额外写入 DEBUG 级别过程日志（通知/下载/聊天链路）。</summary>
    public bool DebugMode { get; set; }

    /// <summary>截图全局热键的修饰键（Win32 MOD_* 位组合），默认 Alt。</summary>
    public uint ScreenshotHotkeyMods { get; set; } = 0x0001;

    /// <summary>截图全局热键的虚拟键码，默认 A。</summary>
    public uint ScreenshotHotkeyVk { get; set; } = 0x41;

    /// <summary>「小工具」页：是否启用极域电子教室绕过（默认开启，客户端启动即注入）。</summary>
    public bool JiYuBypassEnabled { get; set; } = true;

    /// <summary>「小工具」页：是否允许广播窗口置顶（对应 Hook 的 AllowGbTop）。</summary>
    public bool JiYuAllowGbTop { get; set; }

    /// <summary>「小工具」页：是否启用老板键。</summary>
    public bool JiYuBossKeyEnabled { get; set; } = true;

    /// <summary>老板键修饰键（Win32 MOD_* 位组合，Ctrl=2 / Alt=1 / Shift=4 / Win=8）。</summary>
    public uint JiYuBossKeyMods { get; set; } = JiYuService.ModControl | JiYuService.ModAlt;

    /// <summary>老板键虚拟键码（默认 B）。</summary>
    public uint JiYuBossKeyVk { get; set; } = 0x42;
}

// 与账号历史一致用 source generator：JsonSerializerDefaults.Web（camelCase、大小写不敏感），AOT 下可读写。
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
