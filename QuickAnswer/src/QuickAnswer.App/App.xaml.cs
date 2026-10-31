using Microsoft.UI.Xaml;
using QuickAnswer.App.Services;
using QuickAnswer.App.Views;
using QuickAnswer.Core.Api;
using QuickAnswer.Core.Storage;

namespace QuickAnswer.App;

public partial class App : Application
{
    /// <summary>全局单例。项目规模小，用一个静态门面足够，不值得上 DI 容器。</summary>
    public static AppConfig Config { get; private set; } = new();
    public static ApiClient Api { get; private set; } = new();
    public static Session Session { get; private set; } = new();

    public static Window? CurrentWindow { get; private set; }

    public App()
    {
        InitializeComponent();

        // XAML 解析异常在 WinUI 里只会给一句 "Failed to assign to property ..."，没有行号，
        // 不落盘就完全没法查。所有未处理异常都写进 error.log。
        UnhandledException += (_, e) =>
        {
            CrashLog.Write("App.UnhandledException", e.Exception);
            e.Handled = false;
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Config = AppConfig.Load();
        Config.RollDayIfNeeded();

        Api.Configure(Config.ServerUrl, Config.Token);

        // 有 token 就先验一次：7 天过期的 JWT 在本地看不出，必须问服务端。
        if (!string.IsNullOrEmpty(Config.Token))
        {
            var (state, me) = await Api.VerifyTokenAsync();

            if (state == TokenState.Valid && me is not null)
            {
                // 记住登录：直接进主界面，不再要求重新输密码
                Session.SignIn(me, Config.Token);
                ShowMain();
                return;
            }

            if (state == TokenState.Unauthorized)
            {
                // 只有服务端明确说 token 失效才清掉
                Config.Token = null;
                Config.Save();
            }

            // NetworkError：token 原样保留，下次有网还能自动登录
        }

        ShowLogin();
    }

    public static void ShowLogin()
    {
        var w = new LoginWindow();
        SwitchTo(w);
    }

    public static void ShowMain()
    {
        var w = new MainWindow();
        SwitchTo(w);
    }

    private static void SwitchTo(Window window)
    {
        var old = CurrentWindow;
        CurrentWindow = window;
        window.Activate();

        // 先 Activate 再关旧窗口：反过来的话，关闭最后一个窗口会触发进程退出
        if (old is not null && !ReferenceEquals(old, window))
        {
            try
            {
                old.Close();
            }
            catch
            {
            }
        }
    }
}
