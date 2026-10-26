using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Services;

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
        }
        finally
        {
            _syncing = false;
        }
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
