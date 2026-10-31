using System.Runtime.InteropServices;

namespace QuickAnswer.Core.Capture;

/// <summary>GDI / User32 的最小 P/Invoke 集合。集中放一处，避免散落。</summary>
internal static class NativeGdi
{
    public const int SRCCOPY = 0x00CC0020;
    public const int HALFTONE = 4;
    public const int DIB_RGB_COLORS = 0;
    public const int BI_RGB = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h,
                                     IntPtr hdcSrc, int srcX, int srcY, int rop);

    [DllImport("gdi32.dll")]
    public static extern bool StretchBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
                                         IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, int rop);

    [DllImport("gdi32.dll")]
    public static extern int SetStretchBltMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    public static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines,
                                       byte[] bits, ref BITMAPINFOHEADER bmi, int usage);

    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;
}
