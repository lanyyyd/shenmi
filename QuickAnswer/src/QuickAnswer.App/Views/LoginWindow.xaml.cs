using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using QuickAnswer.App.Interop;
using QuickAnswer.Core.Api;
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

        try
        {
            App.Api.Configure(server, null);
            var u = await App.Api.LoginAsync(user, pass);

            App.Config.ServerUrl = server;
            App.Config.Token = App.Api.AccessToken;
            App.Config.Username = u.Username;
            App.Config.Nickname = u.Nickname;
            App.Config.Save();

            App.Session.SignIn(u, App.Api.AccessToken ?? "");
            StatusText.Text = "登录成功";
            App.ShowMain();
        }
        catch (ApiException ex)
        {
            ShowError(ex.Message);
            StatusText.Text = "未连接";
        }
        catch (Exception ex)
        {
            ShowError("连接失败：" + ex.Message);
            StatusText.Text = "未连接";
        }
        finally
        {
            SetBusy(false);
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
