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

    /// <summary>「个性化」头像的固定落盘路径（裁剪后的 512² JPEG）。</summary>
    public string PersonalizationAvatarPath => Path.Combine(AppFolderPath, "personalization", "avatar.jpg");

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
                        // JSON 里显式的 "groups": null 会覆盖属性初始值，消费方（LoadGroups 等）直接 .Groups 会 NRE。
                        settings.Groups ??= [];
                        // 同理防护配装预设：显式 null 会让配装页导航、一键配装直接 NRE。
                        settings.Loadout ??= CsLoadoutPreset.Default();
                        settings.Loadout.T ??= new Dictionary<uint, uint>();
                        settings.Loadout.Ct ??= new Dictionary<uint, uint>();
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

    /// <summary>唯一的 CS2 配装预设，供装备页面编辑与登录页一键装配。新用户用项目内置默认配装。</summary>
    public CsLoadoutPreset Loadout { get; set; } = CsLoadoutPreset.Default();

    /// <summary>「个性化」面板里设置的昵称，供登录页一键把账号资料设为该值。null/空表示不改昵称。</summary>
    public string? PersonaName { get; set; }

    /// <summary>「个性化」面板里设置的真实姓名（资料页 real_name 字段）。null/空表示不改。</summary>
    public string? ProfileRealName { get; set; }

    /// <summary>「个性化」面板里设置的概要（资料页 summary 字段，可多行）。null/空表示不改。</summary>
    public string? ProfileSummary { get; set; }

    /// <summary>是否在「一键个性化」完成后清空账号的曾用名记录。</summary>
    public bool ClearAliasHistoryOnPersonalize { get; set; }

    /// <summary>用户自定义账号分组定义（名称/排序）。成员关系存于各账号的 GroupIds，此处只存定义。</summary>
    public List<AccountGroup> Groups { get; set; } = [];

    /// <summary>是否在登录时把「来源账号」的 CS2 本地设置复制到要登录的账号。</summary>
    public bool Cs2SyncOnLogin { get; set; }

    /// <summary>CS2 设置同步的「来源账号」SteamID64；空表示未选择。详见 <see cref="Cs2CloudService"/>。</summary>
    public string? Cs2SyncSourceSteamId { get; set; }

    /// <summary>更新检查使用的 GitHub 站点代码（direct / gh-proxy.org / v4.gh-proxy.org / v6.gh-proxy.org / cdn.gh-proxy.org），与 GitHubUpdateService.ProxySites 保持一致；未知值回退 direct。</summary>
    public string UpdateProxySite { get; set; } = "direct";

    /// <summary>自定义界面主题色，格式 #RRGGBB 或 #AARRGGBB；null 表示使用默认 Aurora 主题色。</summary>
    public string? UiColor { get; set; }

    /// <summary>是否每秒自动旋转界面主题色。</summary>
    public bool UiColorAnimated { get; set; }

    /// <summary>自定义背景默认不透明度。</summary>
    public const double DefaultCustomBackgroundOpacity = 0.4;

    /// <summary>是否启用用户自定义图片/视频背景。</summary>
    public bool CustomBackgroundEnabled { get; set; }

    /// <summary>数据目录 personalization 文件夹内的自定义背景文件名；null 表示尚未设置。</summary>
    public string? CustomBackgroundImageFileName { get; set; }

    /// <summary>自定义背景不透明度，范围 0.1～0.9。</summary>
    public double CustomBackgroundOpacity { get; set; } = DefaultCustomBackgroundOpacity;
}

// 与账号历史一致用 source generator：JsonSerializerDefaults.Web（camelCase、大小写不敏感），AOT 下可读写。
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(CsLoadoutPreset))]
[JsonSerializable(typeof(AccountGroup))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
