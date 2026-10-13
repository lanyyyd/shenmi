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

            // 未设置时 SteamPathText 显示的是本地化占位文案，需随语言刷新（已设置时是中性路径，刷新无副作用）。
            UpdateSteamPathText();

            // 背景文件状态文案是代码设置的，语言切换后重算。
            UpdateCustomBackgroundControls();

            // 来源账号候选/选中文本用 Settings_Cs2Sync_SourceItem_Format 拼装，跟随语言重建。
            RefreshCs2SyncSources();
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

            SetThemeColorPickerColor(AppState.UiColorService.BaseColor);
            UpdateThemeColorControls(settings);
            CustomBackgroundToggle.IsOn = settings.CustomBackgroundEnabled &&
                AppState.SettingsService.GetCustomBackgroundImagePath(settings) is not null;
            CustomBackgroundOpacitySlider.Value = Math.Clamp(settings.CustomBackgroundOpacity, 0.1, 0.9);
            UpdateCustomBackgroundControls();
        }
        finally
        {
            _syncing = false;
        }

        DataFolderPathText.Text = AppState.SettingsService.AppFolderPath;
        UpdateSteamPathText();
        RefreshCs2SyncSources();
    }

    /// <summary>显示当前持久化的 Steam 安装目录；未设置时显示占位文案（启动会自动检测）。</summary>
    private void UpdateSteamPathText()
    {
        SteamPathText.Text = SteamPathCoordinator.GetPersistedInstallPath() ?? Loc.T("Settings_SteamPath_NotSet");
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

    // ---------- CS2 设置同步（issue #10）：来源账号 + 登录时强推 + 立即推送 ----------

    /// <summary>来源账号下拉候选：展示文本（昵称（Steam64））+ 搜索文本（昵称/账号名/Steam64/历史备注）。</summary>
    private sealed record Cs2SourceOption(string SteamId64, string Display, string SearchText);

    private List<Cs2SourceOption> _cs2SourceOptions = [];

    // 已保存的来源账号 SteamID64 的本地镜像，避免各处反复 Load 设置来判断“选中了谁”。
    private string? _cs2SourceSteamId;

    // 刷新代数：语言切换 / 刷新按钮连点 / 导航竞态下并发多轮扫描时，只让最后发起的一轮落地。
    private int _cs2RefreshGen;

    // 搜索框（内部 TextBox）是否持有焦点：刷新完成时正在输入则不动文本/候选，避免打断用户。
    private bool _cs2SourceBoxFocused;

    private async void RefreshCs2SyncSources()
    {
        // userdata 目录解析 + 扫描来源账号 + 离线名称解析都放后台线程：大 userdata / 慢盘 /
        // 数 MB 的 localconfig.vdf 都不卡设置页导航。
        // userdata 路径与「立即推送」一致走 ResolvePathsOrThrow（带自动探测回退），
        // 修掉“未持久化 Steam 路径时下拉恒空、但立即推送却能工作”的不一致。
        var gen = ++_cs2RefreshGen;
        var (sources, names) = await Task.Run(() =>
        {
            try
            {
                var paths = SteamPathCoordinator.ResolvePathsOrThrow();
                var scanned = AppState.Cs2CloudService.EnumerateSources(paths.UserdataPath);
                return (scanned, SteamAccountNameService.BuildOfflineNames(paths, scanned));
            }
            catch (Exception ex)
            {
                AppLog.Warn($"扫描 CS2 设置来源账号失败：{ex.Message}");
                return ((IReadOnlyList<Cs2SettingsSource>)Array.Empty<Cs2SettingsSource>(),
                    (IReadOnlyDictionary<string, OfflineAccountName>)new Dictionary<string, OfflineAccountName>());
            }
        });

        if (gen != _cs2RefreshGen)
        {
            return;
        }

        _syncing = true;
        try
        {
            _cs2SourceOptions = BuildCs2SourceOptions(sources, names);

            var settings = AppState.SettingsService.Load();
            Cs2SyncToggle.IsOn = settings.Cs2SyncOnLogin;
            _cs2SourceSteamId = settings.Cs2SyncSourceSteamId;

            // 正在输入时不动文本/候选：下一次击键会用新数据重新过滤，失焦时恢复规范文本。
            if (!_cs2SourceBoxFocused)
            {
                Cs2SyncSourceBox.ItemsSource = FilterCs2SourceDisplays(null);
                Cs2SyncSourceBox.Text = Cs2SourceSelectedDisplay();
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>拼装候选并排序：有名字的按名字排前面，只剩 Steam64 的按数字排后面。</summary>
    private static List<Cs2SourceOption> BuildCs2SourceOptions(
        IReadOnlyList<Cs2SettingsSource> sources,
        IReadOnlyDictionary<string, OfflineAccountName> names)
    {
        var options = new List<Cs2SourceOption>(sources.Count);
        foreach (var source in sources)
        {
            // 显示名优先级：应用历史（含在线刷新过的昵称）> 本机 Steam 文件的离线解析。
            var history = AppState.HistoryAccounts.FirstOrDefault(item =>
                string.Equals(item.SteamId, source.SteamId64, StringComparison.OrdinalIgnoreCase));
            names.TryGetValue(source.SteamId64, out var offline);

            var persona = FirstNonEmpty(history?.PersonaName, offline?.PersonaName);
            var accountName = FirstNonEmpty(history?.AccountName, offline?.AccountName);
            var name = persona ?? accountName;
            // 有的数据源会把 Steam64 本身存成名字，显示成「X（X）」纯属噪音，按无名处理。
            if (string.Equals(name, source.SteamId64, StringComparison.OrdinalIgnoreCase))
            {
                name = null;
            }

            var display = name is null
                ? source.SteamId64
                : Loc.Tf("Settings_Cs2Sync_SourceItem_Format", name, source.SteamId64);

            // 搜索面覆盖昵称、登录账号名、Steam64 和历史备注——展示文本只放昵称，避免下拉太长。
            var searchText = string.Join(' ',
                new[] { persona, accountName, source.SteamId64, history?.Note }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));

            options.Add(new Cs2SourceOption(source.SteamId64, display, searchText));
        }

        return options
            .OrderBy(option => option.Display == option.SteamId64 ? 1 : 0)
            .ThenBy(option => option.Display, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string? FirstNonEmpty(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first : !string.IsNullOrWhiteSpace(second) ? second : null;

    /// <summary>当前已保存来源的展示文本；来源目录已消失时退回显示原始 Steam64，未选择时为空。</summary>
    private string Cs2SourceSelectedDisplay()
    {
        if (string.IsNullOrWhiteSpace(_cs2SourceSteamId))
        {
            return string.Empty;
        }

        var option = _cs2SourceOptions.FirstOrDefault(item =>
            string.Equals(item.SteamId64, _cs2SourceSteamId, StringComparison.OrdinalIgnoreCase));
        return option?.Display ?? _cs2SourceSteamId;
    }

    private List<string> FilterCs2SourceDisplays(string? query)
    {
        var trimmed = query?.Trim() ?? string.Empty;
        return _cs2SourceOptions
            .Where(option => trimmed.Length == 0 ||
                option.SearchText.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .Select(option => option.Display)
            .ToList();
    }

    /// <summary>把候选设为当前来源：规范化文本并持久化（重复选择同一账号时不重复写盘）。</summary>
    private void CommitCs2Source(string display)
    {
        var option = _cs2SourceOptions.FirstOrDefault(item =>
            string.Equals(item.Display, display, StringComparison.Ordinal));
        if (option is null)
        {
            return;
        }

        _syncing = true;
        try
        {
            Cs2SyncSourceBox.Text = option.Display;
        }
        finally
        {
            _syncing = false;
        }

        if (string.Equals(option.SteamId64, _cs2SourceSteamId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _cs2SourceSteamId = option.SteamId64;
        var settings = AppState.SettingsService.Load();
        settings.Cs2SyncSourceSteamId = option.SteamId64;
        AppState.SettingsService.Save(settings);
    }

    private void Cs2SyncSourceBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // 只响应用户敲键；程序赋值/选中回填触发的 TextChanged 不重开候选。
        if (_syncing || args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        sender.ItemsSource = FilterCs2SourceDisplays(sender.Text);
    }

    private void Cs2SyncSourceBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is string chosen)
        {
            CommitCs2Source(chosen);
            return;
        }

        // 直接回车：文本恰好等于某候选、或非空搜索词过滤后只剩一个候选，则视为选中它。
        // 空文本回车不自动选中（单账号机器上会“无输入即选择”），只展开全部候选。
        var exact = _cs2SourceOptions.FirstOrDefault(option =>
            string.Equals(option.Display, args.QueryText?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            CommitCs2Source(exact.Display);
            return;
        }

        var matches = FilterCs2SourceDisplays(args.QueryText);
        if (matches.Count == 1 && !string.IsNullOrWhiteSpace(args.QueryText))
        {
            CommitCs2Source(matches[0]);
            return;
        }

        sender.ItemsSource = matches;
    }

    private void Cs2SyncSourceBox_GotFocus(object sender, RoutedEventArgs e)
    {
        _cs2SourceBoxFocused = true;

        // 获得焦点即展开全部候选，保留旧 ComboBox「点开就能挑」的体验。
        Cs2SyncSourceBox.ItemsSource = FilterCs2SourceDisplays(null);
        if (_cs2SourceOptions.Count > 0)
        {
            Cs2SyncSourceBox.IsSuggestionListOpen = true;
        }
    }

    private void Cs2SyncSourceBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _cs2SourceBoxFocused = false;

        if (_syncing)
        {
            return;
        }

        // 提交只走 QuerySubmitted（回车/点选候选）。失焦一律回退为已保存选择的展示文本，
        // 与旧 ComboBox 的轻取消语义一致：仅方向键预览过的候选或半截搜索词都不算选择，
        // 否则“瞄一眼就点走”会把高亮项静默写进设置，后续推送就推错账号。
        _syncing = true;
        try
        {
            Cs2SyncSourceBox.Text = Cs2SourceSelectedDisplay();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void Cs2SyncToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        settings.Cs2SyncOnLogin = Cs2SyncToggle.IsOn;
        AppState.SettingsService.Save(settings);
    }

    private void Cs2SyncRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshCs2SyncSources();
    }

    private async void Cs2SyncPushNowButton_Click(object sender, RoutedEventArgs e)
    {
        var source = AppState.SettingsService.Load().Cs2SyncSourceSteamId;
        if (string.IsNullOrWhiteSpace(source))
        {
            AppState.ShowStatus(Loc.T("Cs2Cloud_Error_NoSourceSelected"), InfoBarSeverity.Error);
            return;
        }

        AppState.ShowStatus(Loc.T("Cs2Cloud_Progress_Pushing"), InfoBarSeverity.Informational);
        // 推送期间禁用按钮，避免重复点击排队多次串行推送。
        Cs2SyncPushNowButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() =>
            {
                try
                {
                    var paths = SteamPathCoordinator.ResolvePathsOrThrow();
                    return AppState.Cs2CloudService.PushSourceNow(paths, source);
                }
                catch (Exception ex)
                {
                    return new Cs2CloudPushResult(false, 0, ex.Message);
                }
            });

            var severity = !result.Ok
                ? InfoBarSeverity.Error
                : result.AccountCloudDisabled ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            AppState.ShowStatus(Cs2CloudService.DescribeResult(result), severity);
        }
        finally
        {
            Cs2SyncPushNowButton.IsEnabled = true;
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
