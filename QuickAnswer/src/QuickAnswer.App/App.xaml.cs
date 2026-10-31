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

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Config = AppConfig.Load();
        Config.RollDayIfNeeded();

        Api.Configure(Config.ServerUrl, Config.Token);

        // 先把登录窗口显示出来，再去后台校验 token。
        // 反过来的话，服务器不可达时用户会对着空桌面等满一个网络超时，
        // 看起来就是「软件启动了但没有界面」。
        ShowLogin();

        if (!string.IsNullOrEmpty(Config.Token))
            _ = TryAutoLoginAsync();
    }

    /// <summary>静默续期：token 还有效就直接切到主界面，用户不用再输密码。</summary>
    private static async Task TryAutoLoginAsync()
    {
        try
        {
            // 这一步在后台跑，失败也不打扰用户 —— 登录页已经在那儿了
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var (state, me) = await Api.VerifyTokenAsync(cts.Token);

            if (state == TokenState.Valid && me is not null && !string.IsNullOrEmpty(Config.Token))
            {
                Session.SignIn(me, Config.Token);
                ShowMain();
                return;
            }

            if (state == TokenState.Unauthorized)
            {
                // 只有服务端明确说 token 失效才清掉；断网时原样保留
                Config.Token = null;
                Config.Save();
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("App.TryAutoLogin", ex);
        }
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
