using System.Runtime.InteropServices;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 聊天消息的系统级提醒：
/// · 桌面通知横幅走 Windows App SDK 的 <see cref="AppNotificationManager"/>（Win10 1809+ / Win11 均可）；
/// · 任务栏图标闪红走 <c>user32!FlashWindowEx</c>（FLASHW_TRAY，窗口重新获得焦点即停——Win10/Win11 一致）。
/// 两者都做了降级：通知注册失败（如无开始菜单快捷方式 / 未打包环境受限）只记日志，闪图标仍照常工作。
/// </summary>
internal static partial class ChatNotifyService
{
    private const uint FLASHW_TRAY = 0x00000002;
    private const uint FLASHW_TIMERNOFG = 0x0000000C;

    private static bool _registered;
    private static bool _registerFailed;

    /// <summary>注册通知通道。首个聊天页打开时调用一次即可；重复调用安全。</summary>
    public static void Initialize()
    {
        if (_registered || _registerFailed)
        {
            return;
        }
        try
        {
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            _registerFailed = true;
            AppLog.Warn($"系统通知不可用（{ex.Message}），将仅闪烁任务栏。");
        }
    }

    /// <summary>弹一条系统通知并闪烁任务栏。任何异常都不应影响聊天主流程。</summary>
    public static void Notify(string title, string body)
    {
        FlashTaskbar();

        if (!_registered)
        {
            return;
        }
        try
        {
            var notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(body)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"发送系统通知失败：{ex.Message}");
        }
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
