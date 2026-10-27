using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Services;
using Windows.UI.Core;
using VirtualKey = Windows.System.VirtualKey;

// Windows.System 与 Microsoft.UI.Dispatching 都有 DispatcherQueue 系列类型，
// 客户端必须使用 WinUI 3 的 Microsoft.UI.Dispatching 版本，这里用别名消除歧义。
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace SteamEyaWinUI.Pages;

/// <summary>
/// 「小工具」页：内置极域助手（原版 JiYuTrainer，只负责启动 / 显示 / 停止）与保队友。
/// 注入、热键、广播置顶等能力全部由原版界面提供，这里只做开关和状态展示。
/// </summary>
public sealed partial class ToolsPage : Page
{
    private readonly DispatcherQueueTimer _timer;
    private bool _syncing;

    public ToolsPage()
    {
        InitializeComponent();

        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => RefreshStatus();

        Loc.LanguageChanged += OnLanguageChanged;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        RefreshText();
        SyncFromSettings();
        RefreshStatus();
        _timer.Start();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshStatus();
        _timer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        _recordingFullscreen = false;
        _recordingBoss = false;
    }

    private void OnLanguageChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            RefreshText();
            RefreshStatus();
        });
    }

    private void RefreshText()
    {
        TitleText.Text = Loc.T("Tools_Title");
        SubtitleText.Text = Loc.T("Tools_Subtitle");

        HelperTitle.Text = Loc.T("Tools_Helper_Title");
        HelperLabel.Text = Loc.T("Tools_Label_Helper");
        InjectionLabel.Text = Loc.T("Tools_Label_Injection");
        StudentLabel.Text = Loc.T("Tools_Label_Student");
        VersionLabel.Text = Loc.T("Tools_Label_Version");
        HelperDesc.Text = Loc.T("Tools_Helper_Desc");

        ShowButton.Content = Loc.T("Tools_Btn_Show");
        ConfigButton.Content = Loc.T("Tools_Btn_Config");
        FolderButton.Content = Loc.T("Tools_Btn_Folder");

        HotkeyTitle.Text = Loc.T("Tools_Hotkey_Title");
        HotkeyDesc.Text = Loc.T("Tools_Hotkey_Desc");
        FullscreenHotkeySwitch.Header = Loc.T("Tools_Hotkey_Fullscreen");
        BossHotkeySwitch.Header = Loc.T("Tools_Hotkey_Boss");
        BossHideSwitch.Header = Loc.T("Tools_Hotkey_BossHide");
        FullscreenHotkeySwitch.OnContent = Loc.T("Tools_On");
        FullscreenHotkeySwitch.OffContent = Loc.T("Tools_Off");
        BossHotkeySwitch.OnContent = Loc.T("Tools_On");
        BossHotkeySwitch.OffContent = Loc.T("Tools_Off");
        BossHideSwitch.OnContent = Loc.T("Tools_On");
        BossHideSwitch.OffContent = Loc.T("Tools_Off");
        FullscreenRecordButton.Content = Loc.T(
            _recordingFullscreen ? "Tools_Hotkey_Recording" : "Tools_Hotkey_Record");
        BossRecordButton.Content = Loc.T(
            _recordingBoss ? "Tools_Hotkey_Recording" : "Tools_Hotkey_Record");
        BossTestButton.Content = Loc.T("Tools_Hotkey_Test");
        UpdateHotkeyText();

        InjectionTitle.Text = Loc.T("Tools_Injection_Title");
        InjectionDesc.Text = Loc.T("Tools_Injection_Desc");
        InjectionSwitch.OnContent = Loc.T("Tools_On");
        InjectionSwitch.OffContent = Loc.T("Tools_Off");
        DetachButton.Content = Loc.T("Tools_Btn_Detach");

        CloseWithAppTitle.Text = Loc.T("Tools_CloseWithApp_Title");
        CloseWithAppDesc.Text = Loc.T("Tools_CloseWithApp_Desc");
        CloseWithAppSwitch.OnContent = Loc.T("Tools_On");
        CloseWithAppSwitch.OffContent = Loc.T("Tools_Off");

        PanicTitle.Text = Loc.T("Tools_Panic_Title");
        PanicDesc.Text = Loc.T("Tools_Panic_Desc");
        PanicButton.Content = _panicArmed ? Loc.T("Tools_Panic_Button_Confirm") : Loc.T("Tools_Panic_Button");
        PanicCancelButton.Content = Loc.T("Common_Cancel");
    }

    private void SyncFromSettings()
    {
        var settings = AppState.SettingsService.Load();

        _syncing = true;
        try
        {
            InjectionSwitch.IsOn = settings.JiYuEnabled;
            CloseWithAppSwitch.IsOn = settings.JiYuCloseWithApp;
            FullscreenHotkeySwitch.IsOn = settings.JiYuFullscreenHotkeyEnabled;
            BossHotkeySwitch.IsOn = settings.JiYuBossKeyEnabled;
            BossHideSwitch.IsOn = settings.JiYuBossKeyHideWindows;
        }
        finally
        {
            _syncing = false;
        }

        UpdateHotkeyText();
        UpdateHotkeyControls(settings);
    }

    private AppSettings SaveSetting(Action<AppSettings> update)
    {
        var settings = AppState.SettingsService.Load();
        update(settings);
        AppState.SettingsService.Save(settings);
        return settings;
    }

    private void OnInjectionToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        if (InjectionSwitch.IsOn)
        {
            if (!JiYuService.IsAvailable)
            {
                SetInjectionSwitch(false);
                _ = SaveSetting(s => s.JiYuEnabled = false);
                ShowStatus(Loc.T("Tools_HelperMissing"), InfoBarSeverity.Error);
                return;
            }

            if (!JiYuService.Start())
            {
                SetInjectionSwitch(false);
                _ = SaveSetting(s => s.JiYuEnabled = false);
                ShowStatus(Loc.T("Tools_StartFailed"), InfoBarSeverity.Error);
                return;
            }

            _ = SaveSetting(s => s.JiYuEnabled = true);
            ShowStatus(Loc.T("Tools_Started"), InfoBarSeverity.Success);
        }
        else
        {
            _ = SaveSetting(s => s.JiYuEnabled = false);
            JiYuService.Stop();
            ShowStatus(Loc.T("Tools_Detached"), InfoBarSeverity.Success);
        }

        RefreshStatus();
    }

    private void OnCloseWithAppToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        _ = SaveSetting(s => s.JiYuCloseWithApp = CloseWithAppSwitch.IsOn);
        ShowStatus(Loc.T("Tools_Saved"), InfoBarSeverity.Success);
    }

    // ---------- 快捷键（伪装全屏 / 老板键） ----------

    private bool _recordingFullscreen;
    private bool _recordingBoss;

    private void UpdateHotkeyText()
    {
        var settings = AppState.SettingsService.Load();

        FullscreenHotkeyBox.Text = _recordingFullscreen
            ? Loc.T("Tools_Hotkey_PressKeys")
            : ScreenshotService.FormatHotkey(
                settings.JiYuFullscreenHotkeyMods, settings.JiYuFullscreenHotkeyVk);

        BossHotkeyBox.Text = _recordingBoss
            ? Loc.T("Tools_Hotkey_PressKeys")
            : ScreenshotService.FormatHotkey(settings.JiYuBossKeyMods, settings.JiYuBossKeyVk);
    }

    private void UpdateHotkeyControls(AppSettings settings)
    {
        FullscreenHotkeyBox.IsEnabled = settings.JiYuFullscreenHotkeyEnabled;
        FullscreenRecordButton.IsEnabled = settings.JiYuFullscreenHotkeyEnabled;
        BossHotkeyBox.IsEnabled = settings.JiYuBossKeyEnabled;
        BossRecordButton.IsEnabled = settings.JiYuBossKeyEnabled;
        BossTestButton.IsEnabled = settings.JiYuBossKeyEnabled;
    }

    /// <summary>把设置里的热键重新注册到主窗口；失败时主窗口状态栏会给出占用提示。</summary>
    private void ApplyHotkeys(AppSettings settings)
    {
        UpdateHotkeyControls(settings);

        var (bossOk, fullscreenOk) = MainWindow.ApplyJiYuHotkeys(settings);
        var allOk = (!settings.JiYuBossKeyEnabled || bossOk) &&
                    (!settings.JiYuFullscreenHotkeyEnabled || fullscreenOk);

        if (allOk)
        {
            ShowStatus(Loc.T("Tools_Saved"), InfoBarSeverity.Success);
        }
    }

    private void OnFullscreenHotkeyToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        ApplyHotkeys(SaveSetting(s => s.JiYuFullscreenHotkeyEnabled = FullscreenHotkeySwitch.IsOn));
    }

    private void OnBossHotkeyToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        ApplyHotkeys(SaveSetting(s => s.JiYuBossKeyEnabled = BossHotkeySwitch.IsOn));
    }

    private void OnBossHideToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        _ = SaveSetting(s => s.JiYuBossKeyHideWindows = BossHideSwitch.IsOn);
        ShowStatus(Loc.T("Tools_Saved"), InfoBarSeverity.Success);
    }

    private void OnFullscreenRecordClick(object sender, RoutedEventArgs e)
    {
        _recordingFullscreen = true;
        FullscreenRecordButton.Content = Loc.T("Tools_Hotkey_Recording");
        FullscreenHotkeyBox.Text = Loc.T("Tools_Hotkey_PressKeys");
        FullscreenHotkeyBox.Focus(FocusState.Programmatic);
    }

    private void OnBossRecordClick(object sender, RoutedEventArgs e)
    {
        _recordingBoss = true;
        BossRecordButton.Content = Loc.T("Tools_Hotkey_Recording");
        BossHotkeyBox.Text = Loc.T("Tools_Hotkey_PressKeys");
        BossHotkeyBox.Focus(FocusState.Programmatic);
    }

    private void OnFullscreenHotkeyKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_recordingFullscreen)
        {
            return;
        }

        if (TryReadHotkey(e, out var mods, out var vk))
        {
            _recordingFullscreen = false;
            var settings = SaveSetting(s =>
            {
                s.JiYuFullscreenHotkeyMods = mods;
                s.JiYuFullscreenHotkeyVk = vk;
                s.JiYuFullscreenHotkeyEnabled = true;
            });

            RefreshText();
            SetSwitchesSilently(settings);
            ApplyHotkeys(settings);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            _recordingFullscreen = false;
            RefreshText();
        }

        e.Handled = true;
    }

    private void OnBossHotkeyKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_recordingBoss)
        {
            return;
        }

        if (TryReadHotkey(e, out var mods, out var vk))
        {
            _recordingBoss = false;
            var settings = SaveSetting(s =>
            {
                s.JiYuBossKeyMods = mods;
                s.JiYuBossKeyVk = vk;
                s.JiYuBossKeyEnabled = true;
            });

            RefreshText();
            SetSwitchesSilently(settings);
            ApplyHotkeys(settings);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            _recordingBoss = false;
            RefreshText();
        }

        e.Handled = true;
    }

    /// <summary>录制时读取按下的组合键；按的是纯修饰键或 Esc 时返回 false（Esc 由调用方处理为取消）。</summary>
    private bool TryReadHotkey(KeyRoutedEventArgs e, out uint mods, out uint vk)
    {
        vk = (uint)e.Key;
        mods = CurrentModifiers();

        if (vk == (uint)VirtualKey.Escape || IsModifierKey(vk))
        {
            return false;
        }

        if (mods == 0)
        {
            ShowStatus(Loc.T("Tools_Hotkey_NeedModifier"), InfoBarSeverity.Warning);
            return false;
        }

        return true;
    }

    private void SetSwitchesSilently(AppSettings settings)
    {
        _syncing = true;
        try
        {
            FullscreenHotkeySwitch.IsOn = settings.JiYuFullscreenHotkeyEnabled;
            BossHotkeySwitch.IsOn = settings.JiYuBossKeyEnabled;
            BossHideSwitch.IsOn = settings.JiYuBossKeyHideWindows;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnBossTestClick(object sender, RoutedEventArgs e)
    {
        if (!JiYuService.Query().Injected)
        {
            ShowStatus(Loc.T("Tools_Hotkey_NotInjected"), InfoBarSeverity.Warning);
            return;
        }

        if (!JiYuService.TriggerBossKey())
        {
            ShowStatus(Loc.T("Tools_Hotkey_TestFailed"), InfoBarSeverity.Warning);
            return;
        }

        ShowStatus(Loc.T("Tools_Hotkey_TestSent"), InfoBarSeverity.Success);
    }

    private static bool IsModifierKey(uint vk) =>
        vk is 16 or 17 or 18 or 91 or 92 or 160 or 161 or 162 or 163 or 164 or 165;

    private static uint CurrentModifiers()
    {
        var mods = 0u;
        if (IsKeyDown(VirtualKey.Control)) mods |= ScreenshotService.ModControl;
        if (IsKeyDown(VirtualKey.Menu)) mods |= ScreenshotService.ModAlt;
        if (IsKeyDown(VirtualKey.Shift)) mods |= ScreenshotService.ModShift;
        if (IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows)) mods |= ScreenshotService.ModWin;
        return mods;
    }

    private static bool IsKeyDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    /// <summary>对应原版退出提示里的「分离极域并退出」：先卸载注入，再结束内置助手。</summary>
    private void OnDetachClick(object sender, RoutedEventArgs e)
    {
        var state = JiYuService.Query();
        if (!state.Running && !state.Injected)
        {
            ShowStatus(Loc.T("Tools_Detach_NotRunning"), InfoBarSeverity.Warning);
            return;
        }

        JiYuService.Stop();
        SetInjectionSwitch(false);
        _ = SaveSetting(s => s.JiYuEnabled = false);
        ShowStatus(Loc.T("Tools_Detached"), InfoBarSeverity.Success);
        RefreshStatus();
    }

    private void OnShowClick(object sender, RoutedEventArgs e)
    {
        if (!JiYuService.IsAvailable)
        {
            ShowStatus(Loc.T("Tools_HelperMissing"), InfoBarSeverity.Error);
            return;
        }

        JiYuService.ShowMainWindow();
        RefreshStatus();
    }

    private void OnConfigClick(object sender, RoutedEventArgs e)
    {
        if (!JiYuService.OpenSettings())
        {
            ShowStatus(Loc.T("Tools_HelperMissing"), InfoBarSeverity.Error);
            return;
        }

        ShowStatus(Loc.T("Tools_ConfigOpened"), InfoBarSeverity.Success);
    }

    private void OnFolderClick(object sender, RoutedEventArgs e) => JiYuService.OpenFolder();

    private void SetInjectionSwitch(bool value)
    {
        _syncing = true;
        try
        {
            InjectionSwitch.IsOn = value;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void RefreshStatus()
    {
        var state = JiYuService.Query();

        if (!state.Available)
        {
            HelperValue.Text = Loc.T("Tools_HelperMissing");
            InjectionValue.Text = Loc.T("Tools_Status_Unknown");
            StudentValue.Text = Loc.T("Tools_Status_Unknown");
            VersionValue.Text = Loc.Tf("Tools_Status_Version_Format", JiYuService.BundledVersion);
            ShowButton.IsEnabled = false;
            ConfigButton.IsEnabled = false;
            FolderButton.IsEnabled = false;
            DetachButton.IsEnabled = false;
            return;
        }

        ShowButton.IsEnabled = true;
        ConfigButton.IsEnabled = true;
        FolderButton.IsEnabled = true;
        DetachButton.IsEnabled = state.Running || state.Injected;

        HelperValue.Text = state.Running
            ? Loc.Tf("Tools_Status_Helper_Running_Format", state.ProcessId)
            : Loc.T("Tools_Status_Helper_None");

        InjectionValue.Text = state.Injected
            ? Loc.T("Tools_Status_Injected")
            : !state.Running
                ? Loc.T("Tools_Status_InjectionOff")
                : state.StudentRunning
                    ? Loc.T("Tools_Status_NotInjected")
                    : Loc.T("Tools_Status_WaitingStudent");

        StudentValue.Text = state.StudentRunning
            ? Loc.Tf("Tools_Status_Student_Running_Format", state.StudentPid)
            : Loc.T("Tools_Status_Student_None");

        VersionValue.Text = Loc.Tf("Tools_Status_Version_Format", JiYuService.BundledVersion);
    }

    private bool _panicArmed;
    private int _panicArmVersion;

    /// <summary>
    /// 两步确认：首次点击进入待确认态（按钮变「确认保队友」、右侧出现取消），
    /// 再点同一按钮即执行。8 秒无操作自动复位，避免用户走了之后下次误触直接下线全员。
    /// </summary>
    private async void OnPanicClick(object sender, RoutedEventArgs e)
    {
        if (!_panicArmed)
        {
            ArmPanic();
            return;
        }

        await ExecutePanicAsync();
    }

    private async void ArmPanic()
    {
        _panicArmed = true;
        var version = ++_panicArmVersion;

        PanicButton.Content = Loc.T("Tools_Panic_Button_Confirm");
        PanicCancelButton.Content = Loc.T("Common_Cancel");
        PanicCancelButton.Visibility = Visibility.Visible;

        await Task.Delay(8000);

        // 期间若已取消/已执行/又重新进入待确认，就不要再动 UI。
        if (_panicArmed && version == _panicArmVersion)
        {
            DisarmPanic();
        }
    }

    private void DisarmPanic()
    {
        _panicArmed = false;
        _panicArmVersion++;
        PanicButton.Content = Loc.T("Tools_Panic_Button");
        PanicCancelButton.Visibility = Visibility.Collapsed;
    }

    private void OnPanicCancelClick(object sender, RoutedEventArgs e) => DisarmPanic();

    private async Task ExecutePanicAsync()
    {
        DisarmPanic();

        PanicButton.IsEnabled = false;
        if (!await ChatHub.SendPanicAsync())
        {
            PanicButton.IsEnabled = true;
            ShowStatus(Loc.T("Tools_Panic_NotConnected"), InfoBarSeverity.Warning);
            return;
        }

        // 发送成功后服务端会把 panic 广播回本机，ChatHub 收到后立即退出，这里无需再处理。
        ShowStatus(Loc.T("Tools_Panic_Sent"), InfoBarSeverity.Success);
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        PageInfoBar.Message = message;
        PageInfoBar.Severity = severity;
        PageInfoBar.IsOpen = true;
    }
}
