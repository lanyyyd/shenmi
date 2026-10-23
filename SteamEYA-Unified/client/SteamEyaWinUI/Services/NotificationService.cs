using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using Microsoft.Windows.AppNotifications;
using SteamEyaWinUI.Localization;
using Microsoft.Windows.AppNotifications.Builder;

namespace SteamEyaWinUI.Services;

/// <summary>通知发送方式。</summary>
internal enum NotificationChannel
{
    /// <summary>窗口不在前台时系统通知 + 应用内浮窗双保险；在前台时只弹轻量浮窗。</summary>
    Auto,

    /// <summary>只弹应用内浮窗（不依赖系统通知，任何 Windows 版本都可见）。</summary>
    InApp,

    /// <summary>只发系统通知（进通知中心，受系统「勿扰/通知开关」影响）。</summary>
    System,

    /// <summary>完全关闭。</summary>
    Off
}

/// <summary>
/// 统一通知出口。
///
/// 双通道设计的起因：Windows App SDK 的 <see cref="AppNotificationManager"/> 对「未打包（unpackaged）」
/// 应用虽然会自动注册 COM 激活与 AUMID，但在 Win10 上有若干**静默失败**场景（以管理员身份运行、
/// 系统通知总开关被关、通知设置里没有该应用条目），这些情况既不会抛异常，也不会出现在日志里，
/// 表现就是「注册成功但什么也不弹」。
///
/// 所以这里保留系统通道（Win11 体验更好、能进通知中心），同时自绘一个置顶浮窗作为**兜底可见通道**：
/// 只要程序在跑，浮窗一定出现在工作区右下角。两者是否叠加由 <see cref="NotificationChannel"/> 决定。
///
/// 诊断：<see cref="Describe"/> 会输出操作系统版本、是否提权、系统通知总开关、AUMID 注册表项、
/// 通道注册结果与最近一次发送走的是哪条通道，设置页可直接展示给用户回传。
/// </summary>
internal static partial class NotificationService
{
    /// <summary>
    /// 固定的 AppUserModelID。必须是稳定值：系统通知的设置项、通知中心条目都按它归档，
    /// 一旦变化，用户之前在系统设置里对该应用的开关会失效。
    /// </summary>
    internal const string AppUserModelId = "SteamEYA.Desktop";

    private const int TokenElevationClass = 20;

    private static readonly object Gate = new();

    private static DispatcherQueue? _ui;
    private static bool _initialized;
    private static bool _handlersWired;

    // ---- 环境诊断（Initialize 时采样一次） ----
    private static bool _elevated;
    private static int _osBuild;
    private static string _osText = "未知";
    private static bool _systemRegistered;
    private static string? _systemRegisterError;
    private static long _lastRegisterAttemptTicks;   // 失败后 60 秒内不重复尝试，避免每条通知都卡一下
    private static bool _aumidRegistered;
    private static string? _aumidError;

    // ---- 运行期状态 ----
    private static NotificationChannel _channel = NotificationChannel.Auto;
    private static string _lastChannelUsed = "无";

    /// <summary>点击通知（系统或浮窗）后的回调，参数为通知携带的 argument。始终在 UI 线程触发。</summary>
    public static event Action<string>? Invoked;

    /// <summary>注入 UI 派发器；应用内浮窗必须在该线程创建。</summary>
    public static void SetUiDispatcher(DispatcherQueue ui)
    {
        _ui = ui;
    }

    /// <summary>
    /// 进程级 AUMID 必须在创建任何窗口之前设置，否则系统会按 exe 路径生成一个默认值，
    /// 导致通知设置里的应用名可能显示成路径。因此在 <c>Program.Main</c> 最早调用。
    /// </summary>
    internal static void SetProcessAppUserModelId()
    {
        try
        {
            var hr = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            AppLog.Debug($"SetCurrentProcessExplicitAppUserModelID -> 0x{hr:X8}");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"设置 AppUserModelID 失败：{ex.Message}");
        }
    }

    /// <summary>注册通知通道（幂等）。启动时调用一次，之后发送失败会惰性重试。</summary>
    internal static void Initialize()
    {
        lock (Gate)
        {
            if (!_initialized)
            {
                _initialized = true;
                CaptureEnvironment();
                EnsureAumidRegistry();
                ReloadChannel();
            }

            if (_systemRegistered || _elevated)
            {
                return;
            }

            // 注册失败时不要每条通知都重试：Register 内部会走 COM 注册，失败一次就要几百毫秒。
            var sinceLast = Environment.TickCount64 - Interlocked.Read(ref _lastRegisterAttemptTicks);
            if (_lastRegisterAttemptTicks != 0 && sinceLast < 60_000)
            {
                return;
            }

            Interlocked.Exchange(ref _lastRegisterAttemptTicks, Environment.TickCount64);
            RegisterSystemChannel();
        }
    }

    /// <summary>设置页改完通知方式/调试开关后调用，让新配置立即生效。</summary>
    internal static void ReloadChannel()
    {
        try
        {
            _channel = ParseChannel(AppState.SettingsService.Load().NotificationMode);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取通知设置失败，回退为自动：{ex.Message}");
            _channel = NotificationChannel.Auto;
        }

        AppLog.Debug($"通知方式 = {_channel}");
    }

    private static NotificationChannel ParseChannel(string? value) => value switch
    {
        "inapp" => NotificationChannel.InApp,
        "system" => NotificationChannel.System,
        "off" => NotificationChannel.Off,
        _ => NotificationChannel.Auto
    };

    // ---------------- 环境采样 ----------------

    private static void CaptureEnvironment()
    {
        _osBuild = Environment.OSVersion.Version.Build;
        _osText = $"{Environment.OSVersion.Version}（build {_osBuild}，{(IsWindows11 ? "Windows 11" : "Windows 10 及更早")}）";
        _elevated = DetectElevation();

        AppLog.Info($"通知环境：OS={_osText}；提权={_elevated}；" +
                    $"系统通知总开关={(IsToastEnabledByPolicy() ? "开" : "关")}；" +
                    $"进程 AUMID={AppUserModelId}");
    }

    private static bool IsWindows11 => _osBuild >= 22000;

    /// <summary>文档明确：应用通知不支持提权（管理员）进程，且 Show 会静默失败而不抛异常。</summary>
    private static bool DetectElevation()
    {
        nint token = 0;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), 0x0008 /* TOKEN_QUERY */, out token))
            {
                return false;
            }

            if (!GetTokenInformation(token, TokenElevationClass, out var info, Unsafe.SizeOf<TokenElevation>(), out _))
            {
                return false;
            }

            return info.Elevated != 0;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"检测提权状态失败：{ex.Message}");
            return false;
        }
        finally
        {
            if (token != 0)
            {
                CloseHandle(token);
            }
        }
    }

    /// <summary>系统级「获取来自应用和其他发送者的通知」总开关被关时，任何 Show 都不会显示。</summary>
    private static bool IsToastEnabledByPolicy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\PushNotifications");
            var value = key?.GetValue("ToastEnabled");
            return value is null || Convert.ToInt32(value) != 0;
        }
        catch
        {
            return true;    // 读不到就当作开启，交给后续实际发送去验证
        }
    }

    /// <summary>
    /// Win10 在通知设置里按 AUMID 归档应用条目，缺少 DisplayName/IconUri 时可能不显示通知。
    /// 这里主动补一份（WinAppSDK 的 Register 不保证写这两个值）。
    /// </summary>
    private static void EnsureAumidRegistry()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                _aumidError = "无法取得 exe 路径";
                return;
            }

            using var key = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\AppUserModelId\{AppUserModelId}");
            if (key is null)
            {
                _aumidError = "创建注册表项失败";
                return;
            }

            key.SetValue("DisplayName", "SteamEYA");
            key.SetValue("IconUri", exe);
            _aumidRegistered = true;
            AppLog.Debug($"AUMID 注册表已写入：{AppUserModelId} -> {exe}");
        }
        catch (Exception ex)
        {
            _aumidError = ex.Message;
            AppLog.Warn($"写入 AUMID 注册表失败：{ex.Message}");
        }
    }

    // ---------------- 系统通道 ----------------

    private static void RegisterSystemChannel()
    {
        if (_elevated)
        {
            _systemRegisterError = "以管理员身份运行时不支持应用通知";
            AppLog.Info("跳过系统通知注册：当前进程已提权。");
            return;
        }

        if (_osBuild is > 0 and < 17763)
        {
            _systemRegisterError = $"系统版本过低（build {_osBuild} < 17763）";
            AppLog.Warn(_systemRegisterError);
            return;
        }

        try
        {
            Wire();
            AppNotificationManager.Default.Register();
            _systemRegistered = true;
            _systemRegisterError = null;
            AppLog.Info("系统通知通道已注册（AppNotificationManager）。");
        }
        catch (Exception ex)
        {
            _systemRegistered = false;
            _systemRegisterError = $"0x{ex.HResult:X8} {ex.Message}";
            AppLog.Error($"系统通知注册失败（0x{ex.HResult:X8}）：{ex.Message}", ex);
        }
    }

    private static void Wire()
    {
        if (_handlersWired)
        {
            return;
        }

        _handlersWired = true;
        try
        {
            // 官方要求：先订阅点击回调，再 Register。
            AppNotificationManager.Default.NotificationInvoked += (_, args) => RaiseInvoked(args?.Argument);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"订阅通知点击事件失败：{ex.Message}");
        }
    }

    private static void RaiseInvoked(string? argument)
    {
        var value = argument ?? string.Empty;
        AppLog.Debug($"通知被点击，参数：{value}");

        if (_ui is { } ui)
        {
            ui.TryEnqueue(() => Invoked?.Invoke(value));
        }
        else
        {
            Invoked?.Invoke(value);
        }
    }

    // ---------------- 发送 ----------------

    /// <summary>发送一条通知。任何异常都不得影响调用方业务。</summary>
    /// <param name="title">标题。</param>
    /// <param name="body">正文。</param>
    /// <param name="argKey">点击回调的键（如 room / openpath）；为 null 表示点击不携带参数。</param>
    /// <param name="argValue">与 <paramref name="argKey"/> 配对的值，系统通道会自动做 URL 编码。</param>
    /// <param name="glyph">浮窗图标字形（Segoe Fluent Icons），为空时用默认消息图标。</param>
    internal static void Show(
        string title,
        string body,
        string? argKey = null,
        string? argValue = null,
        string? glyph = null)
    {
        try
        {
            Initialize();

            if (_channel == NotificationChannel.Off)
            {
                AppLog.Debug($"通知已关闭，丢弃：{title}");
                return;
            }

            var foreground = IsMainWindowForeground();

            // 自动模式：前台时用户本就能看到界面，只弹轻量浮窗；不在前台时系统通知与应用内浮窗都发，
            // 因为系统通知存在「注册成功但被系统策略吞掉」的情况，浮窗保证一定能看到。
            var wantSystem = _channel switch
            {
                NotificationChannel.System => true,
                NotificationChannel.InApp => false,
                _ => _systemRegistered && !foreground
            };

            var wantInApp = _channel switch
            {
                NotificationChannel.System => false,
                NotificationChannel.InApp => true,
                _ => true
            };

            if (wantSystem && !_systemRegistered)
            {
                AppLog.Debug("系统通道不可用，本次只发应用内浮窗。");
                wantSystem = false;
                wantInApp = _channel != NotificationChannel.System;
            }

            if (wantSystem)
            {
                ShowSystem(title, body, argKey, argValue);
            }

            if (wantInApp)
            {
                ShowInApp(title, body, argKey, argValue, glyph);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"发送通知失败：{title}", ex);
        }
    }

    private static void ShowSystem(string title, string body, string? argKey, string? argValue)
    {
        try
        {
            var builder = new AppNotificationBuilder().AddText(title).AddText(body);
            if (!string.IsNullOrWhiteSpace(argKey) && argValue is not null)
            {
                builder.AddArgument(argKey, argValue);
            }

            AppNotificationManager.Default.Show(builder.BuildNotification());
            _lastChannelUsed = "系统通知";
            AppLog.Debug($"已发送系统通知：{title} / {body}");
        }
        catch (Exception ex)
        {
            _systemRegistered = false;   // 下次发送前会重新注册
            _lastChannelUsed = "系统通知（失败）";
            AppLog.Error($"系统通知发送失败（0x{ex.HResult:X8}），下次将重试注册。", ex);
        }
    }

    private static void ShowInApp(string title, string body, string? argKey, string? argValue, string? glyph)
    {
        // 与系统通道保持一致：点击回传 "key=URL编码值"，解析端统一做解码。
        var invocation = string.IsNullOrWhiteSpace(argKey)
            ? null
            : $"{argKey}={Uri.EscapeDataString(argValue ?? string.Empty)}";

        void Create()
        {
            try
            {
                var toast = new ToastWindow(title, body, glyph);
                toast.Clicked += () => RaiseInvoked(invocation);
                toast.ShowToast();
                _lastChannelUsed = "应用内浮窗";
                AppLog.Debug($"已弹出应用内浮窗：{title}");
            }
            catch (Exception ex)
            {
                AppLog.Error("创建应用内浮窗失败。", ex);
            }
        }

        if (_ui is not { } ui)
        {
            // 主窗口还没建好就收到业务通知（理论上不会发生）：宁可不弹，也不要在非 UI 线程建窗口。
            AppLog.Debug("UI 派发器未就绪，跳过应用内浮窗。");
            return;
        }

        if (ui.HasThreadAccess)
        {
            Create();
        }
        else
        {
            ui.TryEnqueue(Create);
        }
    }

    private static bool IsMainWindowForeground()
    {
        try
        {
            var hwnd = MainWindow.Hwnd;
            return hwnd != 0 && GetForegroundWindow() == hwnd;
        }
        catch
        {
            return false;
        }
    }

    // ---------------- 业务入口 ----------------

    /// <summary>聊天消息提醒。点击后回到对应房间。</summary>
    internal static void NotifyChat(string title, string body, string room) =>
        Show(title, body, "room", room, "\uE8BD");   // Message

    /// <summary>下载完成。</summary>
    internal static void NotifyDownloadCompleted(string fileName, string directory) =>
        Show(Loc.T("Dl_Notify_Completed_Title"),
             Loc.Tf("Dl_Notify_Completed_Body_Format", fileName),
             "openpath",
             directory,
             "\uE896");   // Download

    /// <summary>下载失败（带原因）。引擎给出的是错误码，这里统一翻成用户能看懂的说明。</summary>
    internal static void NotifyDownloadFailed(string fileName, string? reason)
    {
        var text = LocalizeDownloadError(reason);
        Show(Loc.T("Dl_Notify_Failed_Title"),
             string.IsNullOrWhiteSpace(text)
                 ? Loc.Tf("Dl_Notify_Failed_Body_Format", fileName)
                 : Loc.Tf("Dl_Notify_Failed_Body_Reason_Format", fileName, text),
             null,
             null,
             "\uEA39");   // ErrorBadge
    }

    private static string LocalizeDownloadError(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        return raw switch
        {
            "DiskFull" => Loc.T("Dl_Error_DiskFull"),
            "RangeNotSupported" => Loc.T("Dl_Error_RangeNotSupported"),
            _ when raw.StartsWith("Incomplete:", StringComparison.Ordinal) => Loc.T("Dl_Error_Incomplete"),
            _ => raw
        };
    }

    // ---------------- 诊断 ----------------

    /// <summary>设置页展示用：一眼看出通知到底卡在哪一环。</summary>
    internal static string Describe()
    {
        Initialize();

        var builder = new StringBuilder();
        builder.AppendLine($"系统：{_osText}");
        builder.AppendLine($"运行身份：{(_elevated ? "管理员（通知不可用）" : "普通用户")}");
        builder.AppendLine($"系统通知总开关：{(IsToastEnabledByPolicy() ? "开启" : "已关闭")}");
        builder.AppendLine($"AUMID：{AppUserModelId}（注册表{(_aumidRegistered ? "已写入" : "未写入")}{(_aumidError is null ? "" : "：" + _aumidError)}）");
        builder.AppendLine($"系统通道：{(_systemRegistered ? "可用" : "不可用" + (_systemRegisterError is null ? "" : "：" + _systemRegisterError))}");
        builder.AppendLine($"当前方式：{ChannelText(_channel)}");
        builder.Append($"最近一次发送：{_lastChannelUsed}");
        return builder.ToString();
    }

    private static string ChannelText(NotificationChannel channel) => channel switch
    {
        NotificationChannel.Auto => Loc.T("Settings_Notify_Mode_Auto"),
        NotificationChannel.InApp => Loc.T("Settings_Notify_Mode_InApp"),
        NotificationChannel.System => Loc.T("Settings_Notify_Mode_System"),
        _ => Loc.T("Settings_Notify_Mode_Off")
    };

    /// <summary>设置页「发送测试通知」：同时验证两条通道，并把诊断结果回给调用方显示。</summary>
    internal static string Test()
    {
        Initialize();
        _lastChannelUsed = "无";

        Show("SteamEYA 测试通知",
             _systemRegistered
                 ? "如果你只看到这条浮窗、没看到系统横幅，说明系统通知被系统设置拦住了，可在设置里切到「仅应用内」。"
                 : "系统通知当前不可用，这条是应用内浮窗。具体原因见下方诊断信息。",
             null,
             null,
             "\uE7E7");

        return Describe();
    }

    // ---------------- P/Invoke ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevation
    {
        public int Elevated;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(nint tokenHandle, int infoClass, out TokenElevation info, int infoLength, out int returnLength);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appUserModelId);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();
}
