using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using SteamEyaWinUI.Localization;
using Windows.Storage.Streams;

namespace SteamEyaWinUI.Services;

/// <summary>一次全屏截图的原始像素（BGRA，行序自上而下）。</summary>
internal sealed class ScreenshotFrame
{
    public int Width { get; init; }

    public int Height { get; init; }

    public byte[] Pixels { get; init; } = [];

    /// <summary>主窗口所在显示器的缩放比例（DIP → 物理像素）。</summary>
    public double Scale { get; init; } = 1.0;
}

/// <summary>
/// 截图能力：全局热键、GDI 抓屏、裁剪、PNG 编码、写剪贴板。
/// 用 GDI 而不是 Windows.Graphics.Capture：无打包 + Native AOT 下前者零权限、零初始化，启动即用。
/// </summary>
internal static class ScreenshotService
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    /// <summary>WM_HOTKEY 里用来区分本功能的 id。</summary>
    public const int HotkeyId = 0x5A61;

    private const uint ModNoRepeat = 0x4000;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const uint Srccopy = 0x00CC0020;
    private const uint Captureblt = 0x40000000;
    private const uint BiRgb = 0;
    private const uint DibRgbColors = 0;

    private static nint _hotkeyWindow;

    /// <summary>注册全局热键；vk=0 视为禁用。返回是否注册成功（被别的程序占用会失败）。</summary>
    public static bool RegisterHotkey(nint hwnd, uint mods, uint vk)
    {
        UnregisterHotkey();

        if (hwnd == 0 || vk == 0)
        {
            return false;
        }

        var ok = RegisterHotKey(hwnd, HotkeyId, mods | ModNoRepeat, vk);
        if (ok)
        {
            _hotkeyWindow = hwnd;
        }

        return ok;
    }

    public static void UnregisterHotkey()
    {
        if (_hotkeyWindow != 0)
        {
            UnregisterHotKey(_hotkeyWindow, HotkeyId);
            _hotkeyWindow = 0;
        }
    }

    /// <summary>把修饰键与虚拟键格式化成 "Alt+A" 这类显示文本。</summary>
    public static string FormatHotkey(uint mods, uint vk)
    {
        if (vk == 0)
        {
            return string.Empty;
        }

        var text = new System.Text.StringBuilder();
        if ((mods & ModControl) != 0) text.Append("Ctrl+");
        if ((mods & ModAlt) != 0) text.Append("Alt+");
        if ((mods & ModShift) != 0) text.Append("Shift+");
        if ((mods & ModWin) != 0) text.Append("Win+");
        text.Append(KeyName(vk));
        return text.ToString();
    }

    private static string KeyName(uint vk)
    {
        if ((vk >= 'A' && vk <= 'Z') || (vk >= '0' && vk <= '9'))
        {
            return ((char)vk).ToString();
        }

        if (vk is >= 0x70 and <= 0x87)
        {
            return $"F{vk - 0x70 + 1}";
        }

        return vk switch
        {
            0x20 => "Space",
            0x0D => "Enter",
            0x1B => "Esc",
            0x09 => "Tab",
            0x2E => "Delete",
            0x2D => "Insert",
            0x24 => "Home",
            0x23 => "End",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x25 => "Left",
            0x27 => "Right",
            0x26 => "Up",
            0x28 => "Down",
            0xC0 => "`",
            0xBD => "-",
            0xBB => "=",
            0xDB => "[",
            0xDD => "]",
            0xDC => "\\",
            0xBA => ";",
            0xDE => "'",
            0xBC => ",",
            0xBE => ".",
            0xBF => "/",
            _ => $"0x{vk:X2}"
        };
    }

    /// <summary>热键触发入口：先抓屏，再打开全屏覆盖层。必须在 UI 线程调用。</summary>
    public static void BeginCapture()
    {
        var frame = CapturePrimaryScreen();
        if (frame is null)
        {
            AppState.ShowStatus(Loc.T("Screenshot_CaptureFailed"), Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
            return;
        }

        var window = new SteamEyaWinUI.ScreenshotWindow(frame);
        window.Activate();
        window.BringToFront();
    }

    /// <summary>抓取主显示器整屏。失败返回 null（例如 UAC 安全桌面、锁屏）。</summary>
    public static ScreenshotFrame? CapturePrimaryScreen()
    {
        var width = GetSystemMetrics(SmCxScreen);
        var height = GetSystemMetrics(SmCyScreen);
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var screenDc = GetDC(0);
        if (screenDc == 0)
        {
            return null;
        }

        nint memDc = 0;
        nint bitmap = 0;
        nint previous = 0;
        try
        {
            memDc = CreateCompatibleDC(screenDc);
            bitmap = CreateCompatibleBitmap(screenDc, width, height);
            if (memDc == 0 || bitmap == 0)
            {
                return null;
            }

            previous = SelectObject(memDc, bitmap);
            if (!BitBlt(memDc, 0, 0, width, height, screenDc, 0, 0, Srccopy | Captureblt))
            {
                return null;
            }

            SelectObject(memDc, previous);
            previous = 0;

            var pixels = new byte[width * height * 4];
            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    // 负高度 = 自上而下，和 BitmapEncoder 的行序一致。
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = BiRgb
                }
            };

            var lines = GetDIBits(memDc, bitmap, 0, (uint)height, pixels, ref info, DibRgbColors);
            if (lines == 0)
            {
                return null;
            }

            // 32bpp BI_RGB 的 alpha 通常是 0，直接交给编码器会得到全透明图，这里统一补成不透明。
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
            }

            return new ScreenshotFrame
            {
                Width = width,
                Height = height,
                Pixels = pixels,
                Scale = GetWindowScale()
            };
        }
        catch (Exception ex)
        {
            AppLog.Warn($"截屏失败：{ex.Message}");
            return null;
        }
        finally
        {
            if (previous != 0 && memDc != 0)
            {
                SelectObject(memDc, previous);
            }

            if (bitmap != 0)
            {
                DeleteObject(bitmap);
            }

            if (memDc != 0)
            {
                DeleteDC(memDc);
            }

            ReleaseDC(0, screenDc);
        }
    }

    private static double GetWindowScale()
    {
        try
        {
            var hwnd = MainWindow.Hwnd;
            if (hwnd == 0)
            {
                return 1.0;
            }

            var dpi = GetDpiForWindow(hwnd);
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
        catch
        {
            return 1.0;
        }
    }

    /// <summary>按物理像素裁剪（自动夹到图像范围内）。</summary>
    public static byte[] Crop(ScreenshotFrame frame, int x, int y, int width, int height)
    {
        x = Math.Clamp(x, 0, frame.Width - 1);
        y = Math.Clamp(y, 0, frame.Height - 1);
        width = Math.Clamp(width, 1, frame.Width - x);
        height = Math.Clamp(height, 1, frame.Height - y);

        var result = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
        {
            var sourceOffset = ((y + row) * frame.Width + x) * 4;
            System.Buffer.BlockCopy(frame.Pixels, sourceOffset, result, row * width * 4, width * 4);
        }

        return result;
    }

    /// <summary>把 BGRA 像素编码为 PNG 字节。</summary>
    public static async Task<byte[]> EncodePngAsync(byte[] bgra, int width, int height)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            (uint)width,
            (uint)height,
            96,
            96,
            bgra);
        await encoder.FlushAsync();

        stream.Seek(0);
        var bytes = new byte[(int)stream.Size];
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    /// <summary>把 PNG 字节放入剪贴板（Flush 后即使本程序退出，粘贴仍然可用）。</summary>
    public static async Task<bool> CopyToClipboardAsync(byte[] png)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(png);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);

            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            Clipboard.SetContent(package);
            Clipboard.Flush();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"写入剪贴板失败：{ex.Message}");
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDc);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(nint hdcDest, int x, int y, int width, int height, nint hdcSrc, int srcX, int srcY, uint rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(nint hdc, nint bitmap, uint start, uint lines, byte[] bits, ref BitmapInfo info, uint usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
