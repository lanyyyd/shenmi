using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
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
    private const nuint WindowSubclassId = 1;

    private static nint s_hwnd;

    // Informational/Success 状态若干秒后自动收起；Warning/Error 常驻，直到被替换或用户手动关闭。
    private static readonly TimeSpan StatusAutoDismissDelay = TimeSpan.FromSeconds(6);
    private readonly DispatcherQueueTimer _statusDismissTimer;

    public static MainWindow? Instance { get; private set; }

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

        // 关窗口时停掉背景视频，及时释放解码器与文件句柄。
        Closed += (_, _) => StopBackgroundVideo();

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
        TitleVersionText.Text = $"v{GitHubUpdateService.CurrentVersion}";

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

        // 预载历史账号，登录页的头像/资料复用依赖该缓存。
        AppState.ReloadHistory();
        ApplyAccessMode();

        // 首次启动即解析并持久化 Steam 安装路径（之后上号直接复用，不再每次探测）。
        // 等内容进入可视树（XamlRoot 就绪）后再跑，检测失败才需要弹框。
        RootNavigationView.Loaded += OnRootNavigationViewLoaded;

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
            HistoryNavItem.Visibility = Visibility.Collapsed;
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
            var (ok, pushed, msg) = await new SyncService().TrySyncAsync();
            if (ok && pushed > 0)
            {
                ShowStatus($"已同步 {pushed} 条离线聊天记录", InfoBarSeverity.Success);
            }
            else if (!ok && !string.IsNullOrEmpty(msg))
            {
                AppLog.Info($"离线记录同步跳过：{msg}");
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

    /// <summary>历史页“载入到登录页”：切到登录页并填充账号。</summary>
    public void LoadAccountIntoLogin(SteamAccountHistoryItem account)
    {
        RootNavigationView.SelectedItem = LoginNavItem;
        AppState.LoginPage?.LoadHistoryAccount(account);
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
            "history" => typeof(HistoryPage),
            "cachedAccounts" => typeof(CachedAccountsPage),
            "cloudSave" => typeof(CloudSavePage),
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
        HistoryNavItem.Content = Loc.T("Nav_History");
        CachedAccountsNavItem.Content = Loc.T("Nav_CachedAccounts");
        CloudSaveNavItem.Content = Loc.T("Nav_CloudSave");
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
