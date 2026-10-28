using Microsoft.UI.Xaml;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Services;

namespace SteamEyaWinUI;

public partial class App : Application
{
    private Window? _window;

    /// <summary>
    /// 界面当前实际主题。主题设在窗口根元素上（见 MainWindow.ApplyTheme），
    /// Application.RequestedTheme 始终是启动值，故需从根元素 ActualTheme 读取。
    /// </summary>
    internal static ElementTheme ActualTheme =>
        Current is App app && app._window?.Content is FrameworkElement root
            ? root.ActualTheme
            : ElementTheme.Default;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 在任何窗口/页面构造前确定界面语言，保证首帧即用所选语言渲染。
        Loc.Initialize(AppState.SettingsService);
        var startupSettings = AppState.SettingsService.Load();

        // 调试模式：只影响 DEBUG 级别日志，必须先于其它服务初始化，否则会漏掉启动阶段的过程日志。
        AppLog.SetDebugEnabled(startupSettings.DebugMode);

        // 先把 Aurora 主题色算好再建窗口：登录页出现的瞬间就是用户选定的主题色，
        // 不会先闪一帧默认粉紫色再跳。
        AppState.UiColorService.Apply(startupSettings.UiColor, startupSettings.UiColorAnimated, startupSettings.Theme);

        SessionState.Initialize();

        // 极域助手不依赖登录：开启「极域注入」时，客户端一启动就拉起内置原版 JiYuTrainer
        //（静默启动并注入）；未安装极域时它只等待，不影响主界面。
        // 无极域版没有内置助手，也不做任何注入。
        if (!AppEdition.IsNoJiYu && startupSettings.JiYuEnabled)
        {
            JiYuService.Start();
        }

        // 启动即注册系统通知通道：不依赖「登录后 / 打开聊天页」这些更晚的时机，
        // 避免把注册失败拖到用户已经错过提醒之后。注册失败只写日志，不影响启动。
        NotificationService.Initialize();

        // 先显示统一登录页；登录成功后再进入主界面（见 LoginWindow.GoToMain）。
        _window = new LoginWindow();
        _window.Activate();
    }

    /// <summary>登录成功后由 LoginWindow 调用：切换到主界面。</summary>
    internal void ShowMainWindow()
    {
        _window = new MainWindow();
        _window.Activate();
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // 崩溃前尽力落盘，便于排查 AOT 产物上的现场问题。
        try
        {
            var logFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SteamEYA");
            Directory.CreateDirectory(logFolder);
            File.AppendAllText(
                Path.Combine(logFolder, "crash.log"),
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}{Environment.NewLine}");
        }
        catch
        {
            // 日志写入失败不影响异常继续传播。
        }
    }
}
