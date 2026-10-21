using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Services;
using Windows.System;
using Windows.UI.Core;

// Windows.System 与 Microsoft.UI.Dispatching 都有 DispatcherQueue 系列类型，
// 客户端必须使用 WinUI 3 的 Microsoft.UI.Dispatching 版本，这里用别名消除歧义。
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace SteamEyaWinUI.Pages;

/// <summary>
/// 「小工具」页：极域电子教室绕过状态、绕过开关、广播置顶许可与老板键设置。
/// 页面只负责展示/下发配置，真正的进程注入与窗口控制在 32 位 JiYuHost.exe 中完成。
/// </summary>
public sealed partial class ToolsPage : Page
{
    private readonly DispatcherQueueTimer _timer;
    private bool _syncing;
    private bool _recording;

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
        JiYuService.EnsureStarted();
        JiYuService.RequestRefresh();
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
        _recording = false;
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

        StatusTitle.Text = Loc.T("Tools_Status_Section");
        RefreshLabel.Text = Loc.T("Tools_Status_Refresh");
        StudentLabel.Text = Loc.T("Tools_Label_Student");
        BypassLabel.Text = Loc.T("Tools_Label_Bypass");
        BroadcastLabel.Text = Loc.T("Tools_Label_Broadcast");
        HotkeyLabel.Text = Loc.T("Tools_Label_Hotkey");

        BypassTitle.Text = Loc.T("Tools_Bypass_Title");
        BypassDesc.Text = Loc.T("Tools_Bypass_Desc");
        BypassSwitch.OnContent = Loc.T("Tools_On");
        BypassSwitch.OffContent = Loc.T("Tools_Off");

        AllowTopTitle.Text = Loc.T("Tools_AllowTop_Title");
        AllowTopDesc.Text = Loc.T("Tools_AllowTop_Desc");
        AllowTopSwitch.OnContent = Loc.T("Tools_On");
        AllowTopSwitch.OffContent = Loc.T("Tools_Off");

        BossTitle.Text = Loc.T("Tools_Boss_Title");
        BossDesc.Text = Loc.T("Tools_Boss_Desc");
        BossHotkeyLabel.Text = Loc.T("Tools_Boss_HotkeyLabel");
        BossSwitch.OnContent = Loc.T("Tools_On");
        BossSwitch.OffContent = Loc.T("Tools_Off");
        RecordButton.Content = Loc.T(_recording ? "Tools_Boss_Recording" : "Tools_Boss_Record");
        TestButton.Content = Loc.T("Tools_Boss_Test");

        if (_recording)
        {
            HotkeyBox.Text = Loc.T("Tools_Boss_PressKeys");
        }
        else
        {
            var settings = AppState.SettingsService.Load();
            HotkeyBox.Text = JiYuService.FormatHotkey(settings.JiYuBossKeyMods, settings.JiYuBossKeyVk);
        }
    }

    private void SyncFromSettings()
    {
        var settings = AppState.SettingsService.Load();

        _syncing = true;
        try
        {
            BypassSwitch.IsOn = settings.JiYuBypassEnabled;
            AllowTopSwitch.IsOn = settings.JiYuAllowGbTop;
            BossSwitch.IsOn = settings.JiYuBossKeyEnabled;
            HotkeyBox.Text = JiYuService.FormatHotkey(settings.JiYuBossKeyMods, settings.JiYuBossKeyVk);
            UpdateHotkeyControls(settings);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void UpdateHotkeyControls(AppSettings settings)
    {
        var enabled = settings.JiYuBossKeyEnabled;
        HotkeyBox.IsEnabled = enabled;
        RecordButton.IsEnabled = enabled;
        TestButton.IsEnabled = enabled;
    }

    private void OnBypassToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        var settings = SaveSetting(s => s.JiYuBypassEnabled = BypassSwitch.IsOn);
        ApplyAndReport(settings);
        RefreshStatus();
    }

    private void OnAllowTopToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        var settings = SaveSetting(s => s.JiYuAllowGbTop = AllowTopSwitch.IsOn);
        ApplyAndReport(settings);
        RefreshStatus();
    }

    private void OnBossToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        var settings = SaveSetting(s => s.JiYuBossKeyEnabled = BossSwitch.IsOn);
        UpdateHotkeyControls(settings);
        ApplyAndReport(settings);
        RefreshStatus();
    }

    private AppSettings SaveSetting(Action<AppSettings> update)
    {
        var settings = AppState.SettingsService.Load();
        update(settings);
        AppState.SettingsService.Save(settings);
        return settings;
    }

    private void ApplyAndReport(AppSettings settings)
    {
        JiYuService.EnsureStarted();
        if (!JiYuService.ApplySettings(settings))
        {
            ShowStatus(Loc.T("Tools_Saved_HostNotRunning"), InfoBarSeverity.Warning);
            return;
        }

        JiYuService.RequestRefresh();
        ShowStatus(Loc.T("Tools_Saved"), InfoBarSeverity.Success);
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        JiYuService.EnsureStarted();
        JiYuService.RequestRefresh();
        RefreshStatus();
    }

    private void OnRecordClick(object sender, RoutedEventArgs e)
    {
        _recording = true;
        RecordButton.Content = Loc.T("Tools_Boss_Recording");
        HotkeyBox.Text = Loc.T("Tools_Boss_PressKeys");
        HotkeyBox.Focus(FocusState.Programmatic);
        ShowStatus(Loc.T("Tools_Boss_CancelHint"), InfoBarSeverity.Informational);
    }

    private void OnHotkeyKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_recording)
        {
            return;
        }

        var vk = (uint)e.Key;
        if (vk == (uint)VirtualKey.Escape)
        {
            _recording = false;
            RefreshText();
            e.Handled = true;
            return;
        }

        if (IsModifierKey(vk))
        {
            e.Handled = true;
            return;
        }

        var mods = CurrentModifiers();
        if (mods == 0)
        {
            ShowStatus(Loc.T("Tools_Boss_NeedModifier"), InfoBarSeverity.Warning);
            e.Handled = true;
            return;
        }

        _recording = false;
        var settings = SaveSetting(s =>
        {
            s.JiYuBossKeyMods = mods;
            s.JiYuBossKeyVk = vk;
            s.JiYuBossKeyEnabled = true;
        });

        _syncing = true;
        BossSwitch.IsOn = true;
        _syncing = false;

        UpdateHotkeyControls(settings);
        RefreshText();
        HotkeyBox.Text = JiYuService.FormatHotkey(mods, vk);
        ApplyAndReport(settings);
        ShowStatus(Loc.Tf("Tools_Boss_Saved_Format", JiYuService.FormatHotkey(mods, vk)), InfoBarSeverity.Success);
        RefreshStatus();
        e.Handled = true;
    }

    private void OnTestClick(object sender, RoutedEventArgs e)
    {
        JiYuService.EnsureStarted();

        var status = JiYuService.ReadStatus();
        if (!status.StudentRunning)
        {
            ShowStatus(Loc.T("Tools_Boss_NoBroadcast"), InfoBarSeverity.Warning);
            return;
        }

        if (!JiYuService.TriggerBossKey())
        {
            ShowStatus(Loc.T("Tools_Boss_NotRunning"), InfoBarSeverity.Warning);
            return;
        }

        ShowStatus(Loc.T("Tools_Boss_TestSent"), InfoBarSeverity.Success);
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var status = JiYuService.ReadStatus();

        if (!status.Available)
        {
            StudentValue.Text = Loc.T("Tools_Status_HostMissing");
            BypassValue.Text = Loc.T("Tools_Status_Unknown");
            BroadcastValue.Text = Loc.T("Tools_Status_Unknown");
            HotkeyValue.Text = Loc.T("Tools_Status_Unknown");
            DetailText.Visibility = Visibility.Collapsed;
            return;
        }

        StudentValue.Text = status.StudentRunning
            ? Loc.Tf("Tools_Status_Student_Running_Format", status.Pid)
            : Loc.T("Tools_Status_Student_None");

        BypassValue.Text = !status.BypassEnabled
            ? Loc.T("Tools_Status_Bypass_Paused")
            : status.Controlled
                ? Loc.T("Tools_Status_Bypass_Active")
                : status.StudentRunning
                    ? Loc.T("Tools_Status_Bypass_Waiting")
                    : Loc.T("Tools_Status_Bypass_NoStudent");

        BroadcastValue.Text = BuildBroadcastText(status);

        var settings = AppState.SettingsService.Load();
        var hotkey = JiYuService.FormatHotkey(settings.JiYuBossKeyMods, settings.JiYuBossKeyVk);
        HotkeyValue.Text = !status.HotkeyEnabled
            ? Loc.T("Tools_Status_Hotkey_Disabled")
            : status.HotkeyRegistered
                ? Loc.Tf("Tools_Status_Hotkey_Registered_Format", hotkey)
                : Loc.Tf("Tools_Status_Hotkey_Unregistered_Format", hotkey);

        if (string.IsNullOrWhiteSpace(status.Message))
        {
            DetailText.Visibility = Visibility.Collapsed;
        }
        else
        {
            DetailText.Text = status.Message;
            DetailText.Visibility = Visibility.Visible;
        }
    }

    private static string BuildBroadcastText(JiYuStatus status)
    {
        var parts = new List<string>();
        if (status.GbFullscreen)
        {
            parts.Add(Loc.T("Tools_Status_Broadcast_Fullscreen"));
        }

        if (status.GbTopmost)
        {
            parts.Add(Loc.T("Tools_Status_Broadcast_Topmost"));
        }

        if (status.InputLocked)
        {
            parts.Add(Loc.T("Tools_Status_Broadcast_Locked"));
        }

        return parts.Count == 0
            ? Loc.T("Tools_Status_Broadcast_None")
            : string.Join(" / ", parts);
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        PageInfoBar.Message = message;
        PageInfoBar.Severity = severity;
        PageInfoBar.IsOpen = true;
    }

    private static bool IsModifierKey(uint vk)
    {
        return vk is 16 or 17 or 18 or 91 or 92 or 160 or 161 or 162 or 163 or 164 or 165;
    }

    private static uint CurrentModifiers()
    {
        var mods = 0u;
        if (IsDown(VirtualKey.Control)) mods |= JiYuService.ModControl;
        if (IsDown(VirtualKey.Menu)) mods |= JiYuService.ModAlt;
        if (IsDown(VirtualKey.Shift)) mods |= JiYuService.ModShift;
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) mods |= JiYuService.ModWin;
        return mods;
    }

    private static bool IsDown(VirtualKey key)
    {
        return (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;
    }
}
