using QuickAnswer.Core.Models;

namespace QuickAnswer.Core.Capture;

/// <summary>一张截图的原始像素（BGRA8888，行优先，无 padding 之外的额外间距）。</summary>
public sealed class CapturedImage
{
    public int Width { get; init; }
    public int Height { get; init; }
    public byte[] Bgra { get; init; } = Array.Empty<byte>();

    /// <summary>这张图在虚拟桌面里的位置（物理像素）。OCR 结果的 BoundingRect 要加这个偏移才是屏幕坐标。</summary>
    public ScreenRect SourceRect { get; init; }
}

/// <summary>
/// 屏幕捕获。用 GDI BitBlt 而不是 PrintWindow / Windows.Graphics.Capture：
///   - PrintWindow 只能拿到单窗口，拿不到「屏幕上实际显示的样子」；
///   - GraphicsCapture 有黄框、焦点切换、Win10 兼容性一堆坑，开销也大；
///   - BitBlt 对任意窗口的合成结果都有效，1080p 区域约 5–15ms。
/// 前提是进程已声明 PerMonitorV2 DPI 感知（见 app.manifest），否则 150% 缩放下坐标会偏。
/// </summary>
public static class ScreenCapturer
{
    /// <summary>虚拟桌面（所有显示器合并）的边界，物理像素。</summary>
    public static ScreenRect VirtualScreen() => new(
        NativeGdi.GetSystemMetrics(NativeGdi.SM_XVIRTUALSCREEN),
        NativeGdi.GetSystemMetrics(NativeGdi.SM_YVIRTUALSCREEN),
        NativeGdi.GetSystemMetrics(NativeGdi.SM_CXVIRTUALSCREEN),
        NativeGdi.GetSystemMetrics(NativeGdi.SM_CYVIRTUALSCREEN));

    /// <summary>截取指定区域。坐标为虚拟桌面物理像素，可以是负值（左侧副屏）。</summary>
    public static CapturedImage? Capture(ScreenRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return null;

        IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, hBmp = IntPtr.Zero, oldObj = IntPtr.Zero;
        try
        {
            screenDc = NativeGdi.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
                return null;

            memDc = NativeGdi.CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero)
                return null;

            hBmp = NativeGdi.CreateCompatibleBitmap(screenDc, rect.Width, rect.Height);
            if (hBmp == IntPtr.Zero)
                return null;

            oldObj = NativeGdi.SelectObject(memDc, hBmp);

            if (!NativeGdi.BitBlt(memDc, 0, 0, rect.Width, rect.Height,
                                  screenDc, rect.X, rect.Y, NativeGdi.SRCCOPY))
                return null;

            NativeGdi.SelectObject(memDc, oldObj);
            oldObj = IntPtr.Zero;

            var pixels = ReadPixels(memDc, hBmp, rect.Width, rect.Height);
            if (pixels is null)
                return null;

            return new CapturedImage
            {
                Width = rect.Width,
                Height = rect.Height,
                Bgra = pixels,
                SourceRect = rect,
            };
        }
        catch
        {
            return null;
        }
        finally
        {
            if (oldObj != IntPtr.Zero && memDc != IntPtr.Zero)
                NativeGdi.SelectObject(memDc, oldObj);
            if (hBmp != IntPtr.Zero)
                NativeGdi.DeleteObject(hBmp);
            if (memDc != IntPtr.Zero)
                NativeGdi.DeleteDC(memDc);
            if (screenDc != IntPtr.Zero)
                NativeGdi.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>
    /// 截取并缩放到指定尺寸的灰度图 —— 只给 dHash 用。
    /// 这一步是「1 秒一次检测但不烧 CPU」的关键：9×8 的 StretchBlt 约 1–3ms，
    /// 相比之下一次 OCR 要 60–120ms，差两个数量级。
    /// </summary>
    public static byte[]? CaptureGray(ScreenRect rect, int w, int h)
    {
        if (rect.Width <= 0 || rect.Height <= 0 || w <= 0 || h <= 0)
            return null;

        IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, hBmp = IntPtr.Zero, oldObj = IntPtr.Zero;
        try
        {
            screenDc = NativeGdi.GetDC(IntPtr.Zero);
            memDc = NativeGdi.CreateCompatibleDC(screenDc);
            hBmp = NativeGdi.CreateCompatibleBitmap(screenDc, w, h);
            oldObj = NativeGdi.SelectObject(memDc, hBmp);

            NativeGdi.SetStretchBltMode(memDc, NativeGdi.HALFTONE);
            if (!NativeGdi.StretchBlt(memDc, 0, 0, w, h, screenDc, rect.X, rect.Y, rect.Width, rect.Height,
                                      NativeGdi.SRCCOPY))
                return null;

            NativeGdi.SelectObject(memDc, oldObj);
            oldObj = IntPtr.Zero;

            var bgra = ReadPixels(memDc, hBmp, w, h);
            if (bgra is null)
                return null;

            var gray = new byte[w * h];
            for (int i = 0, p = 0; i < gray.Length; i++, p += 4)
            {
                // ITU-R BT.601 亮度，整数近似
                gray[i] = (byte)((bgra[p + 2] * 77 + bgra[p + 1] * 150 + bgra[p] * 29) >> 8);
            }
            return gray;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (oldObj != IntPtr.Zero && memDc != IntPtr.Zero)
                NativeGdi.SelectObject(memDc, oldObj);
            if (hBmp != IntPtr.Zero) NativeGdi.DeleteObject(hBmp);
            if (memDc != IntPtr.Zero) NativeGdi.DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) NativeGdi.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>
    /// 截取并放大 —— 给 OCR 用。
    /// 放大倍数不是可选项：单字高度低于 12px 时 Windows OCR 的准确率断崖式下跌，
    /// 2 倍双三次放大的收益远大于任何后处理滤镜。
    /// </summary>
    public static CapturedImage? CaptureScaled(ScreenRect rect, double scale)
    {
        int w = Math.Max(1, (int)Math.Round(rect.Width * scale));
        int h = Math.Max(1, (int)Math.Round(rect.Height * scale));

        // 超过 OCR 引擎上限（10000px）就退回去，否则 RecognizeAsync 会抛
        if (w > 8000 || h > 8000)
            return Capture(rect);

        if (Math.Abs(scale - 1.0) < 0.01)
            return Capture(rect);

        IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, hBmp = IntPtr.Zero, oldObj = IntPtr.Zero;
        try
        {
            screenDc = NativeGdi.GetDC(IntPtr.Zero);
            memDc = NativeGdi.CreateCompatibleDC(screenDc);
            hBmp = NativeGdi.CreateCompatibleBitmap(screenDc, w, h);
            oldObj = NativeGdi.SelectObject(memDc, hBmp);

            NativeGdi.SetStretchBltMode(memDc, NativeGdi.HALFTONE);
            if (!NativeGdi.StretchBlt(memDc, 0, 0, w, h, screenDc, rect.X, rect.Y, rect.Width, rect.Height,
                                      NativeGdi.SRCCOPY))
                return null;

            NativeGdi.SelectObject(memDc, oldObj);
            oldObj = IntPtr.Zero;

            var pixels = ReadPixels(memDc, hBmp, w, h);
            if (pixels is null)
                return null;

            return new CapturedImage { Width = w, Height = h, Bgra = pixels, SourceRect = rect };
        }
        catch
        {
            return null;
        }
        finally
        {
            if (oldObj != IntPtr.Zero && memDc != IntPtr.Zero)
                NativeGdi.SelectObject(memDc, oldObj);
            if (hBmp != IntPtr.Zero) NativeGdi.DeleteObject(hBmp);
            if (memDc != IntPtr.Zero) NativeGdi.DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) NativeGdi.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static byte[]? ReadPixels(IntPtr hdc, IntPtr hBmp, int w, int h)
    {
        var bmi = new NativeGdi.BITMAPINFOHEADER
        {
            biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeGdi.BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h,             // 负数 = 自上而下，省一次翻转
            biPlanes = 1,
            biBitCount = 32,
            biCompression = NativeGdi.BI_RGB,
        };

        var buffer = new byte[w * h * 4];
        int lines = NativeGdi.GetDIBits(hdc, hBmp, 0, (uint)h, buffer, ref bmi, NativeGdi.DIB_RGB_COLORS);
        return lines == 0 ? null : buffer;
    }
}
