using System.Runtime.InteropServices;

namespace SteamEyaWinUI.Services.RemoteAssist;

/// <summary>
/// GDI BitBlt 兜底采集：主显示器 → 32 位 DIB → 内存。
///
/// 什么时候会用到它：DXGI 桌面复制不可用（双显卡错配、远程桌面会话、虚拟显示器、
/// 某些显卡驱动），或者被独占全屏挡住。代价是 CPU 拷贝（1080p 一帧约 8 MB），
/// 所以调用方会自动把帧率压到 10~15fps。宁可卡一点，也不要黑屏。
/// </summary>
internal sealed partial class GdiCapture : ICaptureSource
{
    private const uint Srccopy = 0x00CC0020;
    private const uint Captureblt = 0x40000000;

    private readonly nint _screenDc;
    private readonly nint _memDc;
    private readonly nint _bitmap;
    private readonly nint _oldBitmap;
    private readonly nint _bits;
    private readonly byte[] _buffer;

    private GdiCapture(nint screenDc, nint memDc, nint bitmap, nint oldBitmap, nint bits,
        int width, int height)
    {
        _screenDc = screenDc;
        _memDc = memDc;
        _bitmap = bitmap;
        _oldBitmap = oldBitmap;
        _bits = bits;
        Width = width;
        Height = height;
        _buffer = new byte[width * height * 4];
    }

    public int Width { get; }

    public int Height { get; }

    public int DesktopX => 0;

    public int DesktopY => 0;

    public bool GpuAvailable => false;

    public static GdiCapture Create()
    {
        var width = GetSystemMetrics(0);   // SM_CXSCREEN
        var height = GetSystemMetrics(1);  // SM_CYSCREEN
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("读不到主显示器尺寸。");
        }

        var screenDc = GetDC(nint.Zero);
        if (screenDc == nint.Zero)
        {
            throw new InvalidOperationException("GetDC 失败。");
        }

        var memDc = CreateCompatibleDC(screenDc);
        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height,       // 负高度 = 自上而下，省掉逐行翻转
                Planes = 1,
                BitCount = 32,
                Compression = 0,        // BI_RGB
            },
        };

        var bitmap = CreateDIBSection(memDc, ref info, 0, out var bits, nint.Zero, 0);
        if (bitmap == nint.Zero || bits == nint.Zero)
        {
            DeleteDC(memDc);
            ReleaseDC(nint.Zero, screenDc);
            throw new InvalidOperationException("CreateDIBSection 失败。");
        }

        var old = SelectObject(memDc, bitmap);
        return new GdiCapture(screenDc, memDc, bitmap, old, bits, width, height);
    }

    public bool TryAcquire(int timeoutMs, out CapturedFrame frame)
    {
        frame = default;

        var ok = BitBlt(_memDc, 0, 0, Width, Height, _screenDc, 0, 0, Srccopy | Captureblt);
        if (!ok)
        {
            return false;
        }

        Marshal.Copy(_bits, _buffer, 0, _buffer.Length);

        var cursor = default(CaptureCursor);
        var info = new CursorInfo { Size = (uint)Marshal.SizeOf<CursorInfo>() };
        if (GetCursorInfo(ref info))
        {
            cursor = new CaptureCursor((info.Flags & 1) != 0, info.ScreenPosition.X, info.ScreenPosition.Y);
        }

        frame = new CapturedFrame(true, null, _buffer, cursor);
        return true;
    }

    public void Release()
    {
        // GDI 没有 Acquire/Release 配对，空实现。
    }

    public void Dispose()
    {
        try
        {
            SelectObject(_memDc, _oldBitmap);
            DeleteObject(_bitmap);
            DeleteDC(_memDc);
            ReleaseDC(nint.Zero, _screenDc);
        }
        catch
        {
            // 忽略
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

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public uint Size;
        public uint Flags;
        public nint Cursor;
        public Point32 ScreenPosition;
    }

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint hwnd, nint dc);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorInfo(ref CursorInfo info);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage,
        out nint bits, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(nint dest, int x, int y, int width, int height,
        nint src, int srcX, int srcY, uint rop);
}
