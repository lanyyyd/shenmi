using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;
using SteamEyaWinUI.Pages;
using SteamEyaWinUI.Services;
using Windows.Graphics;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace SteamEyaWinUI;

public sealed partial class MainWindow : Window
{
    private const int InitialWindowWidth = 1280;
    private const int InitialWindowHeight = 860;
    private const int MinWindowWidth = 1180;
    private const int MinWindowHeight = 780;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint WmHotkey = 0x0312;
    private const nuint WindowSubclassId = 1;

    private static nint s_hwnd;

    // Informational/Success 状态若干秒后自动收起；Warning/Error 常驻，直到被替换或用户手动关闭。
    private static readonly TimeSpan StatusAutoDismissDelay = TimeSpan.FromSeconds(6);
    private readonly DispatcherQueueTimer _statusDismissTimer;

    public static MainWindow? Instance { get; private set; }

    // 关闭确认：_exiting 用于区分「用户确认退出」与「点了 X 但被拦下来询问」。
    private bool _exiting;
    private bool _closeDialogOpen;

    /// <summary>主窗口句柄，供文件/目录选择器等 WinRT 互操作（InitializeWithWindow）使用；在 ConfigureWindowSize 中赋值。</summary>
    public static nint Hwnd => s_hwnd;

    /// <summary>主窗口当前是否处于激活（前台）状态：聊天室据此判断是否需要弹系统通知/闪任务栏。</summary>
    public static bool IsActive { get; private set; } = true;

    public MainWindow()
    {
        Instance = this;

        InitializeComponent();

        Activated += (_, e) => IsActive = e.WindowActivationState != WindowActivationState.Deactivated;

        // 背景视频的播放器：MediaPlayerElement.MediaPlayer 只读，必须显式创建再 SetMediaPlayer，
        // 否则永远是 null、视频背景播不出来。
        _backgroundPlayer = new MediaPlayer
        {
            IsLoopingEnabled = true,
            IsMuted = true
        };
        CustomBackgroundMedia.SetMediaPlayer(_backgroundPlayer);

        // 通知需要回到 UI 线程，这里把窗口的派发器交给常驻聊天服务。
        ChatHub.AttachUi(DispatcherQueue);
        // 系统通知：注入 UI 派发器、确保注册，并接管「点击通知 → 跳到对应聊天房间」。
        NotificationService.SetUiDispatcher(DispatcherQueue);
        NotificationService.Initialize();
        NotificationService.Invoked += OnNotificationInvoked;

        // 关窗口时：停背景视频释放解码器与文件句柄；断开常驻聊天；
        // 结束本机拉起的网关 / 云游戏引擎（含 PyInstaller onefile 派生的子进程与浏览器驱动），避免残留。
        Closed += (_, _) =>
        {
            StopBackgroundVideo();
            ChatHub.Shutdown();
            EngineLauncher.Stop();
            GatewayLauncher.Stop();
            JiYuService.Stop();
        };

        // 背景层按系统能力挑：Windows 11 用 Mica；Windows 10 没有 Mica（设了会让边框/内容缝隙透出黑色，
        // 表现为四周黑边），退一档到桌面亚克力；都不可用就不设背景层，由根 Grid 的渐变兜底。
        SystemBackdrop = MicaController.IsSupported()
            ? new MicaBackdrop()
            : DesktopAcrylicController.IsSupported()
                ? new DesktopAcrylicBackdrop()
                : null;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SetWindowIcon();
        TitleVersionText.Text = $"v{UpdateService.CurrentVersion}";

        var startupSettings = AppState.SettingsService.Load();
        ApplyTheme(UiColorService.ResolveElementTheme(startupSettings.Theme, startupSettings.UiColor));
        ApplyCustomBackground(startupSettings);
        RefreshNavText();
        Loc.LanguageChanged += RefreshNavText;

        _statusDismissTimer = DispatcherQueue.CreateTimer();
        _statusDismissTimer.Interval = StatusAutoDismissDelay;
        _statusDismissTimer.IsRepeating = false;
        _statusDismissTimer.Tick += (_, _) => StatusInfoBar.IsOpen = false;

        AppState.StatusReporter = ShowStatus;
        AppState.BusyChanged += OnBusyChanged;

        ConfigureWindowSize();

        // 截图全局热键：必须等 hwnd 就绪后再注册（失败说明被其它程序占用，状态栏会提示）。
        ApplyScreenshotHotkey(startupSettings);

        // 关闭拦截：先问「隐藏到托盘还是退出」，再决定是否真的关窗。
        AppWindow.Closing += OnAppWindowClosing;

        ApplyAccessMode();

        // 首次启动即解析并持久化 Steam 安装路径（之后上号直接复用，不再每次探测）。
        // 等内容进入可视树（XamlRoot 就绪）后再跑，检测失败才需要弹框。
        RootNavigationView.Loaded += OnRootNavigationViewLoaded;
        // 界面进入可视树（XamlRoot 就绪）后再检查更新并弹「检测到新版本」。
        RootNavigationView.Loaded += (_, _) => _ = RunStartupUpdateCheckAsync();

        // 在线状态下：把离线期间产生的聊天记录幂等同步到服务器。
        if (!SessionState.Offline)
        {
            _ = RunStartupSyncAsync();
        }
    }

    /// <summary>按在线/离线模式决定可用导航项：离线仅保留「局域网聊天」。</summary>
    private void ApplyAccessMode()
    {
        if (SessionState.Offline)
        {
            LoginNavItem.Visibility = Visibility.Collapsed;
            CloudGameNavItem.Visibility = Visibility.Collapsed;
            CachedAccountsNavItem.Visibility = Visibility.Collapsed;
            CloudSaveNavItem.Visibility = Visibility.Collapsed;
            SettingsNavItem.Visibility = Visibility.Collapsed;
            AboutNavItem.Visibility = Visibility.Collapsed;
            RootNavigationView.SelectedItem = ChatNavItem;
        }
        else
        {
            RootNavigationView.SelectedItem = LoginNavItem;
        }
    }

    private async Task RunStartupSyncAsync()
    {
        try
        {
            // 离线期间的消息存在「本机网关」的数据库里，联网启动时网关并未运行，
            // 必须先把它拉起来，否则读 /api/sync/pending 必然失败、同步被静默跳过。
            await GatewayLauncher.EnsureStartedAsync(SessionState.Nickname);

            var (ok, pushed, msg) = await new SyncService().TrySyncAsync();
            if (ok && pushed > 0)
            {
                ShowStatus($"已同步 {pushed} 条离线聊天记录", InfoBarSeverity.Success);
            }
            else if (!ok && !string.IsNullOrEmpty(msg))
            {
                // 直接显示出来，方便用户/我们定位是网关没起来、还是服务端返回了拒绝。
                AppLog.Warn($"离线记录同步失败：{msg}");
                ShowStatus($"离线记录同步失败：{msg}", InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("启动同步失败。", ex);
        }
    }

    // 启动时解析 Steam 路径：成功则静默持久化；自动检测失败时弹框让用户手动选择含 steam.exe 的目录。
    private async void OnRootNavigationViewLoaded(object sender, RoutedEventArgs e)
    {
        RootNavigationView.Loaded -= OnRootNavigationViewLoaded;
        try
        {
            await SteamPathCoordinator.EnsureResolvedAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("启动时解析 Steam 安装路径失败。", ex);
        }
    }

    // ---------------- 检查更新（自建更新源） ----------------

    /// <summary>启动时自动检查更新；发现新版本弹「检测到新版本」。离线模式跳过。</summary>
    private async Task RunStartupUpdateCheckAsync()
    {
        if (SessionState.Offline)
        {
            return;
        }

        try
        {
            await AppState.CheckForUpdatesAsync(isAutomatic: true);
            if (AppState.LatestUpdate is { HasUpdate: true } info)
            {
                await ShowUpdateDialogAsync(info);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"启动检查更新失败：{ex.Message}");
        }
    }

    /// <summary>「检测到新版本」对话框：更新内容 / 版本号 / 文件大小 / 更新时间 + 立即更新 / 下次再说。</summary>
    private async Task ShowUpdateDialogAsync(AppUpdateInfo info)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = Loc.Tf("Update_Dialog_Version_Format", info.Version),
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
        });
        if (!string.IsNullOrWhiteSpace(info.Notes))
        {
            panel.Children.Add(new TextBlock { Text = info.Notes, TextWrapping = TextWrapping.Wrap });
        }
        panel.Children.Add(new TextBlock
        {
            Text = Loc.Tf("Update_Dialog_Size_Format", UpdateFlow.FormatBytes(info.FileSize)),
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        panel.Children.Add(new TextBlock
        {
            Text = Loc.Tf("Update_Dialog_Time_Format", UpdateFlow.FormatTime(info.UpdatedAt)),
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = Loc.T("Update_Dialog_Title"),
            Content = new ScrollViewer { Content = panel, MaxHeight = 360 },
            PrimaryButtonText = Loc.T("Update_Dialog_Now"),
            CloseButtonText = Loc.T("Update_Dialog_Later"),
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await UpdateFlow.StartAsync(info, Close);
        }
    }

    /// <summary>注册/重注册截图热键；设置页改键后也走这里。</summary>
    internal static bool ApplyScreenshotHotkey(AppSettings settings)
    {
        if (s_hwnd == 0)
        {
            return false;
        }

        var ok = ScreenshotService.RegisterHotkey(
            s_hwnd, settings.ScreenshotHotkeyMods, settings.ScreenshotHotkeyVk);

        if (!ok)
        {
            Instance?.ShowStatus(Loc.T("Screenshot_Hotkey_Conflict"), InfoBarSeverity.Warning);
        }

        return ok;
    }

    // ---------------- 关闭确认与托盘 ----------------

    private static bool IsExitAction(string? action) =>
        string.Equals(action, "exit", StringComparison.OrdinalIgnoreCase);

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting)
        {
            return;
        }

        // 先无条件取消，等用户选完再决定是否真的 Close()。
        args.Cancel = true;

        if (_closeDialogOpen)
        {
            return;
        }

        _closeDialogOpen = true;
        try
        {
            var settings = AppState.SettingsService.Load();

            if (settings.RememberCloseAction)
            {
                if (IsExitAction(settings.CloseAction))
                {
                    ExitFromTray();
                }
                else
                {
                    HideToTray();
                }

                return;
            }

            var (action, remember) = await ShowCloseDialogAsync(settings);
            if (action is null)
            {
                return;   // 用户点了取消
            }

            if (remember)
            {
                settings.RememberCloseAction = true;
                settings.CloseAction = action;
                AppState.SettingsService.Save(settings);
            }

            if (IsExitAction(action))
            {
                ExitFromTray();
            }
            else
            {
                HideToTray();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("处理关闭确认失败。", ex);
        }
        finally
        {
            _closeDialogOpen = false;
        }
    }

    private async Task<(string? Action, bool Remember)> ShowCloseDialogAsync(AppSettings settings)
    {
        var options = new RadioButtons { MaxColumns = 1 };
        options.Items.Add(Loc.T("Close_Tray"));
        options.Items.Add(Loc.T("Close_Exit"));
        options.SelectedIndex = IsExitAction(settings.CloseAction) ? 1 : 0;

        var remember = new CheckBox
        {
            Content = Loc.T("Close_Remember"),
            IsChecked = settings.RememberCloseAction
        };

        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(options);
        panel.Children.Add(remember);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = Loc.T("Close_Title"),
            Content = panel,
            PrimaryButtonText = Loc.T("Common_Confirm"),
            CloseButtonText = Loc.T("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return (null, false);
        }

        return (options.SelectedIndex == 1 ? "exit" : "tray", remember.IsChecked == true);
    }

    private void HideToTray()
    {
        TrayIconService.Show(s_hwnd, Loc.T("Tray_Tip"));
        AppWindow.Hide();
    }

    internal void RestoreFromTray()
    {
        try
        {
            AppWindow.Show();
            Activate();
            TrayIconService.Hide(s_hwnd);
            SetForegroundWindow(s_hwnd);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"从托盘恢复窗口失败：{ex.Message}");
        }
    }

    internal void ExitFromTray()
    {
        _exiting = true;
        ToastWindow.CloseAll();
        TrayIconService.Hide(s_hwnd);
        Close();
    }

    public void ShowStatus(string message, InfoBarSeverity severity)
    {
        // 状态可能来自后台线程（如登录后的 CS2 云推送进度），统一封送到 UI 线程。
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => ShowStatus(message, severity));
            return;
        }

        StatusInfoBar.Message = message;
        StatusInfoBar.Severity = severity;
        StatusInfoBar.IsOpen = true;

        _statusDismissTimer.Stop();
        if (severity is InfoBarSeverity.Informational or InfoBarSeverity.Success)
        {
            _statusDismissTimer.Start();
        }
    }

    private void RootNavigationView_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is string pageName)
        {
            NavigateTo(pageName);
        }
    }

    private void NavigateTo(string pageName)
    {
        var pageType = pageName switch
        {
            "cloudGame" => typeof(CloudGamePage),
            "chat" => typeof(ChatPage),
            "toolbox" => typeof(ToolboxPage),
            "cachedAccounts" => typeof(CachedAccountsPage),
            "cloudSave" => typeof(CloudSavePage),
            "lanTransfer" => typeof(LanTransferPage),
            "tools" => typeof(ToolsPage),
            "settings" => typeof(SettingsPage),
            "about" => typeof(AboutPage),
            _ => typeof(LoginPage)
        };

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType, null, new EntranceNavigationTransitionInfo());
            ContentFrame.BackStack.Clear();
        }
    }

    /// <summary>点击系统通知：把窗口带到前台并打开通知对应的聊天房间（argument 形如 room=group / room=pm:1:2）。</summary>
    private void OnNotificationInvoked(string argument)
    {
        // 下载完成的通知带 openpath=<目录>：点开后直接定位到下载好的文件所在文件夹。
        if (TryHandleOpenPath(argument))
        {
            return;
        }

        var room = ParseRoomArgument(argument);
        if (string.IsNullOrEmpty(room))
        {
            return;
        }
        try
        {
            Activate();
            ChatHub.RequestOpenRoom(room!);
            RootNavigationView.SelectedItem = ChatNavItem;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"处理通知点击失败：{ex.Message}");
        }
    }

    /// <summary>处理「打开下载目录」类通知参数。</summary>
    private static bool TryHandleOpenPath(string argument)
    {
        const string prefix = "openpath=";
        if (!argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = Uri.UnescapeDataString(argument[prefix.Length..]);
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            AppLog.Warn($"通知指定的目录不存在：{path}");
            return true;    // 参数已经消费掉，不要再当房间参数解析
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"打开目录失败：{ex.Message}");
        }

        return true;
    }

    /// <summary>从通知参数里取出 room 值；参数是 key=value&... 形式，值做过 URL 编码。</summary>
    private static string? ParseRoomArgument(string argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            return null;
        }
        foreach (var pair in argument.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            var key = idx < 0 ? pair : pair[..idx];
            if (key != "room")
            {
                continue;
            }
            var value = idx < 0 ? string.Empty : pair[(idx + 1)..];
            return Uri.UnescapeDataString(value);
        }
        return null;
    }

    private void OnBusyChanged(bool isBusy)
    {
        BusyRing.IsActive = isBusy;
        BusyRing.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>把主题套用到内容根（无打包下 Application.RequestedTheme 不可后置，故走根元素 RequestedTheme）。</summary>
    public void ApplyTheme(ElementTheme theme)
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = theme;
        }
    }

    /// <summary>自定义背景视频的播放器（静音循环）。</summary>
    private readonly MediaPlayer? _backgroundPlayer;

    /// <summary>视频类背景扩展名（与设置页文件选择器、SettingsService 白名单保持一致）。</summary>
    private static readonly string[] CustomBackgroundVideoExtensions =
        [".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv"];

    /// <summary>让背景图的布局尺寸始终跟随窗口客户区，GIF 和大图也能实时缩放。</summary>
    private void RootLayoutGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0)
        {
            return;
        }

        CustomBackgroundImage.Width = e.NewSize.Width;
        CustomBackgroundImage.Height = e.NewSize.Height;
    }

    /// <summary>按当前设置加载自定义背景。</summary>
    internal void ApplyCustomBackground(AppSettings settings)
    {
        ApplyCustomBackground(
            AppState.SettingsService.GetCustomBackgroundImagePath(settings),
            settings.CustomBackgroundEnabled,
            settings.CustomBackgroundOpacity);
    }

    /// <summary>应用或隐藏自定义背景：图片走 Image（保留 GIF 动画），视频走 MediaPlayerElement（静音循环）。</summary>
    internal void ApplyCustomBackground(string? path, bool enabled, double opacity)
    {
        // 两层先都收起来：图片与视频互斥，切换后不能叠在一起。
        HideBackgroundImage();
        StopBackgroundVideo();

        if (!enabled || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        var clamped = Math.Clamp(opacity, 0.1, 0.9);
        if (CustomBackgroundVideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        {
            PlayBackgroundVideo(path, clamped);
            return;
        }

        try
        {
            CustomBackgroundImage.Source = new BitmapImage
            {
                UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute),
                CreateOptions = BitmapCreateOptions.IgnoreImageCache
            };
            CustomBackgroundImage.Opacity = clamped;
            CustomBackgroundImage.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            HideBackgroundImage();
            AppLog.Warn($"加载自定义背景失败：{ex.Message}");
        }
    }

    private void PlayBackgroundVideo(string path, double opacity)
    {
        try
        {
            var player = _backgroundPlayer
                ?? throw new InvalidOperationException("背景视频的播放器未初始化。");

            player.Source = MediaSource.CreateFromUri(new Uri(Path.GetFullPath(path), UriKind.Absolute));
            CustomBackgroundMedia.Opacity = opacity;
            CustomBackgroundMedia.Visibility = Visibility.Visible;
            player.Play();
        }
        catch (Exception ex)
        {
            StopBackgroundVideo();
            AppLog.Warn($"加载自定义背景视频失败：{ex.Message}");
        }
    }

    private void StopBackgroundVideo()
    {
        try
        {
            if (_backgroundPlayer is { } player)
            {
                player.Pause();
                player.Source = null;   // 释放文件句柄，否则换背景/删文件会被占用
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"停止背景视频失败：{ex.Message}");
        }

        CustomBackgroundMedia.Opacity = 0;
        CustomBackgroundMedia.Visibility = Visibility.Collapsed;
    }

    private void HideBackgroundImage()
    {
        CustomBackgroundImage.Source = null;
        CustomBackgroundImage.Opacity = 0;
        CustomBackgroundImage.Visibility = Visibility.Collapsed;
    }

    /// <summary>本地化导航项文字；语言切换时由 Loc.LanguageChanged 再次调用。</summary>
    private void RefreshNavText()
    {
        LoginNavItem.Content = Loc.T("Nav_Login");
        CloudGameNavItem.Content = Loc.T("Nav_CloudGame");
        ChatNavItem.Content = Loc.T("Nav_Chat");
        ToolboxNavItem.Content = Loc.T("Nav_Toolbox");
        CachedAccountsNavItem.Content = Loc.T("Nav_CachedAccounts");
        CloudSaveNavItem.Content = Loc.T("Nav_CloudSave");
        LanTransferNavItem.Content = Loc.T("Nav_LanTransfer");
        ToolsNavItem.Content = Loc.T("Nav_Tools");
        SettingsNavItem.Content = Loc.T("Nav_Settings");
        AboutNavItem.Content = Loc.T("Nav_About");
    }

    private void SetWindowIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }
    }

    private unsafe void ConfigureWindowSize()
    {
        s_hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(s_hwnd) / 96.0;

        AppWindow.Resize(new SizeInt32(
            (int)Math.Ceiling(InitialWindowWidth * scale),
            (int)Math.Ceiling(InitialWindowHeight * scale)));

        // 用 WM_GETMINMAXINFO 子类化实时按当前 DPI 计算最小尺寸，
        // 跨多显示器 / DPI 变化时 OverlappedPresenter.PreferredMinimum*（启动时固定的物理像素）会失效。
        SetWindowSubclass(s_hwnd, &SubclassProc, WindowSubclassId, 0);
        Closed += OnClosed;
    }

    private unsafe void OnClosed(object sender, WindowEventArgs args)
    {
        RemoveWindowSubclass(s_hwnd, &SubclassProc, WindowSubclassId);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe nint SubclassProc(
        nint hWnd,
        uint message,
        nint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData)
    {
        if (message == TrayIconService.WmTrayIcon)
        {
            try
            {
                if (TrayIconService.HandleCallback(hWnd, lParam, out var restore) && restore)
                {
                    Instance?.RestoreFromTray();
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"处理托盘消息失败：{ex.Message}");
            }

            return 0;
        }

        if (message == TrayIconService.WmTrayCommand)
        {
            var command = (int)wParam;
            if (command == TrayIconService.CommandShow)
            {
                Instance?.RestoreFromTray();
            }
            else if (command == TrayIconService.CommandExit)
            {
                Instance?.ExitFromTray();
            }

            return 0;
        }

        if (message == WmHotkey)
        {
            if ((int)wParam == ScreenshotService.HotkeyId)
            {
                // UnmanagedCallersOnly 里不能让异常跨边界，兜底住。
                try
                {
                    ScreenshotService.BeginCapture();
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"截图触发失败：{ex.Message}");
                }

                return 0;
            }
        }

        if (message == WmGetMinMaxInfo)
        {
            var scale = GetDpiForWindow(hWnd) / 96.0;
            var info = (MinMaxInfo*)lParam;
            info->MinTrackSize.X = (int)Math.Ceiling(MinWindowWidth * scale);
            info->MinTrackSize.Y = (int)Math.Ceiling(MinWindowHeight * scale);
            return 0;
        }

        return DefSubclassProc(hWnd, message, wParam, lParam);
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetWindowSubclass(
        nint hWnd,
        delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nuint, nint> callback,
        nuint subclassId,
        nuint referenceData);

    [LibraryImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool RemoveWindowSubclass(
        nint hWnd,
        delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nuint, nint> callback,
        nuint subclassId);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint hWnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }
}
