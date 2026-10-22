using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;
using SteamEyaWinUI.Services;
using Windows.System;

namespace SteamEyaWinUI.Pages;

public sealed partial class LoginPage : Page, INotifyPropertyChanged
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue =
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

    private bool _suppressModeStatus;

    public LoginPage()
    {
        InitializeComponent();

        AppState.LoginPage = this;
        AppState.BusyChanged += OnBusyChanged;
        Loc.LanguageChanged += OnLanguageChanged;

        // SelectorBarItem.IsSelected 在 XAML 解析期不可靠，显式设定初始模式（默认「远程账号」）。
        _suppressModeStatus = true;
        ModeSelector.SelectedItem = RemoteModeItem;
        _suppressModeStatus = false;
        ApplyModeVisibility();

        UpdateAccountInfoFromCurrentInputs();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>XAML 绑定入口：{x:Bind Strings.Get('Key'), Mode=OneWay}。</summary>
    internal LocalizedStrings Strings => Loc.Strings;

    private bool IsRemoteMode => ModeSelector.SelectedItem == RemoteModeItem;

    private bool IsCredsMode => ModeSelector.SelectedItem == CredsModeItem;

    private void OnLanguageChanged()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            // 静态 x:Bind 文本随 Strings 重算；右侧账号信息面板的命令式文本（用户名/SteamID/过期/可用状态）
            // 重跑下面两个方法即可让已显示的“未填写/未解析/未验证”等占位换语言。
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Strings)));
            UpdateRemotePanelState();
            UpdateAccountInfoFromCurrentInputs();
            OnBusyChanged(AppState.IsBusy);
        });
    }

    private static void ShowStatus(string message, InfoBarSeverity severity)
    {
        AppState.ShowStatus(message, severity);
    }

    private void OnBusyChanged(bool isBusy)
    {
        var enabled = !isBusy;
        ModeSelector.IsEnabled = enabled;
        AccountNameBox.IsEnabled = enabled;
        EyaTokenBox.IsEnabled = enabled;
        RemoteUsernameBox.IsEnabled = enabled;
        RemotePasswordBox.IsEnabled = enabled;
        RemoteLoginButton.IsEnabled = enabled;
        RemoteRegisterButton.IsEnabled = enabled;
        RemoteGetAccountButton.IsEnabled = enabled;
        RemoteLogoutButton.IsEnabled = enabled;
        ClearWorkshopButton.IsEnabled = enabled;
        LoginButton.IsEnabled = enabled;

        // 取消按钮反其道而行：仅忙碌时出现且保持可用，让用户中断长任务。
        CancelButton.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        ShowStatus(Loc.T("Login_Status_Cancelling"), InfoBarSeverity.Informational);
        AppState.CancelBusyOperation();
    }

    // 账密模式（调试）：用账号密码经 Web API 换取 EYA 令牌，直接填进手动模式（不落本地记录）。
    private async void CredFetchButton_Click(object sender, RoutedEventArgs e)
    {
        var accountName = CredAccountNameBox.Text.Trim();
        var password = CredPasswordBox.Password;

        if (string.IsNullOrWhiteSpace(accountName))
        {
            ShowStatus(Loc.T("Creds_Error_AccountRequired"), InfoBarSeverity.Warning);
            return;
        }

        if (string.IsNullOrEmpty(password))
        {
            ShowStatus(Loc.T("Creds_Error_PasswordRequired"), InfoBarSeverity.Warning);
            return;
        }

        var cancellationToken = AppState.BeginBusyOperation();
        var progress = new Progress<string>(message =>
            ShowStatus(message, InfoBarSeverity.Informational));

        try
        {
            var result = await AppState.CredentialsAuthService.GetRefreshTokenAsync(
                accountName, password, PromptGuardCodeAsync, progress, cancellationToken);

            var token = FormatHelper.NormalizeToken(result.RefreshToken);
            // 填进手动模式并切过去，方便接着上号/查询；清空密码不留存。
            _suppressModeStatus = true;
            ModeSelector.SelectedItem = ManualModeItem;
            _suppressModeStatus = false;
            AccountNameBox.Text = result.AccountName;
            EyaTokenBox.Text = token;
            CredPasswordBox.Password = string.Empty;
            UpdateAccountInfo(result.AccountName, token);

            ShowStatus(Loc.Tf("Creds_Status_Saved_Format", result.AccountName), InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            ShowStatus(Loc.T("Creds_Status_Cancelled"), InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            AppLog.Error("账密获取 EYA 令牌失败。", ex);
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            AppState.EndBusyOperation();
        }
    }

    // 令牌验证器需要输码时，弹窗向用户索取邮箱/手机验证码；取消返回 null。
    private async Task<string?> PromptGuardCodeAsync(SteamGuardPrompt prompt, CancellationToken cancellationToken)
    {
        var isMobile = prompt.Type == SteamGuardType.DeviceCode;

        var codeBox = new TextBox
        {
            PlaceholderText = Loc.T("Creds_Guard_CodePlaceholder"),
            IsSpellCheckEnabled = false,
            MaxLength = 10,
            Margin = new Thickness(0, 12, 0, 0)
        };

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(prompt.AssociatedMessage)
                ? Loc.T(isMobile ? "Creds_Guard_MobileMessage" : "Creds_Guard_EmailMessage")
                : prompt.AssociatedMessage,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(codeBox);

        var dialog = new ContentDialog
        {
            Title = Loc.T(isMobile ? "Creds_Guard_MobileTitle" : "Creds_Guard_EmailTitle"),
            Content = panel,
            PrimaryButtonText = Loc.T("Common_Confirm"),
            CloseButtonText = Loc.T("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? codeBox.Text.Trim() : null;
    }

    private async void ClearWorkshopButton_Click(object sender, RoutedEventArgs e)
    {
        var cancellationToken = AppState.BeginBusyOperation();
        ShowStatus(Loc.T("Login_Status_ClearingWorkshop"), InfoBarSeverity.Informational);

        var progress = new Progress<string>(message =>
            ShowStatus(message, InfoBarSeverity.Informational));

        try
        {
            var (accountName, eyaToken) = await GetCredentialsAsync(cancellationToken);
            EnsureTokenValidForAction(eyaToken, "Login_Action_ClearWorkshop");
            UpdateAccountInfo(accountName, eyaToken);
            await ApplyAccountProfileAsync(eyaToken, accountName);

            // 不再前置 EnsureTokenAcceptedBySteamAsync：ClearSubscriptionsAsync 内部已完整握手，
            // 令牌被拒会在下方按 SteamCmException.IsTokenFailure 处理，避免无谓的双重握手。
            var count = await AppState.WorkshopService.ClearSubscriptionsAsync(
                eyaToken,
                progress,
                cancellationToken);

            ShowStatus(
                Loc.Tf("Login_Status_WorkshopCleared_Format", count),
                InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            ShowStatus(Loc.T("Login_Status_ClearWorkshopCancelled"), InfoBarSeverity.Informational);
        }
        catch (SteamCmException ex) when (ex.IsTokenFailure)
        {
            // 令牌被 Steam 拒绝：与前置验证失败时一致地标红可用状态，再报错。
            AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_Invalid");
            AccountInfoAvailabilityText.Foreground = FormatHelper.GetStatusBrush(InfoBarSeverity.Error);
            ShowStatus($"{ex.Message}{Loc.T("Login_Error_CannotClearWorkshopSuffix")}", InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            AppState.EndBusyOperation();
        }
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        // 首次会自动检测并持久化 Steam 路径；失效则重新检测；都不行才弹框让用户选。用户取消即中止上号。
        if (!await SteamPathCoordinator.EnsureResolvedAsync())
        {
            ShowStatus(Loc.T("SteamPath_Status_Required"), InfoBarSeverity.Warning);
            return;
        }

        var cancellationToken = AppState.BeginBusyOperation();

        try
        {
            var (accountName, eyaToken) = await GetCredentialsAsync(cancellationToken);
            await LoginAndRecordAsync(accountName, eyaToken, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            ShowStatus(Loc.T("Login_Status_LoginCancelled"), InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            AppLog.Error("登录失败。", ex);
            ShowStatus(Loc.Tf("Login_Error_LoginFailed_Format", ex.Message, AppLog.LogFilePath), InfoBarSeverity.Error);
        }
        finally
        {
            AppState.EndBusyOperation();
        }
    }

    /// <summary>
    /// 用给定账号凭据执行上号：结构校验 → 抓资料填面板 → 在线验证（网络故障跳过）→ 写配置并启动 Steam。
    /// 「登录到 Steam」与「远程账号一键上号」共用，调用方负责路径解析、忙碌状态与异常呈现。
    /// </summary>
    private async Task LoginAndRecordAsync(string accountName, string eyaToken, CancellationToken cancellationToken)
    {
        ShowStatus(Loc.T("Login_Status_Processing"), InfoBarSeverity.Informational);

        var progress = new Progress<string>(message =>
            ShowStatus(message, InfoBarSeverity.Informational));

        EnsureTokenValidForAction(eyaToken, "Login_Action_Login");
        UpdateAccountInfo(accountName, eyaToken);
        // 只读抓一次昵称/头像填右侧面板：历史功能已移除，不落任何账号记录。
        await ApplyAccountProfileAsync(eyaToken, accountName);
        await EnsureTokenAcceptedBySteamAsync(eyaToken, "Login_Action_Login", cancellationToken);
        var result = await Task.Run(
            () => AppState.LoginService.Login(accountName, eyaToken, progress),
            cancellationToken);
        ShowStatus(
            Loc.Tf("Login_Status_LoginStarted_Format", result.SteamId, FormatHelper.FormatRemaining(result.Remaining)),
            InfoBarSeverity.Success);
    }

    private void ModeSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (RemotePanel is null || ManualPanel is null || CredsPanel is null || ActionButtonGrid is null)
        {
            return;
        }

        ApplyModeVisibility();
        UpdateAccountInfoFromCurrentInputs();

        if (!_suppressModeStatus)
        {
            var statusKey = IsRemoteMode
                ? "Login_Status_RemoteModeEnabled"
                : IsCredsMode ? "Login_Status_CredsModeEnabled" : "Login_Status_ManualModeEnabled";
            ShowStatus(Loc.T(statusKey), InfoBarSeverity.Informational);
        }
    }

    private void ApplyModeVisibility()
    {
        var remote = IsRemoteMode;
        var creds = IsCredsMode;

        RemotePanel.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
        ManualPanel.Visibility = (!remote && !creds) ? Visibility.Visible : Visibility.Collapsed;
        CredsPanel.Visibility = creds ? Visibility.Visible : Visibility.Collapsed;

        // 远程账号模式 / 账密模式：下方上号、查询等动作按钮无意义，隐藏。
        ActionButtonGrid.Visibility = (creds || remote) ? Visibility.Collapsed : Visibility.Visible;

        if (remote)
        {
            UpdateRemotePanelState();
        }
    }

    /// <summary>按是否已登录统一账号，切换「远程账号」面板的登录表单 / 取号按钮。</summary>
    private void UpdateRemotePanelState()
    {
        if (RemoteAuthPanel is null || RemoteGetAccountPanel is null)
        {
            return;
        }

        var service = AppState.RemoteAccountService;
        if (service.IsLoggedIn)
        {
            RemoteAuthPanel.Visibility = Visibility.Collapsed;
            RemoteGetAccountPanel.Visibility = Visibility.Visible;
            RemoteWelcomeText.Text = Loc.Tf("Login_Remote_Welcome_Format", SessionState.Nickname);
        }
        else
        {
            RemoteAuthPanel.Visibility = Visibility.Visible;
            RemoteGetAccountPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void ManualCredentialBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateAccountInfoFromCurrentInputs();
    }

    private void AccountNameBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            EyaTokenBox.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
    }

    private Task<(string AccountName, string EyaToken)> GetCredentialsAsync(CancellationToken cancellationToken = default)
    {
        var accountName = AccountNameBox.Text.Trim();
        var eyaToken = FormatHelper.NormalizeToken(EyaTokenBox.Text.Trim());

        if (string.IsNullOrWhiteSpace(accountName))
        {
            throw new InvalidOperationException(Loc.T("Login_Error_AccountNameRequired"));
        }

        if (string.IsNullOrWhiteSpace(eyaToken))
        {
            throw new InvalidOperationException(Loc.T("Login_Error_EyaTokenRequired"));
        }

        return Task.FromResult((accountName, eyaToken));
    }

    /// <summary>右侧账号信息面板当前展示的 SteamID（由 UpdateAccountInfo 解析令牌时维护，仅 UI 线程访问）。</summary>
    private string? _accountInfoPanelSteamId;

    /// <summary>
    /// 异步抓取并填充右侧面板的昵称/头像。只读、失败静默——资料属于装饰信息，绝不能阻断上号。
    /// </summary>
    private async Task ApplyAccountProfileAsync(string eyaToken, string? accountName = null)
    {
        var tokenInfo = AppState.JwtTokenService.Inspect(eyaToken);
        if (string.IsNullOrWhiteSpace(tokenInfo.SteamId))
        {
            return;
        }

        var steamId = tokenInfo.SteamId!;
        var profile = await SteamProfileReader.ReadAsync(steamId);
        if (profile is null)
        {
            return;
        }

        // 面板可能已被切到别的账号：只更新仍展示该账号时的内容。
        if (!string.Equals(_accountInfoPanelSteamId, steamId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ApplyAccountInfoProfile(profile, accountName);
    }

    private void ApplyAccountInfoProfile(SteamProfileInfo? profile, string? accountName = null)
    {
        if (profile is null)
        {
            AccountInfoAvatar.ProfilePicture = null;
            // 留空而非占位文案：PersonPicture 会据 DisplayName 取首字母（英文 "Not set"→"NS"），
            // 空串才回退到默认人像剪影——中文 "未填写" 因 CJK 不生成首字母恰好显示剪影，这里统一成空保持一致。
            AccountInfoAvatar.DisplayName = string.Empty;
            AccountInfoPersonaText.Text = Loc.T("Login_Value_PersonaNotSynced");
            return;
        }

        var persona = string.IsNullOrWhiteSpace(profile.PersonaName) ? accountName : profile.PersonaName;
        AccountInfoAvatar.DisplayName = persona ?? string.Empty;
        AccountInfoAvatar.ProfilePicture = profile.AvatarImage;
        AccountInfoPersonaText.Text = string.IsNullOrWhiteSpace(persona)
            ? Loc.T("Login_Value_PersonaNotSynced")
            : persona;
    }

    // actionName 是稳定的本地化键（Login_Action_*），不是显示文案：既用于和具体动作比较，
    // 也用 Loc.T(actionName) 取当前语言的动作名拼进报错，避免用界面文本做控制流（多语言下会失配）。
    private void EnsureTokenValidForAction(string eyaToken, string actionName)
    {
        var info = AppState.JwtTokenService.Inspect(eyaToken);
        if (info.IsValid)
        {
            return;
        }

        // 用结构化的过期时间判定「已过期」，而非比较本地化后的状态文案。
        var isExpired = info.ExpiresAt is { } expiry && expiry <= DateTimeOffset.Now;
        if (isExpired && actionName == "Login_Action_ClearWorkshop")
        {
            throw new InvalidOperationException(Loc.T("Login_Error_TokenExpiredCannotClearWorkshop"));
        }

        throw new InvalidOperationException(
            Loc.Tf("Login_Error_StatusCannotAction_Format", info.Status, Loc.T(actionName)));
    }

    private void UpdateAccountInfoFromCurrentInputs()
    {
        if (AccountInfoUserText is null)
        {
            return;
        }

        if (IsCredsMode || IsRemoteMode)
        {
            // 账密模式还没有令牌；远程账号模式取号成功后会显式刷新面板，这里先清空。
            UpdateAccountInfo(null, null);
            return;
        }

        UpdateAccountInfo(
            AccountNameBox.Text.Trim(),
            FormatHelper.NormalizeToken(EyaTokenBox.Text.Trim()));
    }

    /// <summary>
    /// 可用状态恢复默认前景色：未验证/验证中等占位与「未填写/未解析」等字段保持同色，
    /// 仅验证结果（有效/无效）才用成功/失败色。
    /// </summary>
    private void ResetAvailabilityForeground() =>
        AccountInfoAvailabilityText.ClearValue(TextBlock.ForegroundProperty);

    private void UpdateAccountInfo(string? userName, string? token)
    {
        AccountInfoUserText.Text = string.IsNullOrWhiteSpace(userName) ? Loc.T("Login_Value_NotFilled") : userName;
        ApplyAccountInfoProfile(null);
        // 用户名为空时给空串（显示默认人像剪影），不给占位文案，避免 PersonPicture 取首字母得到 "NS" 之类。
        AccountInfoAvatar.DisplayName = string.IsNullOrWhiteSpace(userName) ? string.Empty : userName;

        if (string.IsNullOrWhiteSpace(token))
        {
            _accountInfoPanelSteamId = null;
            AccountInfoSteamIdText.Text = Loc.T("Login_Value_NotResolved");
            AccountInfoExpiresText.Text = Loc.T("Login_Value_NotResolved");
            AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_NotVerified");
            ResetAvailabilityForeground();
            return;
        }

        var info = AppState.JwtTokenService.Inspect(token);
        _accountInfoPanelSteamId = string.IsNullOrWhiteSpace(info.SteamId) ? null : info.SteamId;
        AccountInfoSteamIdText.Text = string.IsNullOrWhiteSpace(info.SteamId) ? Loc.T("Login_Value_NotResolved") : info.SteamId;
        AccountInfoExpiresText.Text = info.ExpiresAt.HasValue
            ? info.ExpiresAt.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")
            : Loc.T("Login_Value_NotResolved");
        AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_NotVerified");
        ResetAvailabilityForeground();
    }

    private async Task EnsureTokenAcceptedBySteamAsync(
        string eyaToken,
        string actionName,
        CancellationToken cancellationToken = default)
    {
        AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_Verifying");
        ResetAvailabilityForeground();

        SteamTokenOnlineValidationResult online;
        try
        {
            // ValidateAsync 只把「令牌被 Steam 拒绝」转成 IsValid=false，网络/CM 不可达（含
            // TimeoutException）会向上抛——上号本身只写 VDF+启动 Steam，不依赖本机连得上 CM，
            // 故网络故障不应阻断上号，仅给中性提示后继续。
            online = await AppState.TokenOnlineValidationService.ValidateAsync(eyaToken, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"在线验证令牌时无法连接 Steam，已跳过在线验证继续{Loc.T(actionName)}：{ex.Message}");
            AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_NotVerifiedOffline");
            ResetAvailabilityForeground();
            ShowStatus(Loc.Tf("Login_Status_SkippedOnlineValidation_Format", Loc.T(actionName)), InfoBarSeverity.Warning);
            return;
        }

        AccountInfoAvailabilityText.Text = online.IsValid ? Loc.T("Login_Availability_Valid") : Loc.T("Login_Availability_Invalid");
        AccountInfoAvailabilityText.Foreground = FormatHelper.GetStatusBrush(
            online.IsValid ? InfoBarSeverity.Success : InfoBarSeverity.Error);

        if (online.IsValid)
        {
            return;
        }

        // 走到这里是令牌确实被 Steam 拒绝（IsTokenFailure 路径），维持红色并阻断后续动作。
        throw new InvalidOperationException(
            Loc.Tf("Login_Error_StatusCannotAction_Format", online.Status, Loc.T(actionName)));
    }

    // ========== 远程账号模式事件处理 ==========

    private async void RemoteLoginButton_Click(object sender, RoutedEventArgs e)
    {
        var username = RemoteUsernameBox.Text.Trim();
        var password = RemotePasswordBox.Password;

        if (string.IsNullOrWhiteSpace(username))
        {
            ShowStatus(Loc.T("Login_Remote_Error_UsernameRequired"), InfoBarSeverity.Warning);
            return;
        }

        if (string.IsNullOrEmpty(password))
        {
            ShowStatus(Loc.T("Login_Remote_Error_PasswordRequired"), InfoBarSeverity.Warning);
            return;
        }

        var cancellationToken = AppState.BeginBusyOperation();
        try
        {
            var result = await AppState.RemoteAccountService.LoginAsync(username, password, cancellationToken);
            if (result.IsSuccess)
            {
                RemotePasswordBox.Password = string.Empty;
                ShowStatus(Loc.T("Login_Remote_Status_LoginSuccess"), InfoBarSeverity.Success);
                UpdateRemotePanelState();
            }
            else
            {
                ShowStatus(Loc.Tf("Login_Remote_Error_LoginFailed_Format", result.Message), InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Tf("Login_Remote_Error_LoginFailed_Format", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            AppState.EndBusyOperation();
        }
    }

    private async void RemoteRegisterButton_Click(object sender, RoutedEventArgs e)
    {
        var username = RemoteUsernameBox.Text.Trim();
        var password = RemotePasswordBox.Password;

        if (string.IsNullOrWhiteSpace(username))
        {
            ShowStatus(Loc.T("Login_Remote_Error_UsernameRequired"), InfoBarSeverity.Warning);
            return;
        }

        if (string.IsNullOrEmpty(password))
        {
            ShowStatus(Loc.T("Login_Remote_Error_PasswordRequired"), InfoBarSeverity.Warning);
            return;
        }

        var cancellationToken = AppState.BeginBusyOperation();
        try
        {
            var result = await AppState.RemoteAccountService.RegisterAsync(
                username, password, RemoteInviteBox.Text, cancellationToken);
            if (result.IsSuccess)
            {
                RemotePasswordBox.Password = string.Empty;
                ShowStatus(Loc.T("Login_Remote_Status_RegisterSuccess"), InfoBarSeverity.Success);
                UpdateRemotePanelState();
            }
            else
            {
                ShowStatus(Loc.Tf("Login_Remote_Error_RegisterFailed_Format", result.Message), InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Tf("Login_Remote_Error_RegisterFailed_Format", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            AppState.EndBusyOperation();
        }
    }

    /// <summary>从服务器获取账号并直接一键上号。</summary>
    private async void RemoteGetAccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await SteamPathCoordinator.EnsureResolvedAsync())
        {
            ShowStatus(Loc.T("SteamPath_Status_Required"), InfoBarSeverity.Warning);
            return;
        }

        var cancellationToken = AppState.BeginBusyOperation();
        ShowStatus(Loc.T("Login_Remote_Status_GettingAccount"), InfoBarSeverity.Informational);

        try
        {
            var result = await AppState.RemoteAccountService.GetAccountAsync(cancellationToken);
            if (!result.IsSuccess || result.Account is null)
            {
                ShowStatus(Loc.Tf("Login_Remote_Error_GetFailed_Format", result.Message), InfoBarSeverity.Error);
                return;
            }

            var accountName = result.Account.AccountName;
            var eyaToken = FormatHelper.NormalizeToken(result.Account.EyaToken);
            ShowStatus(Loc.Tf("Login_Remote_Status_GotAccount_Format", accountName), InfoBarSeverity.Informational);

            await LoginAndRecordAsync(accountName, eyaToken, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            ShowStatus(Loc.T("Login_Status_LoginCancelled"), InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            AppLog.Error("远程账号一键上号失败。", ex);
            ShowStatus(Loc.Tf("Login_Error_LoginFailed_Format", ex.Message, AppLog.LogFilePath), InfoBarSeverity.Error);
        }
        finally
        {
            AppState.EndBusyOperation();
        }
    }

    private void RemoteLogoutButton_Click(object sender, RoutedEventArgs e)
    {
        AppState.RemoteAccountService.Logout();
        UpdateAccountInfo(null, null);
        ShowStatus(Loc.T("Login_Remote_Status_LoggedOut"), InfoBarSeverity.Informational);
        UpdateRemotePanelState();
    }
}