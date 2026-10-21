using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SteamEyaWinUI.Services.RemoteAssist;

/// <summary>
/// DXGI 桌面复制（Desktop Duplication）：拿主显示器的 BGRA 纹理 + 光标位置。
///
/// 用桌面复制而不是 GDI 的原因：它给出"帧是否变化"的信号（AccumulatedFrames / LastPresentTime），
/// 静止画面时可以完全跳过编码，局域网带宽直接降到 0。
///
/// 两个必须做对的点（否则一律失败，错误码只有 DXGI_ERROR_UNSUPPORTED，很难看出原因）：
///  1. 设备必须建在**该显示器所属的适配器**上 —— 笔记本双显卡时桌面常由核显合成，
///     而 D3D11CreateDevice(null, Hardware) 拿到的可能是独显；
///  2. 输出的 DesktopCoordinates 左上角是 (0,0) 的才是主显示器，优先拿它。
/// 失败原因逐条累积进异常信息，方便定位是适配器错配、独占全屏还是会话问题。
/// </summary>
internal sealed class DesktopCapture : ICaptureSource
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

    public int DesktopX { get; }

    public int DesktopY { get; }

    public int Width { get; }

    public int Height { get; }

    public bool GpuAvailable => true;

    public static DesktopCapture Create(out DxContext dx)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        var errors = new List<string>();

        // pass 0 只找主显示器（左上角 0,0），pass 1 退而求其次接受任意输出。
        for (var pass = 0; pass < 2; pass++)
        {
            for (uint a = 0; ; a++)
            {
                IDXGIAdapter1? adapter;
                try
                {
                    if (factory.EnumAdapters1(a, out adapter).Failure || adapter is null)
                    {
                        break;
                    }
                }
                catch
                {
                    break;
                }

                using (adapter)
                {
                    DxContext? candidate = null;
                    try
                    {
                        candidate = DxContext.Create(adapter);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"适配器{a}（{SafeName(adapter)}）建 D3D 设备失败：{ex.Message}");
                    }

                    if (candidate is null)
                    {
                        continue;
                    }

                    for (uint o = 0; ; o++)
                    {
                        IDXGIOutput? output;
                        try
                        {
                            if (adapter.EnumOutputs(o, out output).Failure || output is null)
                            {
                                break;
                            }
                        }
                        catch
                        {
                            break;
                        }

                        using (output)
                        {
                            var rect = output.Description.DesktopCoordinates;
                            var width = rect.Right - rect.Left;
                            var height = rect.Bottom - rect.Top;
                            if (width <= 0 || height <= 0)
                            {
                                continue;
                            }

                            var isPrimary = rect.Left == 0 && rect.Top == 0;
                            if (pass == 0 && !isPrimary)
                            {
                                continue;
                            }

                            try
                            {
                                using var output1 = output.QueryInterface<IDXGIOutput1>();
                                var duplication = output1.DuplicateOutput(candidate.Device);
                                dx = candidate;
                                return new DesktopCapture(candidate, duplication, rect.Left, rect.Top,
                                    width, height);
                            }
                            catch (SharpGenException ex)
                            {
                                errors.Add($"适配器{a}/输出{o}（{width}×{height}，主显示器={isPrimary}）：{ex.Message}");
                            }
                            catch (Exception ex)
                            {
                                errors.Add($"适配器{a}/输出{o}：{ex.Message}");
                            }
                        }
                    }

                    candidate.Dispose();
                }
            }
        }

        dx = null!;
        throw new InvalidOperationException(errors.Count == 0
            ? "没有枚举到任何显示输出。"
            : string.Join("；", errors));
    }

    private static string SafeName(IDXGIAdapter1 adapter)
    {
        try
        {
            return adapter.Description1.Description;
        }
        catch
        {
            return "?";
        }
    }

    /// <summary>
    /// 等待一帧。返回 false 表示超时（画面没变）。成功时调用方必须在处理完后调用 <see cref="Release"/>。
    /// </summary>
    public bool TryAcquire(int timeoutMs, out CapturedFrame frame)
    {
        frame = default;

        var result = _duplication.AcquireNextFrame((uint)timeoutMs, out var frameInfo, out var resource);
        if (result.Failure)
        {
            // WAIT_TIMEOUT（0x887A0027）是正常现象：这一帧没变化。
            return false;
        }

        _frameAcquired = true;

        var pointer = frameInfo.PointerPosition;
        var cursor = new CaptureCursor(pointer.Visible, pointer.Position.X, pointer.Position.Y);
        var updated = frameInfo.LastPresentTime != 0 || frameInfo.AccumulatedFrames > 0;

        try
        {
            using (resource)
            {
                var texture = resource.QueryInterface<ID3D11Texture2D>();
                frame = new CapturedFrame(updated, texture, null, cursor);
            }
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"取桌面帧失败：{ex.Message}");
            Release();
            return false;
        }
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
        _dx.Dispose();
    }
}
