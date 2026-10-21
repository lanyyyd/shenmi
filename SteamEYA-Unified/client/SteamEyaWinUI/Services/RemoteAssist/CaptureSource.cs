using Vortice.Direct3D11;

namespace SteamEyaWinUI.Services.RemoteAssist;

internal readonly record struct CaptureCursor(bool Visible, float X, float Y);

/// <summary>一帧采集结果。Texture 非空 = GPU 纹理（走 VideoProcessor 转 NV12）；否则 Bgra 为 CPU 像素。</summary>
internal readonly record struct CapturedFrame(bool DesktopUpdated, ID3D11Texture2D? Texture, byte[]? Bgra, CaptureCursor Cursor);

/// <summary>
/// 屏幕采集抽象。两条实现：
///  · <see cref="DesktopCapture"/>：DXGI 桌面复制，GPU 纹理、能检测"画面有没有变"，首选；
///  · <see cref="GdiCapture"/>：GDI BitBlt 兜底，CPU 像素，任何环境都能出图（含远程桌面会话、
///    双显卡错配、虚拟显示器等 DXGI 复制不可用的场景）。
/// </summary>
internal interface ICaptureSource : IDisposable
{
    int Width { get; }

    int Height { get; }

    /// <summary>采集区域在虚拟桌面中的左上角（物理像素），注入坐标时要用。</summary>
    int DesktopX { get; }

    int DesktopY { get; }

    /// <summary>true = 走 GPU 纹理路径（<see cref="CapturedFrame.Texture"/>）。</summary>
    bool GpuAvailable { get; }

    /// <summary>尝试取一帧；返回 false 表示这次没取到（超时/无变化），调用方直接下一轮。</summary>
    bool TryAcquire(int timeoutMs, out CapturedFrame frame);

    /// <summary>释放当前帧（DXGI 必须 Acquire/Release 配对）。</summary>
    void Release();
}

internal static class CaptureFactory
{
    /// <summary>
    /// 建采集源：先试 DXGI，失败自动降级到 GDI，并把失败原因写进日志/异常，
    /// 避免再出现"只有一句找不到显示器输出"这种没法查的报错。
    /// </summary>
    public static ICaptureSource Create(out DxContext? dx)
    {
        string dxError;
        try
        {
            var capture = DesktopCapture.Create(out var context);
            dx = context;
            return capture;
        }
        catch (Exception ex)
        {
            dx = null;
            dxError = ex.Message;
            AppLog.Warn($"DXGI 桌面复制不可用，降级为 GDI 采集：{ex.Message}");
        }

        try
        {
            var capture = GdiCapture.Create();
            AppLog.Warn($"已使用 GDI 兜底采集（{capture.Width}×{capture.Height}），帧率与 CPU 占用会差一些。");
            return capture;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"屏幕采集失败。DXGI：{dxError}；GDI：{ex.Message}");
        }
    }
}
