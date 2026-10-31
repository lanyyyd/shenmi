using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using QuickAnswer.App.Interop;
using QuickAnswer.App.Services;
using QuickAnswer.Core.Api;
using QuickAnswer.Core.Models;
using Windows.System;

namespace QuickAnswer.App.Views;

public sealed partial class LoginWindow : Window
{
    private bool _busy;

    public LoginWindow()
    {
        InitializeComponent();

        Title = "QuickAnswer · 登录";
        WindowHelper.Resize(this, 420, 600);
        WindowHelper.CenterOnScreen(this, 420, 600);
        WindowHelper.HideFromSwitcher(this);

        UserBox.Text = App.Config.Username ?? "";
        ServerBox.Text = App.Config.ServerUrl;
        PassBox.Focus(FocusState.Programmatic);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            _ = DoLoginAsync();
        }
    }

    private void OnLoginClick(object sender, RoutedEventArgs e) => _ = DoLoginAsync();

    private async Task DoLoginAsync()
    {
        if (_busy)
            return;

        var user = UserBox.Text.Trim();
        var pass = PassBox.Password;
        var server = ServerBox.Text.Trim().TrimEnd('/');

        if (user.Length == 0 || pass.Length == 0)
        {
            ShowError("请填写账号和密码");
            return;
        }

        if (server.Length == 0 || !server.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            ShowError("服务器地址需要以 http:// 或 https:// 开头");
            return;
        }

        SetBusy(true);
        HideError();
        StatusText.Text = "正在连接 " + server;

        UserDto u;
        try
        {
            App.Api.Configure(server, null);
            u = await App.Api.LoginAsync(user, pass);
        }
        catch (ApiException ex)
        {
            // 服务端明确拒绝（密码错、账号禁用、地址不对…）
            ShowError(ex.Message);
            StatusText.Text = "未连接";
            SetBusy(false);
            return;
        }
        catch (Exception ex)
        {
            CrashLog.Write("LoginWindow.Network", ex);
            ShowError("网络请求失败：" + ex.Message);
            StatusText.Text = "未连接";
            SetBusy(false);
            return;
        }

        App.Config.ServerUrl = server;
        App.Config.Token = App.Api.AccessToken;
        App.Config.Username = u.Username;
        App.Config.Nickname = u.Nickname;
        App.Config.Save();

        App.Session.SignIn(u, App.Api.AccessToken ?? "");
        StatusText.Text = "登录成功";
        SetBusy(false);

        // 切窗口放在 try 之外：主界面自己的 XAML 出错时，不能再被伪装成「连接失败」，
        // 否则用户会去反复检查密码和服务器地址，而问题根本不在那里。
        try
        {
            App.ShowMain();
        }
        catch (Exception ex)
        {
            CrashLog.Write("LoginWindow.ShowMain", ex);
            ShowError("登录成功，但主界面加载失败：" + ex.Message);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Busy.IsActive = busy;
        LoginBtn.IsEnabled = !busy;
        UserBox.IsEnabled = !busy;
        PassBox.IsEnabled = !busy;
        ServerBox.IsEnabled = !busy;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorText.Visibility = Visibility.Collapsed;
}
