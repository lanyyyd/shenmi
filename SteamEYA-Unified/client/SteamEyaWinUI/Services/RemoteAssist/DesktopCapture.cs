using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SteamEyaWinUI.Services.RemoteAssist;

internal readonly record struct CaptureCursor(bool Visible, float X, float Y, float HotspotX, float HotspotY);

/// <summary>
/// DXGI 桌面复制（Desktop Duplication）：拿主显示器的 BGRA 纹理 + 光标位置。
///
/// 用桌面复制而不是 GDI/Windows.Graphics.Capture 的原因：它给出"帧是否变化"的信号
/// （AccumulatedFrames / LastPresentTime），静止画面时可以完全跳过编码，局域网带宽直接降到 0。
/// </summary>
internal sealed class DesktopCapture : IDisposable
{
    private readonly DxContext _dx;
    private readonly IDXGIOutputDuplication _duplication;
    private bool _frameAcquired;

    private DesktopCapture(DxContext dx, IDXGIOutputDuplication duplication,
        int x, int y, int width, int height)
    {
        _dx = dx;
        _duplication = duplication;
        DesktopX = x;
        DesktopY = y;
        Width = width;
        Height = height;
    }

    /// <summary>该显示器在虚拟桌面中的左上角（物理像素），注入坐标时要加上。</summary>
    public int DesktopX { get; }

    public int DesktopY { get; }

    public int Width { get; }

    public int Height { get; }

    public static DesktopCapture Create(DxContext dx, int outputIndex = 0)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        // 依次枚举显卡与输出，取第一个可用输出（通常是主显示器）。
        for (var a = 0; ; a++)
        {
            if (factory.EnumAdapters1((uint)a, out var adapter).Failure)
            {
                break;
            }

            using (adapter)
            {
                for (var o = 0; ; o++)
                {
                    if (adapter.EnumOutputs((uint)o, out var output).Failure)
                    {
                        break;
                    }

                    using (output)
                    {
                        using var output1 = output.QueryInterface<IDXGIOutput1>();
                        try
                        {
                            var duplication = output1.DuplicateOutput(dx.Device);
                            var desc = output.Description;
                            var left = desc.DesktopCoordinates.Left;
                            var top = desc.DesktopCoordinates.Top;
                            var width = desc.DesktopCoordinates.Right - left;
                            var height = desc.DesktopCoordinates.Bottom - top;
                            if (width <= 0 || height <= 0)
                            {
                                duplication.Dispose();
                                continue;
                            }
                            return new DesktopCapture(dx, duplication, left, top, width, height);
                        }
                        catch (SharpGenException)
                        {
                            // 该输出不可复制（例如独占全屏 / 受保护内容），换下一个。
                        }
                    }
                }
            }
        }

        throw new InvalidOperationException("找不到可用的显示器输出（可能被独占全屏或受保护内容占用）。");
    }

    /// <summary>
    /// 等待一帧。返回 false 表示超时（画面没变）。成功时调用方必须在处理完后调用 <see cref="Release"/>。
    /// </summary>
    public bool TryAcquire(int timeoutMs, out ID3D11Texture2D? texture, out bool desktopUpdated,
        out CaptureCursor cursor)
    {
        texture = null;
        desktopUpdated = false;
        cursor = default;

        var result = _duplication.AcquireNextFrame((uint)timeoutMs, out var frameInfo, out var resource);
        if (result.Failure)
        {
            // WAIT_TIMEOUT（0x887A0027）是正常现象：这一帧没变化。
            return false;
        }

        _frameAcquired = true;

        var pointer = frameInfo.PointerPosition;
        cursor = new CaptureCursor(pointer.Visible, pointer.Position.X, pointer.Position.Y, 0, 0);
        desktopUpdated = frameInfo.LastPresentTime != 0 || frameInfo.AccumulatedFrames > 0;

        try
        {
            using (resource)
            {
                texture = resource.QueryInterface<ID3D11Texture2D>();
            }
        }
        catch
        {
            Release();
            return false;
        }

        return true;
    }

    /// <summary>释放当前帧（DXGI 要求每 Acquire 必须配对 Release，否则后续 Acquire 会一直失败）。</summary>
    public void Release()
    {
        if (!_frameAcquired)
        {
            return;
        }
        _frameAcquired = false;
        try
        {
            _duplication.ReleaseFrame();
        }
        catch
        {
            // 释放失败不影响后续流程
        }
    }

    public void Dispose()
    {
        Release();
        _duplication.Dispose();
    }
}
