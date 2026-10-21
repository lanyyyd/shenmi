using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace SteamEyaWinUI.Services.RemoteAssist;

/// <summary>解码结果：一帧 NV12 与尺寸（尺寸可能随时变化，例如主控端换了分辨率）。</summary>
internal readonly record struct DecodedFrame(byte[] Nv12, int Width, int Height, long PtsMs);

/// <summary>
/// Media Foundation H.264 解码器（硬件优先）。输入 Annex-B 码流，输出紧密排列的 NV12。
///
/// 关键点：解码器在第一帧之前拿不到真实分辨率，会先回 MF_E_TRANSFORM_STREAM_CHANGE，
/// 必须重新取 GetOutputAvailableType 再 SetOutputType——漏了这步表现就是永远黑屏。
/// </summary>
internal sealed class H264Decoder : IDisposable
{
    private const uint MftEnumFlagSyncMft = 0x1;
    private const uint MftEnumFlagHardware = 0x4;
    private const uint MftEnumFlagSortAndFilter = 0x40;
    private const int MftOutputStreamProvidesSamples = 0x100;

    private readonly IMFTransform _transform;
    private int _width;
    private int _height;
    private bool _streaming;

    private H264Decoder(IMFTransform transform, bool hardware)
    {
        _transform = transform;
        IsHardware = hardware;
    }

    public bool IsHardware { get; }

    public int Width => _width;

    public int Height => _height;

    public static H264Decoder Create()
    {
        var input = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.H264,
        };
        var output = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.NV12,
        };

        var attempts = new (uint Flags, bool Hardware)[]
        {
            (MftEnumFlagHardware | MftEnumFlagSortAndFilter, true),
            (MftEnumFlagSyncMft | MftEnumFlagSortAndFilter, false),
        };

        foreach (var (flags, hardware) in attempts)
        {
            using var activates = MediaFactory.MFTEnumEx(
                TransformCategoryGuids.VideoDecoder, flags, input, output);

            foreach (var activate in activates)
            {
                using (activate)
                {
                    IMFTransform? transform = null;
                    try
                    {
                        transform = activate.ActivateObject<IMFTransform>();
                        using var inputType = MediaFactory.MFCreateMediaType();
                        inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                        inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                        inputType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)2);
                        transform.SetInputType(0, inputType, 0);
                        return new H264Decoder(transform, hardware);
                    }
                    catch
                    {
                        transform?.Dispose();
                    }
                }
            }
        }

        throw new NotSupportedException("本机没有可用的 H.264 解码器。");
    }

    /// <summary>喂一帧 Annex-B 码流，返回本次能解出的帧（可能为 0 帧，解码器有重排延迟）。</summary>
    public List<DecodedFrame> Decode(byte[] annexB, long ptsMs)
    {
        var frames = new List<DecodedFrame>(2);
        if (annexB.Length == 0)
        {
            return frames;
        }

        if (!_streaming)
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
            _streaming = true;
        }

        using var buffer = MediaFactory.MFCreateMemoryBuffer(annexB.Length);
        buffer.Lock(out var destination, out _, out _);
        try
        {
            Marshal.Copy(annexB, 0, destination, annexB.Length);
        }
        finally
        {
            buffer.Unlock();
        }
        buffer.CurrentLength = annexB.Length;

        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = ptsMs * 10_000;
        sample.SampleDuration = 333_333;
        _transform.ProcessInput(0, sample, 0);

        Drain(frames, ptsMs);
        return frames;
    }

    private void Drain(List<DecodedFrame> frames, long ptsMs)
    {
        var info = _transform.GetOutputStreamInfo(0);
        var providesSamples = (info.Flags & MftOutputStreamProvidesSamples) != 0;

        while (true)
        {
            IMFSample? provided = null;
            IMFMediaBuffer? providedBuffer = null;
            try
            {
                if (!providesSamples)
                {
                    provided = MediaFactory.MFCreateSample();
                    providedBuffer = MediaFactory.MFCreateMemoryBuffer(Math.Max(info.Size, 1920 * 1088 * 2));
                    provided.AddBuffer(providedBuffer);
                }

                var output = new OutputDataBuffer { StreamID = 0, Sample = provided };
                var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);

                if (result.Code == ResultCode.TransformStreamChange.Code)
                {
                    Renegotiate();
                    continue;
                }

                if (result.Code == ResultCode.TransformNeedMoreInput.Code || result.Failure)
                {
                    break;
                }

                var frameSample = output.Sample;
                if (frameSample is null)
                {
                    break;
                }

                using (frameSample)
                {
                    using var contiguous = frameSample.ConvertToContiguousBuffer();
                    contiguous.Lock(out var pointer, out _, out var length);
                    byte[] data;
                    try
                    {
                        if (length <= 0)
                        {
                            continue;
                        }
                        data = new byte[length];
                        Marshal.Copy(pointer, data, 0, length);
                    }
                    finally
                    {
                        contiguous.Unlock();
                    }

                    var expected = _width * _height * 3 / 2;
                    if (_width > 0 && _height > 0 && data.Length >= expected)
                    {
                        frames.Add(new DecodedFrame(data, _width, _height,
                            frameSample.SampleTime / 10_000));
                    }
                }
            }
            finally
            {
                providedBuffer?.Dispose();
                provided?.Dispose();
            }
        }
    }

    /// <summary>流格式变化：重新协商输出类型，并从媒体类型里读出真实分辨率。</summary>
    private void Renegotiate()
    {
        try
        {
            using var available = _transform.GetOutputAvailableType(0, 0);
            if (available is not null)
            {
                var size = available.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                _width = (int)(size >> 32);
                _height = (int)(size & 0xFFFF_FFFF);
                _transform.SetOutputType(0, available, 0);
            }
        }
        catch
        {
            // 协商失败就等下一次 STREAM_CHANGE
        }
    }

    public void Dispose()
    {
        try
        {
            if (_streaming)
            {
                _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
                _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
            }
        }
        catch
        {
            // 关闭阶段异常忽略
        }
        _transform.Dispose();
    }
}
