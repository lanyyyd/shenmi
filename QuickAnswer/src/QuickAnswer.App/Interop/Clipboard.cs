using System.Runtime.InteropServices;
using static QuickAnswer.App.Interop.NativeMethods;

namespace QuickAnswer.App.Interop;

/// <summary>
/// 往剪贴板写一段纯文本（CF_UNICODETEXT）。
///
/// 为什么不用 Windows.ApplicationModel.DataTransfer.Clipboard：
///   · 未打包的 WinUI 3 里它要求调用线程带 DispatcherQueue，后台线程直接抛；
///   · 少数机器上会抛「类未注册」（0x80040154），错误信息还看不出原因。
/// 而这里需要的只是「把一串字放进剪贴板」，两句 Win32 就够，不依赖 COM 或窗口。
///
/// 另一个必须处理的现实：OpenClipboard 是**全局独占锁**。微信、输入法、
/// 各种剪贴板管理器随手就会把剪贴板占住，单次失败非常正常。
/// 所以这里带短重试 —— 失败后等一会儿再来，总共不超过约 150ms，
/// 免得把 UI 线程卡出可感知的停顿。
/// </summary>
internal static class ClipboardWriter
{
    private const int Attempts = 10;
    private const int RetryDelayMs = 15;

    /// <summary>写入成功返回 true。失败是常事（剪贴板被别的程序占着），调用方不需要报错。</summary>
    public static bool SetText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        for (int i = 0; i < Attempts; i++)
        {
            if (TrySet(text))
                return true;

            Thread.Sleep(RetryDelayMs);
        }

        return false;
    }

    private static bool TrySet(string text)
    {
        if (!OpenClipboard(IntPtr.Zero))
            return false;

        IntPtr handle = IntPtr.Zero;
        try
        {
            if (!EmptyClipboard())
                return false;

            // +1 是结尾的 UTF-16 NUL，Windows 要求 CF_UNICODETEXT 以双字节 0 收尾
            int bytes = (text.Length + 1) * sizeof(char);
            handle = GlobalAlloc(GMEM_MOVEABLE, new UIntPtr((uint)bytes));
            if (handle == IntPtr.Zero)
                return false;

            IntPtr target = GlobalLock(handle);
            if (target == IntPtr.Zero)
                return false;

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                Marshal.WriteInt16(target, text.Length * sizeof(char), 0);
            }
            finally
            {
                GlobalUnlock(handle);
            }

            if (SetClipboardData(CF_UNICODETEXT, handle) == IntPtr.Zero)
                return false;

            // 交接成功：这块内存归系统所有，下面不能再 GlobalFree
            handle = IntPtr.Zero;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (handle != IntPtr.Zero)
                GlobalFree(handle);

            CloseClipboard();
        }
    }
}
