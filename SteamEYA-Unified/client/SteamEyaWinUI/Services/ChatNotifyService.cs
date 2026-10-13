using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 聊天消息的系统级提醒：
/// · 桌面通知横幅走 Windows App SDK 的 <see cref="AppNotificationManager"/>（Win10 1809+ / Win11 均可）；
/// · 任务栏图标闪红走 <c>user32!FlashWindowEx</c>（FLASHW_TRAY，窗口重新获得焦点即停——Win10/Win11 一致）。
///
/// 未打包应用同样支持：<see cref="AppNotificationManager.Register"/> 会自动配置 COM 激活，无需手工 AUMID。
/// 注册/发送失败都会完整写入日志（含 HRESULT），便于定位"通知不弹"的具体原因；失败不再永久闩死——
/// 之后每条消息都会重试注册，一旦环境恢复即可自动生效。
/// 注意：以管理员权限运行时 Windows 不支持应用通知。
/// </summary>
internal static partial class ChatNotifyService
{
    private const uint FLASHW_TRAY = 0x00000002;
    private const uint FLASHW_TIMERNOFG = 0x0000000C;

    private static bool _handlersWired;
    private static bool _registered;
    private static bool _failLogged;         // 避免每次消息都重复刷失败日志
    private static DispatcherQueue? _ui;

    /// <summary>点击通知后回调，参数为通知携带的 argument（如 "room=group"）。始终在 UI 线程触发。</summary>
    public static event Action<string>? Invoked;

    /// <summary>注入 UI 派发器（由 MainWindow 创建时调用），保证点击回调回到 UI 线程。</summary>
    public static void SetUiDispatcher(DispatcherQueue ui) => _ui = ui;

    /// <summary>注册通知通道（幂等）。启动时即调用一次，之后按需重试。</summary>
    public static void Initialize()
    {
        Wire();
        if (_registered)
        {
            return;
        }
        try
        {
            AppNotificationManager.Default.Register();
            _registered = true;
            _failLogged = false;
            AppLog.Info("系统通知已注册（AppNotificationManager）。");
        }
        catch (Exception ex)
        {
            _registered = false;
            if (!_failLogged)
            {
                _failLogged = true;
                AppLog.Error($"系统通知注册失败（0x{ex.HResult:X8}），将仅闪烁任务栏。", ex);
            }
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
            // 官方要求：先订阅点击回调，再调用 Register。
            AppNotificationManager.Default.NotificationInvoked += (_, args) => OnInvoked(args);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"订阅通知点击事件失败：{ex.Message}");
        }
    }

    private static void OnInvoked(AppNotificationActivatedEventArgs args)
    {
        var arg = args?.Argument ?? string.Empty;
        if (_ui is { } ui)
        {
            ui.TryEnqueue(() => Invoked?.Invoke(arg));
        }
        else
        {
            Invoked?.Invoke(arg);
        }
    }

    /// <summary>弹一条系统通知并闪烁任务栏。任何异常都不应影响聊天主流程。</summary>
    public static void Notify(string title, string body) => Notify(title, body, null);

    /// <summary>弹一条系统通知（带点击参数）并闪烁任务栏。</summary>
    public static void Notify(string title, string body, string? argument)
    {
        FlashTaskbar();

        if (!_registered)
        {
            Initialize();       // 惰性重试：上次注册失败的环境可能已恢复
        }
        if (!_registered)
        {
            return;
        }

        try
        {
            var builder = new AppNotificationBuilder().AddText(title).AddText(body);
            if (!string.IsNullOrEmpty(argument))
            {
                builder.AddArgument("room", argument);
            }
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception ex)
        {
            _registered = false;    // 发送失败：下次消息重试注册
            AppLog.Error($"发送系统通知失败（0x{ex.HResult:X8}）。", ex);
        }
    }

    /// <summary>发送一条测试通知，供用户在设置页自检通知是否可用。</summary>
    public static void Test()
    {
        Initialize();
        var body = _registered
            ? "系统通知工作正常。若没看到横幅，请检查系统「专注助手 / 勿扰」设置。"
            : "系统通知不可用：请看数据目录 logs\\steameya.log 中的注册失败原因。";
        Notify("SteamEYA 测试通知", body);
    }

    /// <summary>闪烁任务栏图标（托盘区）直到窗口被激活。</summary>
    public static void FlashTaskbar()
    {
        var hwnd = MainWindow.Hwnd;
        if (hwnd == 0)
        {
            return;
        }
        try
        {
            var info = new FlashWindowInfo
            {
                Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
                Hwnd = hwnd,
                Flags = FLASHW_TRAY | FLASHW_TIMERNOFG,
                Count = 0,
                Timeout = 0
            };
            FlashWindowEx(ref info);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"闪烁任务栏失败：{ex.Message}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public nint Hwnd;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FlashWindowInfo info);
}
