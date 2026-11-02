using Microsoft.UI.Dispatching;
using SteamEyaWinUI.Localization;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 在线/离线模式控制器：30 秒一次外网探测，按用户偏好（自动 / 优先在线 / 离线）自动切换；
/// 手动切换在线只有探测通过才生效；离线→在线优先复用内存中的在线 JWT，其次「记住密码」，
/// 都没有时弹登录框。远程「全员切换」指令也在这里落地。
/// </summary>
internal static class NetworkModeService
{
    public const string ModeAuto = "Auto";
    public const string ModeOnline = "Online";
    public const string ModeOffline = "Offline";

    /// <summary>后台探测周期；30 秒一次，避免频繁请求影响性能。</summary>
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(30);

    /// <summary>连续失败多少次才判定断网（防抖，避免瞬时抖动就切离线）。</summary>
    private const int FailThreshold = 2;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DispatcherQueue? _ui;
    private static DispatcherQueueTimer? _timer;
    private static Func<Task<(string Username, string Password)?>>? _loginPrompt;
    private static bool _started;
    private static int _failures;
    private static int _successes;

    /// <summary>用户取消过登录框后，本轮恢复不再重复弹；断网/手动切换/远程指令会重置。</summary>
    private static bool _promptSuppressed;

    public static DateTimeOffset? LastProbeAt { get; private set; }

    public static bool? LastProbeOk { get; private set; }

    public static bool IsSwitching { get; private set; }

    /// <summary>偏好或探测状态变化，设置页据此刷新状态文字。</summary>
    public static event Action? StateChanged;

    /// <summary>当前偏好（自动 / Online / Offline），从设置文件读取并随切换持久化。</summary>
    public static string Preference { get; private set; } = ModeAuto;

    /// <summary>主窗口构造时调用：注入 UI 派发器与「在线登录」弹框回调，并启动 30 秒探测。</summary>
    public static void Start(DispatcherQueue ui, Func<Task<(string Username, string Password)?>> loginPrompt)
    {
        _ui = ui;
        _loginPrompt = loginPrompt;
        Preference = Normalize(AppState.SettingsService.Load().NetworkMode);

        if (_started)
        {
            _timer?.Start();
            return;
        }

        _started = true;
        _timer = ui.CreateTimer();
        _timer.Interval = ProbeInterval;
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => _ = ProbeAsync(manual: false);
        _timer.Start();

        // 启动立即探一次，断网时能尽快落到离线模式。
        _ = ProbeAsync(manual: false);
    }

    public static void Stop()
    {
        _timer?.Stop();
        _started = false;
    }

    /// <summary>设置页选择运行模式：先落盘偏好，再立即执行一次切换。</summary>
    public static async Task ApplyPreferenceAsync(string mode)
    {
        mode = Normalize(mode);
        SavePreference(mode);
        _promptSuppressed = false;
        RaiseState();

        if (mode == ModeOffline)
        {
            if (!SessionState.Offline)
            {
                await SwitchToOfflineAsync(automatic: false);
            }
            return;
        }

        // 自动 / 优先在线：立即探测，能上线就上线。
        if (SessionState.Offline)
        {
            await TrySwitchToOnlineAsync(manual: true);
        }
        else
        {
            await ProbeAsync(manual: true);
        }
    }

    /// <summary>探测一次外网连通性，并按偏好执行自动切换。</summary>
    public static async Task ProbeAsync(bool manual)
    {
        if (!_started || IsSwitching)
        {
            return;
        }

        var ok = await SessionState.ProbeOnlineAsync();
        LastProbeAt = DateTimeOffset.Now;
        LastProbeOk = ok;
        if (ok)
        {
            _successes++;
            _failures = 0;
        }
        else
        {
            _failures++;
            _successes = 0;
            // 断网后重置“不再弹登录框”的抑制，恢复时允许重新提示。
            _promptSuppressed = false;
        }

        RaiseState();

        if (manual)
        {
            AppState.ShowStatus(
                Loc.T(ok ? "Mode_Probe_Ok" : "Mode_Probe_Failed"),
                ok ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success
                   : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning);
        }

        if (Preference == ModeOffline)
        {
            return;
        }

        if (ok && SessionState.Offline && _successes >= 1)
        {
            await TrySwitchToOnlineAsync(manual: false);
        }
        else if (!ok && !SessionState.Offline && _failures >= FailThreshold)
        {
            await SwitchToOfflineAsync(automatic: true);
        }
    }

    /// <summary>
    /// 切到在线：先探测，探测通过才继续；身份依次取「内存在线 JWT → 记住密码 → 弹登录框」。
    /// 自动/远程触发且无凭据时不会静默上线，而是提示用户手动登录。
    /// </summary>
    public static async Task<bool> TrySwitchToOnlineAsync(bool manual)
    {
        if (!Gate.Wait(0))
        {
            return false;
        }

        IsSwitching = true;
        RaiseState();
        try
        {
            if (!await SessionState.ProbeOnlineAsync())
            {
                LastProbeAt = DateTimeOffset.Now;
                LastProbeOk = false;
                _failures++;
                AppState.ShowStatus(Loc.T("Mode_Switch_Online_Failed"), Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning);
                return false;
            }

            LastProbeAt = DateTimeOffset.Now;
            LastProbeOk = true;

            var identity = await AcquireOnlineIdentityAsync(manual);
            if (identity is null)
            {
                return false;
            }

            // 本机网关保留运行：离线记录同步与局域网互传/保队友都依赖它。
            await GatewayLauncher.EnsureStartedAsync(identity.Value.Nickname);

            SessionState.EnterOnline(identity.Value.Token, identity.Value.Username, identity.Value.Nickname);
            await ChatHub.RestartAsync();

            _failures = 0;
            _successes = 0;
            AppState.ShowStatus(Loc.T("Mode_Switched_Online"), Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success);
            _ = SyncPendingAsync();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("切换到在线模式失败。", ex);
            AppState.ShowStatus(Loc.Tf("Mode_Switch_Online_Failed_Format", ex.Message),
                Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
            return false;
        }
        finally
        {
            IsSwitching = false;
            Gate.Release();
            RaiseState();
        }
    }

    /// <summary>切到离线：确保本机网关就绪，用当前昵称换本机 JWT，再重启聊天连接。</summary>
    public static async Task<bool> SwitchToOfflineAsync(bool automatic)
    {
        if (SessionState.Offline)
        {
            return true;
        }
        if (!Gate.Wait(0))
        {
            return false;
        }

        IsSwitching = true;
        RaiseState();
        try
        {
            var nickname = LocalNickname();
            if (!await GatewayLauncher.EnsureStartedAsync(nickname))
            {
                AppState.ShowStatus(Loc.T("Mode_Gateway_Failed"), Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
                return false;
            }

            var (ok, result, error) = await new CloudAuthService().LocalLoginAsync(nickname);
            if (!ok || result?.AccessToken is null)
            {
                AppState.ShowStatus(Loc.Tf("Mode_LocalLogin_Failed_Format", error ?? string.Empty),
                    Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
                return false;
            }

            SessionState.EnterOffline(result.AccessToken, nickname);
            await ChatHub.RestartAsync();

            AppState.ShowStatus(
                Loc.T(automatic ? "Mode_Switched_Offline_Auto" : "Mode_Switched_Offline"),
                Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("切换到离线模式失败。", ex);
            AppState.ShowStatus(Loc.Tf("Mode_Switch_Offline_Failed_Format", ex.Message),
                Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
            return false;
        }
        finally
        {
            IsSwitching = false;
            Gate.Release();
            RaiseState();
        }
    }

    /// <summary>收到其他用户下发的「全员切换」指令。</summary>
    public static async Task HandleRemoteModeAsync(string mode)
    {
        var wanted = string.Equals(mode, "online", StringComparison.OrdinalIgnoreCase)
            ? ModeOnline
            : ModeOffline;

        SavePreference(wanted);
        _promptSuppressed = false;
        RaiseState();

        if (wanted == ModeOffline)
        {
            await SwitchToOfflineAsync(automatic: true);
        }
        else
        {
            await TrySwitchToOnlineAsync(manual: false);
        }
    }

    /// <summary>把离线期间产生的消息同步到远端；仅在线且本机网关可用时执行。</summary>
    public static async Task SyncPendingAsync()
    {
        // 离线模式没有远端可推，静默跳过（避免启动时打无意义的“未登录”警告）。
        if (SessionState.Offline || string.IsNullOrWhiteSpace(SessionState.Token))
        {
            return;
        }

        try
        {
            await GatewayLauncher.EnsureStartedAsync(SessionState.Nickname);
            var (ok, pushed, msg) = await new SyncService().TrySyncAsync();
            if (ok && pushed > 0)
            {
                AppState.ShowStatus(Loc.Tf("Mode_Sync_Pushed_Format", pushed),
                    Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success);
            }
            else if (!ok && !string.IsNullOrEmpty(msg))
            {
                AppLog.Warn($"离线记录同步失败：{msg}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("离线记录同步失败。", ex);
        }
    }

    /// <summary>取在线身份：内存 JWT → 记住密码 → 登录框（自动触发且被取消过则跳过）。</summary>
    private static async Task<(string Token, string Username, string Nickname)?> AcquireOnlineIdentityAsync(bool manual)
    {
        if (!SessionState.Offline && SessionState.Token is { Length: > 0 } current)
        {
            return (current, SessionState.Username, SessionState.Nickname);
        }

        var cached = SessionState.CachedOnlineSession;
        if (cached is { } c && await SessionState.ValidateOnlineTokenAsync(c.Token))
        {
            return c;
        }

        var remembered = new RememberCredentialStore().Load();
        if (remembered is { } saved)
        {
            var (ok, result, _) = await new CloudAuthService().LoginAsync(saved.Username, saved.Password);
            if (ok && result?.AccessToken is not null)
            {
                return (result.AccessToken, result.User?.Username ?? saved.Username,
                    result.User?.Nickname ?? saved.Username);
            }
        }

        if (!manual && _promptSuppressed)
        {
            return null;
        }

        if (_loginPrompt is null)
        {
            return null;
        }

        var input = await _loginPrompt();
        if (input is null)
        {
            _promptSuppressed = true;
            if (manual)
            {
                AppState.ShowStatus(Loc.T("Mode_Login_Cancelled"), Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational);
            }
            return null;
        }

        var login = await new CloudAuthService().LoginAsync(input.Value.Username, input.Value.Password);
        if (login.ok && login.result?.AccessToken is not null)
        {
            return (login.result.AccessToken, login.result.User?.Username ?? input.Value.Username,
                login.result.User?.Nickname ?? input.Value.Username);
        }

        _promptSuppressed = true;
        AppState.ShowStatus(Loc.Tf("Mode_Login_Failed_Format", login.error ?? string.Empty),
            Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
        return null;
    }

    private static string LocalNickname()
    {
        var nickname = SessionState.Nickname.Trim();
        if (nickname.Length < 2)
        {
            nickname = SessionState.Username.Trim();
        }
        return nickname.Length < 2 ? "局域网用户" : nickname;
    }

    private static void SavePreference(string mode)
    {
        Preference = Normalize(mode);
        var settings = AppState.SettingsService.Load();
        if (settings.NetworkMode == Preference)
        {
            return;
        }
        settings.NetworkMode = Preference;
        AppState.SettingsService.Save(settings);
    }

    private static string Normalize(string? mode) => mode switch
    {
        ModeOnline => ModeOnline,
        ModeOffline => ModeOffline,
        _ => ModeAuto
    };

    private static void RaiseState() => _ui?.TryEnqueue(() => StateChanged?.Invoke());
}
