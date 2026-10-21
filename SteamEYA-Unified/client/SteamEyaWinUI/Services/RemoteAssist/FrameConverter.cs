using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace SteamEyaWinUI.Services.RemoteAssist;

/// <summary>
/// 用 D3D11 VideoProcessor 在 GPU 上做 BGRA ↔ NV12 转换（含缩放）。
///
/// 为什么不用 shader 手写：色彩矩阵（BT.709 / full-range）搞错会整体偏色，
/// VideoProcessor 直接吃标准色彩空间描述，且转换、缩放一次 Blt 完成。
/// </summary>
internal sealed class FrameConverter : IDisposable
{
    private readonly DxContext _dx;
    private readonly bool _toNv12;
    private readonly Format _inFormat;
    private readonly Format _outFormat;

    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private ID3D11Texture2D? _output;
    private ID3D11Texture2D? _staging;
    private int _width;
    private int _height;
    private int _outRowPitch;

    private FrameConverter(DxContext dx, bool toNv12)
    {
        _dx = dx;
        _toNv12 = toNv12;
        _inFormat = toNv12 ? Format.B8G8R8A8_UNorm : Format.NV12;
        _outFormat = toNv12 ? Format.NV12 : Format.B8G8R8A8_UNorm;
    }

    /// <summary>采集方向：桌面 BGRA 纹理 → NV12 字节。</summary>
    public static FrameConverter CreateBgraToNv12(DxContext dx) => new(dx, true);

    /// <summary>显示方向：NV12 字节 → BGRA 字节（给 WriteableBitmap）。</summary>
    public static FrameConverter CreateNv12ToBgra(DxContext dx) => new(dx, false);

    /// <summary>把桌面纹理转成 NV12 并读回内存（行宽 = width，紧密排列）。</summary>
    public byte[] ConvertFromTexture(ID3D11Texture2D input, int width, int height)
    {
        Ensure(width, height);

        using var inputView = _dx.VideoDevice.CreateVideoProcessorInputView(
            input, _enumerator!, InputViewDesc());

        using var outputView = _dx.VideoDevice.CreateVideoProcessorOutputView(
            _output!, _enumerator!, OutputViewDesc());

        var stream = new VideoProcessorStream
        {
            Enable = true,
            OutputIndex = 0,
            InputFrameOrField = 0,
            PastFrames = 0,
            FutureFrames = 0,
            InputSurface = inputView,
        };

        _dx.VideoContext.VideoProcessorBlt(_processor!, outputView, 0, 1, [stream]);
        return ReadBack();
    }

    /// <summary>把 NV12 字节上传到 GPU、转成 BGRA 并读回内存。</summary>
    public byte[] ConvertFromNv12(byte[] nv12, int width, int height)
    {
        Ensure(width, height);

        // 输入侧：NV12 纹理，用 UpdateSubresource 上传（一张 1080p ≈ 3 MB，PCIe 无压力）。
        UploadNv12(nv12, width, height);

        using var inputView = _dx.VideoDevice.CreateVideoProcessorInputView(
            _inputNv12!, _enumerator!, InputViewDesc());

        using var outputView = _dx.VideoDevice.CreateVideoProcessorOutputView(
            _output!, _enumerator!, OutputViewDesc());

        var stream = new VideoProcessorStream
        {
            Enable = true,
            OutputIndex = 0,
            InputFrameOrField = 0,
            PastFrames = 0,
            FutureFrames = 0,
            InputSurface = inputView,
        };

        _dx.VideoContext.VideoProcessorBlt(_processor!, outputView, 0, 1, [stream]);
        return ReadBack();
    }

    private ID3D11Texture2D? _inputNv12;

    private void UploadNv12(byte[] nv12, int width, int height)
    {
        if (_inputNv12 is null)
        {
            _inputNv12 = _dx.CreateTexture(Format.NV12, width, height,
                BindFlags.RenderTarget | BindFlags.Decoder);
        }

        _dx.Context.UpdateSubresource(nv12, _inputNv12, 0, (uint)width, (uint)(width * height * 3 / 2), null);
    }

    private void Ensure(int width, int height)
    {
        if (_enumerator is not null && _width == width && _height == height)
        {
            return;
        }

        _processor?.Dispose();
        _enumerator?.Dispose();
        _output?.Dispose();
        _staging?.Dispose();
        _inputNv12?.Dispose();
        _inputNv12 = null;

        _width = width;
        _height = height;

        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational(30, 1),
            InputWidth = (uint)width,
            InputHeight = (uint)height,
            OutputFrameRate = new Rational(30, 1),
            OutputWidth = (uint)width,
            OutputHeight = (uint)height,
            Usage = VideoUsage.PlaybackNormal,
        };

        _enumerator = _dx.VideoDevice.CreateVideoProcessorEnumerator(content);
        _processor = _dx.VideoDevice.CreateVideoProcessor(_enumerator, 0);

        // 屏幕内容是 sRGB 全范围；NV12 用 BT.709 + 全范围（0-255），否则暗部会被压扁。
        var colorSpace = new VideoProcessorColorSpace
        {
            Usage = 0,
            RGB_Range = 0,
            YCbCr_Matrix = 1,
            YCbCr_xvYCC = 0,
            Nominal_Range = 1,
        };
        _dx.VideoContext.VideoProcessorSetStreamColorSpace(_processor, 0, colorSpace);
        _dx.VideoContext.VideoProcessorSetOutputColorSpace(_processor, colorSpace);
        _dx.VideoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);

        _output = _dx.CreateTexture(_outFormat, width, height, BindFlags.RenderTarget);
        _staging = _dx.CreateTexture(_outFormat, width, height, BindFlags.None,
            ResourceUsage.Staging, CpuAccessFlags.Read);

        _outRowPitch = _toNv12 ? width : width * 4;
    }

    private byte[] ReadBack()
    {
        _dx.Context.CopyResource(_staging!, _output!);

        var mapped = _dx.Context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var rows = _toNv12 ? _height * 3 / 2 : _height;
            var buffer = new byte[_outRowPitch * rows];
            var stride = (int)mapped.RowPitch;

            unsafe
            {
                var src = (byte*)mapped.DataPointer;
                fixed (byte* dst = buffer)
                {
                    for (var y = 0; y < rows; y++)
                    {
                        Buffer.MemoryCopy(src + (long)y * stride, dst + (long)y * _outRowPitch,
                            _outRowPitch, _outRowPitch);
                    }
                }
            }

            return buffer;
        }
        finally
        {
            _dx.Context.Unmap(_staging!, 0);
        }
    }

    private static VideoProcessorInputViewDescription InputViewDesc() => new()
    {
        FourCC = 0,
        ViewDimension = VideoProcessorInputViewDimension.Texture2D,
        Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
    };

    private static VideoProcessorOutputViewDescription OutputViewDesc() => new()
    {
        ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
        Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
    };

    public void Dispose()
    {
        _inputNv12?.Dispose();
        _staging?.Dispose();
        _output?.Dispose();
        _processor?.Dispose();
        _enumerator?.Dispose();
    }
}
