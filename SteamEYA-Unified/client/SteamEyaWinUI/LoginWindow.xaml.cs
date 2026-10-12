using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SteamEyaWinUI.Services;

namespace SteamEyaWinUI;

/// <summary>统一登录窗口：软件打开后的第一个界面。登录成功后进入主界面。</summary>
public sealed partial class LoginWindow : Window
{
    private readonly CloudAuthService _auth = new();
    private readonly RememberCredentialStore _remember = new();
    private bool _online;

    public LoginWindow()
    {
        InitializeComponent();
        Title = "SteamEYA 登录";

        // 登录窗口先于主窗口出现，这里也要按用户设置套一层主题，
        // 否则系统主题与所选主题不一致时，Aurora 文字/输入框会与底色对不上（白底白字）。
        var settings = AppState.SettingsService.Load();
        RootGrid.RequestedTheme = UiColorService.ResolveElementTheme(settings.Theme, settings.UiColor);

        SessionState.Initialize();
        PrefillRemembered();
        _ = DetectNetworkAsync();
    }

    /// <summary>若此前勾选过「记住密码」，回填用户名与密码（仅自动填写，不自动登录）。</summary>
    private void PrefillRemembered()
    {
        var saved = _remember.Load();
        if (saved is null)
        {
            return;
        }

        UserBox.Text = saved.Value.Username;
        PassBox.Password = saved.Value.Password;
        RememberBox.IsChecked = true;
    }

    private async Task DetectNetworkAsync()
    {
        _online = await SessionState.ProbeOnlineAsync();
        if (_online)
        {
            NetworkText.Text = "网络状态：在线";
            OfflineButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            NetworkText.Text = "网络状态：离线（可进入局域网模式）";
            OfflineButton.Visibility = Visibility.Visible;
        }
    }

    private void OnTabChanged(object sender, RoutedEventArgs e)
    {
        if (NickBox is null)
        {
            return;
        }
        var register = TabRegister.IsChecked == true;
        NickBox.Visibility = register ? Visibility.Visible : Visibility.Collapsed;
        RememberBox.Visibility = register ? Visibility.Collapsed : Visibility.Visible;
        AuthButton.Content = register ? "注 册" : "登 录";
    }

    private async void OnAuthClick(object sender, RoutedEventArgs e)
    {
        var username = UserBox.Text.Trim();
        var password = PassBox.Password;
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            ShowInfo("请输入用户名和密码", InfoBarSeverity.Warning);
            return;
        }

        AuthButton.IsEnabled = false;
        try
        {
            var register = TabRegister.IsChecked == true;
            var (ok, result, error) = register
                ? await _auth.RegisterAsync(username, password, NickBox.Text.Trim())
                : await _auth.LoginAsync(username, password);

            if (!ok || result?.AccessToken is null)
            {
                ShowInfo(error ?? "操作失败", InfoBarSeverity.Error);
                return;
            }

            SessionState.EnterOnline(result.AccessToken, result.User?.Username ?? username,
                result.User?.Nickname ?? username);

            // 「记住密码」仅在登录时生效：勾选则加密保存，取消则清除已存凭据。
            if (!register)
            {
                if (RememberBox.IsChecked == true)
                {
                    _remember.Save(username, password);
                }
                else
                {
                    _remember.Clear();
                }
            }

            GoToMain();
        }
        finally
        {
            AuthButton.IsEnabled = true;
        }
    }

    private async void OnOfflineClick(object sender, RoutedEventArgs e)
    {
        var input = new TextBox
        {
            PlaceholderText = "输入昵称",
            Header = "昵称（务必与在线聊天用户名一致）"
        };
        var tip = new TextBlock
        {
            Text = "务必使用和在线聊天一模一样的用户名以保证后续聊天记录正常同步！！",
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var panel = new StackPanel();
        panel.Children.Add(input);
        panel.Children.Add(tip);

        var dialog = new ContentDialog
        {
            Title = "离线模式 · 设置昵称",
            Content = panel,
            PrimaryButtonText = "进入",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var nickname = input.Text.Trim();
        if (nickname.Length < 2)
        {
            ShowInfo("昵称至少 2 位", InfoBarSeverity.Warning);
            return;
        }

        ShowInfo("正在启动本地网关…", InfoBarSeverity.Informational);
        var started = await GatewayLauncher.EnsureStartedAsync(nickname);
        if (!started)
        {
            ShowInfo("本地网关启动失败（请确认已部署 gateway）", InfoBarSeverity.Error);
            return;
        }

        var (ok, result, error) = await _auth.LocalLoginAsync(nickname);
        if (!ok || result?.AccessToken is null)
        {
            ShowInfo(error ?? "离线登录失败", InfoBarSeverity.Error);
            return;
        }

        SessionState.EnterOffline(result.AccessToken, nickname);
        GoToMain();
    }

    private void GoToMain()
    {
        if (Application.Current is App app)
        {
            app.ShowMainWindow();
        }
        Close();
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        LoginInfoBar.Message = message;
        LoginInfoBar.Severity = severity;
        LoginInfoBar.IsOpen = true;
    }
}
