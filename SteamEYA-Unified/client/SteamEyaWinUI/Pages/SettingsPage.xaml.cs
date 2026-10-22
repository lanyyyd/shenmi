using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Services;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;

namespace SteamEyaWinUI.Pages;

public sealed partial class SettingsPage : Page, INotifyPropertyChanged
{
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

    // 代码设置 ComboBox.SelectedItem 会触发 SelectionChanged，置位以区分“用户选择”与“初始同步”，避免回写/重复应用。
    private bool _syncing;
    private bool _languageItemsBuilt;

    // 自定义背景可选类型：图片（含 GIF）+ 视频（静音循环播放）。
    private static readonly string[] BackgroundImageExtensions =
        [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv"];

    private bool _pickingBackground;

    public SettingsPage()
    {
        // XAML 初始化取色器/滑杆/开关时可能触发变更事件；暂时屏蔽，避免首帧用默认值覆盖已保存设置。
        _syncing = true;
        InitializeComponent();
        _syncing = false;

        // 语言切换后，让本页所有 {x:Bind Strings.Get(...), Mode=OneWay} 重新求值（主题项文本等）。
        Loc.LanguageChanged += OnLanguageChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>XAML 绑定入口：{x:Bind Strings.Get('Key'), Mode=OneWay}。</summary>
    internal LocalizedStrings Strings => Loc.Strings;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        BuildLanguageItems();
        SyncFromSettings();
    }

    private void OnLanguageChanged()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Strings)));

            // 主题项 Content 经 x:Bind 已换语言，但 ComboBox 关闭态的“选中框”显示的是选中项内容的快照，
            // 不会自动重读——把 SelectedItem 重置一轮强制刷新。同一 UI 回调内同步完成，中间空态不渲染，无闪烁。
            // （语言下拉的项是各语言自称名，不随界面语言变，故无需处理。）
            var theme = ThemeComboBox.SelectedItem;
            if (theme is not null)
            {
                _syncing = true;
                ThemeComboBox.SelectedItem = null;
                ThemeComboBox.SelectedItem = theme;
                _syncing = false;
            }

            // 聊天室运行模式下拉同理：选中项内容是本地化的，重置 SelectedItem 强制刷新显示。
            var chatMode = ChatModeComboBox.SelectedItem;
            if (chatMode is not null)
            {
                _syncing = true;
                ChatModeComboBox.SelectedItem = null;
                ChatModeComboBox.SelectedItem = chatMode;
                _syncing = false;
            }

            // 未设置时 SteamPathText 显示的是本地化占位文案，需随语言刷新（已设置时是中性路径，刷新无副作用）。
            UpdateSteamPathText();

            // 背景文件状态文案是代码设置的，语言切换后重算。
            UpdateCustomBackgroundControls();
            UpdateDownloadDirText();

        });
    }

    /// <summary>按已加载的语言包动态生成语言下拉项（只建一次）。语言自称名不随界面语言变化。</summary>
    private void BuildLanguageItems()
    {
        if (_languageItemsBuilt)
        {
            return;
        }

        foreach (var pack in Loc.AvailablePacks)
        {
            LanguageComboBox.Items.Add(new ComboBoxItem { Content = pack.Name, Tag = pack.Code });
        }

        _languageItemsBuilt = true;
    }

    /// <summary>按当前语言与已保存主题选中对应下拉项；过程中屏蔽 SelectionChanged 处理。</summary>
    private void SyncFromSettings()
    {
        _syncing = true;
        try
        {
            var settings = AppState.SettingsService.Load();
            LanguageComboBox.SelectedItem = FindByTag(LanguageComboBox, Loc.CurrentCode);
            ThemeComboBox.SelectedItem = FindByTag(ThemeComboBox, settings.Theme) ?? ThemeComboBox.Items[0];
            ChatModeComboBox.SelectedItem = FindByTag(ChatModeComboBox, NormalizeChatMode(settings.ChatMode));

            SetThemeColorPickerColor(AppState.UiColorService.BaseColor);
            UpdateThemeColorControls(settings);
            CustomBackgroundToggle.IsOn = settings.CustomBackgroundEnabled &&
                AppState.SettingsService.GetCustomBackgroundImagePath(settings) is not null;
            CustomBackgroundOpacitySlider.Value = Math.Clamp(settings.CustomBackgroundOpacity, 0.1, 0.9);
            UpdateCustomBackgroundControls();
            DownloadThreadBox.Value = Math.Clamp(settings.DownloadThreadCount, 1, 256);
        }
        finally
        {
            _syncing = false;
        }

        DataFolderPathText.Text = AppState.SettingsService.AppFolderPath;
        UpdateSteamPathText();
        UpdateDownloadDirText();
    }

    /// <summary>显示当前持久化的 Steam 安装目录；未设置时显示占位文案（启动会自动检测）。</summary>
    private void UpdateSteamPathText()
    {
        SteamPathText.Text = SteamPathCoordinator.GetPersistedInstallPath() ?? Loc.T("Settings_SteamPath_NotSet");
    }

    private void UpdateDownloadDirText()
    {
        if (DownloadDirText is null)
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        DownloadDirText.Text = string.IsNullOrWhiteSpace(settings.DownloadDirectory)
            ? Loc.Tf("Settings_Download_Dir_Default_Format", DownloadPaths.DefaultDirectory)
            : DownloadPaths.ResolveDirectory(settings);
    }

    private void DownloadThreadBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncing)
        {
            return;
        }

        var value = double.IsNaN(args.NewValue)
            ? AppSettings.DefaultDownloadThreadCount
            : (int)args.NewValue;

        var settings = AppState.SettingsService.Load();
        settings.DownloadThreadCount = Math.Clamp(value, 1, 256);
        AppState.SettingsService.Save(settings);
    }

    private async void DownloadDirButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads
            };
            picker.FileTypeFilter.Add("*");

            // 无打包 WinUI：选择器必须显式绑定窗口句柄。
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            var settings = AppState.SettingsService.Load();
            settings.DownloadDirectory = folder.Path;
            AppState.SettingsService.Save(settings);

            UpdateDownloadDirText();
            AppState.ShowStatus(Loc.T("Settings_Download_Dir_Changed"), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppState.ShowStatus(Loc.Tf("Settings_Download_Dir_ChangeFailed_Format", ex.Message), InfoBarSeverity.Error);
        }
    }

    private void DownloadDirResetButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppState.SettingsService.Load();
        settings.DownloadDirectory = null;
        AppState.SettingsService.Save(settings);
        UpdateDownloadDirText();
    }

    private static ComboBoxItem? FindByTag(ComboBox combo, string tag) =>
        combo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is string value &&
                string.Equals(value, tag, StringComparison.OrdinalIgnoreCase));

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || (LanguageComboBox.SelectedItem as ComboBoxItem)?.Tag is not string code)
        {
            return;
        }

        Loc.SetLanguage(code);
    }

    /// <summary>兼容旧数据：未知取值一律当作「自动」。</summary>
    private static string NormalizeChatMode(string? mode) => mode switch
    {
        "Web" => "Web",
        "Native" => "Native",
        _ => "Auto"
    };

    private void ChatModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || (ChatModeComboBox.SelectedItem as ComboBoxItem)?.Tag is not string mode)
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        settings.ChatMode = mode;
        AppState.SettingsService.Save(settings);
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || (ThemeComboBox.SelectedItem as ComboBoxItem)?.Tag is not string theme)
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        // 选中「自定义」且此前没挑过颜色时，用当前主题色初始化，避免切换后颜色无处可挑。
        if (string.Equals(theme, "Custom", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(settings.UiColor))
        {
            var current = AppState.UiColorService.BaseColor;
            settings.UiColor = $"#{current.A:X2}{current.R:X2}{current.G:X2}{current.B:X2}";
        }

        settings.Theme = theme;
        AppState.SettingsService.Save(settings);
        ApplyThemeSettings(settings);
    }

    /// <summary>套用主题：内容根主题 + 整套 Aurora 换色，并同步主题色控件的显示。</summary>
    private void ApplyThemeSettings(AppSettings settings)
    {
        MainWindow.Instance?.ApplyTheme(UiColorService.ResolveElementTheme(settings.Theme, settings.UiColor));
        AppState.UiColorService.Apply(settings.UiColor, settings.UiColorAnimated, settings.Theme);
        UpdateThemeColorControls(settings);
    }

    /// <summary>主题色按钮仅在「自定义」主题下可见；色块跟随当前实际主题色。</summary>
    private void UpdateThemeColorControls(AppSettings settings)
    {
        var color = AppState.UiColorService.BaseColor;
        ThemeColorButton.Visibility = string.Equals(settings.Theme, "Custom", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ThemeColorSwatch.Background = new SolidColorBrush(color);
        SetThemeColorPickerColor(color);
    }

    /// <summary>设置取色器颜色；期间屏蔽 ColorChanged，避免回写设置。</summary>
    private void SetThemeColorPickerColor(Color color)
    {
        var previous = _syncing;
        _syncing = true;
        try
        {
            ThemeColorPicker.Color = color;
        }
        finally
        {
            _syncing = previous;
        }
    }

    private void ThemeColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_syncing)
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        settings.UiColor = $"#{args.NewColor.A:X2}{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}";
        AppState.SettingsService.Save(settings);
        AppState.UiColorService.Apply(settings.UiColor, settings.UiColorAnimated, settings.Theme);
        ThemeColorSwatch.Background = new SolidColorBrush(args.NewColor);
    }

    private void ResetThemeColorButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppState.SettingsService.Load();
        settings.UiColor = null;
        AppState.SettingsService.Save(settings);
        AppState.UiColorService.Apply(settings.UiColor, settings.UiColorAnimated, settings.Theme);
        MainWindow.Instance?.ApplyTheme(UiColorService.ResolveElementTheme(settings.Theme, settings.UiColor));
        UpdateThemeColorControls(settings);
    }

    // ---------- 自定义背景 ----------

    private async void CustomBackgroundToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        // 打开开关但还没有背景文件：直接拉起选图流程；用户取消就回退开关。
        if (CustomBackgroundToggle.IsOn && AppState.SettingsService.GetCustomBackgroundImagePath(settings) is null)
        {
            if (!await PickAndApplyCustomBackgroundAsync())
            {
                _syncing = true;
                try
                {
                    CustomBackgroundToggle.IsOn = false;
                }
                finally
                {
                    _syncing = false;
                }

                settings.CustomBackgroundEnabled = false;
                AppState.SettingsService.Save(settings);
                MainWindow.Instance?.ApplyCustomBackground(settings);
                UpdateCustomBackgroundControls();
            }

            return;
        }

        settings.CustomBackgroundEnabled = CustomBackgroundToggle.IsOn;
        AppState.SettingsService.Save(settings);
        MainWindow.Instance?.ApplyCustomBackground(settings);
        UpdateCustomBackgroundControls();
    }

    private async void ChooseCustomBackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        await PickAndApplyCustomBackgroundAsync();
    }

    /// <summary>选文件 → 导入数据目录 → 落盘设置 → 立刻应用。</summary>
    private async Task<bool> PickAndApplyCustomBackgroundAsync()
    {
        if (_pickingBackground)
        {
            return false;
        }

        _pickingBackground = true;
        UpdateCustomBackgroundControls();
        try
        {
            // 背景同时支持图片与视频，起始位置用「此电脑」而非图片库，选视频时不用手动翻目录。
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
                ViewMode = PickerViewMode.Thumbnail
            };
            foreach (var extension in BackgroundImageExtensions)
            {
                picker.FileTypeFilter.Add(extension);
            }

            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Hwnd);
            StorageFile? file;
            try
            {
                file = await picker.PickSingleFileAsync();
            }
            catch (Exception ex)
            {
                AppLog.Error("打开背景图片选择器失败。", ex);
                AppState.ShowStatus(Loc.T("Settings_Background_ChooseFail"), InfoBarSeverity.Error);
                return false;
            }

            if (file is null || string.IsNullOrWhiteSpace(file.Path))
            {
                return false;
            }

            string fileName;
            try
            {
                var sourcePath = file.Path;
                fileName = await Task.Run(() => AppState.SettingsService.ImportCustomBackgroundImage(sourcePath));
            }
            catch (Exception ex)
            {
                AppLog.Error("导入自定义背景失败。", ex);
                AppState.ShowStatus(Loc.T("Settings_Background_ChooseFail"), InfoBarSeverity.Error);
                return false;
            }

            var settings = AppState.SettingsService.Load();
            settings.CustomBackgroundImageFileName = fileName;
            settings.CustomBackgroundEnabled = true;
            settings.CustomBackgroundOpacity = Math.Clamp(CustomBackgroundOpacitySlider.Value, 0.1, 0.9);
            AppState.SettingsService.Save(settings);

            _syncing = true;
            try
            {
                CustomBackgroundToggle.IsOn = true;
                CustomBackgroundOpacitySlider.Value = settings.CustomBackgroundOpacity;
            }
            finally
            {
                _syncing = false;
            }

            MainWindow.Instance?.ApplyCustomBackground(settings);
            AppState.SettingsService.DeleteOtherCustomBackgroundImages(fileName);
            return true;
        }
        finally
        {
            _pickingBackground = false;
            UpdateCustomBackgroundControls();
        }
    }

    private void ClearCustomBackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppState.SettingsService.Load();
        settings.CustomBackgroundEnabled = false;
        settings.CustomBackgroundImageFileName = null;
        AppState.SettingsService.Save(settings);
        MainWindow.Instance?.ApplyCustomBackground(settings);
        AppState.SettingsService.DeleteCustomBackgroundImages();

        _syncing = true;
        try
        {
            CustomBackgroundToggle.IsOn = false;
        }
        finally
        {
            _syncing = false;
        }

        UpdateCustomBackgroundControls();
    }

    private void CustomBackgroundOpacitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        settings.CustomBackgroundOpacity = Math.Clamp(e.NewValue, 0.1, 0.9);
        AppState.SettingsService.Save(settings);
        if (settings.CustomBackgroundEnabled)
        {
            MainWindow.Instance?.ApplyCustomBackground(settings);
        }
    }

    private void ResetCustomBackgroundOpacityButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppState.SettingsService.Load();
        settings.CustomBackgroundOpacity = AppSettings.DefaultCustomBackgroundOpacity;
        AppState.SettingsService.Save(settings);

        _syncing = true;
        try
        {
            CustomBackgroundOpacitySlider.Value = AppSettings.DefaultCustomBackgroundOpacity;
        }
        finally
        {
            _syncing = false;
        }

        if (settings.CustomBackgroundEnabled)
        {
            MainWindow.Instance?.ApplyCustomBackground(settings);
        }
    }

    /// <summary>刷新背景卡片的文件状态文案与按钮可用性。</summary>
    private void UpdateCustomBackgroundControls()
    {
        var settings = AppState.SettingsService.Load();
        var hasImage = AppState.SettingsService.GetCustomBackgroundImagePath(settings) is not null;
        CustomBackgroundFileNameText.Text = Loc.T(hasImage
            ? "Settings_Background_Selected"
            : "Settings_Background_None");
        ChooseCustomBackgroundButton.IsEnabled = !_pickingBackground;
        ClearCustomBackgroundButton.IsEnabled = hasImage && !_pickingBackground;
        CustomBackgroundOpacitySlider.IsEnabled = hasImage && CustomBackgroundToggle.IsOn;
        ResetCustomBackgroundOpacityButton.IsEnabled = hasImage && CustomBackgroundToggle.IsOn;
    }

    /// <summary>手动更改上号使用的 Steam 安装目录（多 Steam 时指定要用哪一个）。选择器+校验+持久化都在协调器里。</summary>
    private async void ChangeSteamPathButton_Click(object sender, RoutedEventArgs e)
    {
        if (await SteamPathCoordinator.PickAndPersistManuallyAsync())
        {
            UpdateSteamPathText();
            AppState.ShowStatus(Loc.T("Settings_SteamPath_Changed"), InfoBarSeverity.Success);
        }
    }


    private async void ChatSyncButton_Click(object sender, RoutedEventArgs e)
    {
        if (SessionState.Offline || string.IsNullOrWhiteSpace(SessionState.Token))
        {
            AppState.ShowStatus(Loc.T("Settings_ChatSync_OnlineOnly"), InfoBarSeverity.Warning);
            return;
        }

        ChatSyncButton.IsEnabled = false;
        AppState.ShowStatus(Loc.T("Settings_ChatSync_Pushing"), InfoBarSeverity.Informational);
        try
        {
            // 先拉起本地网关，否则读不到 /api/sync/pending
            await GatewayLauncher.EnsureStartedAsync(SessionState.Nickname);
            var (ok, pushed, msg) = await new SyncService().TrySyncAsync();
            if (ok && pushed > 0)
            {
                AppState.ShowStatus(Loc.Tf("Settings_ChatSync_Success_Format", pushed), InfoBarSeverity.Success);
            }
            else if (ok && pushed == 0)
            {
                AppState.ShowStatus(Loc.T("Settings_ChatSync_Nothing"), InfoBarSeverity.Informational);
            }
            else
            {
                AppState.ShowStatus(Loc.Tf("Settings_ChatSync_Fail_Format", msg ?? "unknown"), InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            AppState.ShowStatus(Loc.Tf("Settings_ChatSync_Fail_Format", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            ChatSyncButton.IsEnabled = true;
        }
    }

    private void OpenDataFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = AppState.SettingsService.AppFolderPath;
        try
        {
            Directory.CreateDirectory(folder);
            // explorer.exe 接受目录路径作参数直接打开资源管理器；比 ShellExecute 文件夹更稳。
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("打开数据目录失败。", ex);
            AppState.ShowStatus(Loc.T("Settings_Data_OpenFail"), InfoBarSeverity.Error);
        }
    }

    // 自检系统通知是否可用：立刻弹一条测试通知；不可用时会提示查看日志。
    private void NotifyTestButton_Click(object sender, RoutedEventArgs e) => ChatNotifyService.Test();
}
