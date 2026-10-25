using Microsoft.UI.Xaml;
using Windows.UI;
using Microsoft.UI.Xaml.Media;
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
        Title = "茶π·次元口袋";
        UpdateTabVisuals();

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

    /// <summary>选中项实色圆角、未选中透明，做成分段控件的样子。</summary>
    private void UpdateTabVisuals()
    {
        ApplyTabStyle(TabLoginButton, !_isRegister);
        ApplyTabStyle(TabRegisterButton, _isRegister);
    }

    private static void ApplyTabStyle(Button button, bool selected)
    {
        if (selected)
        {
            button.Background = (Brush)Application.Current.Resources["AuroraAccentGradientBrush"];
            button.Foreground = new SolidColorBrush(Colors.White);
            button.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            return;
        }

        button.Background = new SolidColorBrush(Colors.Transparent);
        button.Foreground = (Brush)Application.Current.Resources["AuroraTextMutedBrush"];
        button.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
    }

    /// <summary>当前是否处于「注册」页签。用按钮替代原来的 RadioButton，外观才能做成胶囊分段。</summary>
    private bool _isRegister;

    private async void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (NickBox is null)
        {
            return;
        }

        var register = ReferenceEquals(sender, TabRegisterButton);
        _isRegister = register;
        UpdateTabVisuals();

        NickBox.Visibility = register ? Visibility.Visible : Visibility.Collapsed;
        RememberBox.Visibility = register ? Visibility.Collapsed : Visibility.Visible;
        AuthButton.Content = register ? "注 册" : "登 录";

        // 邀请码框与网页版注册保持一致：由后台「注册必须填写邀请码」开关决定是否显示。
        InviteBox.Visibility = Visibility.Collapsed;
        if (register)
        {
            await RefreshInviteAsync();
        }
    }

    /// <summary>按服务端开关决定是否显示邀请码输入框（读的是聊天室后台的同一个设置）。</summary>
    private async Task RefreshInviteAsync()
    {
        var info = await _auth.GetInviteInfoAsync();
        InviteBox.Visibility = info.Required ? Visibility.Visible : Visibility.Collapsed;
        if (info.Required && info.Available == 0)
        {
            ShowInfo("当前注册需要邀请码，但暂无可用邀请码，请联系管理员", InfoBarSeverity.Warning);
        }
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
            var register = _isRegister;
            var (ok, result, error) = register
                ? await _auth.RegisterAsync(username, password, NickBox.Text.Trim(), InviteBox.Text)
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
