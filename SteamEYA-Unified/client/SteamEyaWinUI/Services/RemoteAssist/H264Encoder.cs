using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace SteamEyaWinUI.Services.RemoteAssist;

/// <summary>
/// Media Foundation H.264 编码器：优先硬件 MFT（NVENC / QSV / AMF），拿不到再退到微软的软件 MFT。
///
/// 输入是紧密排列的 NV12 系统内存（由 D3D11 VideoProcessor 在 GPU 上转好再读回），
/// 输出是 Annex-B 码流。刻意不用 IMFDXGIDeviceManager：硬件 MFT 普遍也吃系统内存输入，
/// 省掉设备管理器能少一大块 COM 互操作，而实测瓶颈根本不在这一次 3 MB 的拷贝上。
/// </summary>
internal sealed class H264Encoder : IDisposable
{
    private const uint MftEnumFlagSyncMft = 0x1;
    private const uint MftEnumFlagHardware = 0x4;
    private const uint MftEnumFlagSortAndFilter = 0x40;
    private const int MftOutputStreamProvidesSamples = 0x100;

    private readonly IMFTransform _transform;
    private readonly long _frameDuration;
    private readonly int _width;
    private readonly int _height;
    private bool _started;

    private H264Encoder(IMFTransform transform, int width, int height, int fps, bool hardware)
    {
        _transform = transform;
        _width = width;
        _height = height;
        _frameDuration = 10_000_000L / Math.Max(fps, 1);
        IsHardware = hardware;
    }

    public bool IsHardware { get; }

    public static H264Encoder Create(int width, int height, int fps, int bitrate)
    {
        // NV12 的宽高必须是偶数（4:2:0 采样），奇数尺寸先裁一像素。
        width &= ~1;
        height &= ~1;

        var input = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.NV12,
        };
        var output = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.H264,
        };

        var attempts = new (uint Flags, bool Hardware)[]
        {
            (MftEnumFlagHardware | MftEnumFlagSortAndFilter, true),
            (MftEnumFlagSyncMft | MftEnumFlagSortAndFilter, false),
        };

        foreach (var (flags, hardware) in attempts)
        {
            using var activates = MediaFactory.MFTEnumEx(
                TransformCategoryGuids.VideoEncoder, flags, input, output);

            foreach (var activate in activates)
            {
                using (activate)
                {
                    IMFTransform? transform = null;
                    try
                    {
                        transform = activate.ActivateObject<IMFTransform>();
                        if (TryConfigure(transform, width, height, fps, bitrate))
                        {
                            return new H264Encoder(transform, width, height, fps, hardware);
                        }
                    }
                    catch
                    {
                        // 该 MFT 不可用，换下一个
                    }

                    transform?.Dispose();
                }
            }
        }

        throw new NotSupportedException("本机没有可用的 H.264 编码器（硬件与软件 MFT 均不可用）。");
    }

    private static bool TryConfigure(IMFTransform transform, int width, int height, int fps, int bitrate)
    {
        using var outputType = MediaFactory.MFCreateMediaType();
        outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        outputType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
        outputType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)2);   // Progressive
        outputType.Set(MediaTypeAttributeKeys.Mpeg2Profile, (uint)100);  // High Profile
        outputType.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 0);
        outputType.Set(MediaTypeAttributeKeys.Compressed, true);
        outputType.Set(MediaTypeAttributeKeys.FixedSizeSamples, false);
        MediaFactory.MFSetAttributeSize(outputType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
        MediaFactory.MFSetAttributeRatio(outputType, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1);
        transform.SetOutputType(0, outputType, 0);

        using var inputType = MediaFactory.MFCreateMediaType();
        inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
        inputType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)2);
        inputType.Set(MediaTypeAttributeKeys.FixedSizeSamples, true);
        inputType.Set(MediaTypeAttributeKeys.DefaultStride, (uint)width);
        MediaFactory.MFSetAttributeSize(inputType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
        MediaFactory.MFSetAttributeRatio(inputType, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1);
        transform.SetInputType(0, inputType, 0);

        return true;
    }

    /// <summary>编码一帧 NV12，返回本帧产出的 Annex-B 数据（可能为空，编码器有内部延迟）。</summary>
    public byte[] Encode(byte[] nv12, long ptsMs, bool forceKeyFrame = false)
    {
        if (!_started)
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
            _started = true;
        }

        if (forceKeyFrame)
        {
            // 没有 ICodecAPI 时，flush 是让编码器重新吐 IDR 的标准手段。
            try
            {
                _transform.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero);
                _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
            }
            catch
            {
                // 不支持 flush 的 MFT 忽略即可，等自然 GOP 刷新
            }
        }

        using var buffer = MediaFactory.MFCreateMemoryBuffer(nv12.Length);
        buffer.Lock(out var destination, out _, out _);
        try
        {
            Marshal.Copy(nv12, 0, destination, nv12.Length);
        }
        finally
        {
            buffer.Unlock();
        }
        buffer.CurrentLength = nv12.Length;

        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = ptsMs * 10_000;
        sample.SampleDuration = _frameDuration;
        _transform.ProcessInput(0, sample, 0);

        return Drain();
    }

    private byte[] Drain()
    {
        var info = _transform.GetOutputStreamInfo(0);
        var providesSamples = (info.Flags & MftOutputStreamProvidesSamples) != 0;

        using var collected = new MemoryStream(128 * 1024);
        while (true)
        {
            IMFSample? provided = null;
            IMFMediaBuffer? providedBuffer = null;
            try
            {
                if (!providesSamples)
                {
                    provided = MediaFactory.MFCreateSample();
                    providedBuffer = MediaFactory.MFCreateMemoryBuffer(Math.Max(info.Size, _width * _height * 3 / 2));
                    provided.AddBuffer(providedBuffer);
                }

                var output = new OutputDataBuffer { StreamID = 0, Sample = provided };
                var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);

                if (result.Code == ResultCode.TransformStreamChange.Code)
                {
                    // 编码器换了输出类型（例如协商到别的 profile）：重新取一次再继续。
                    try
                    {
                        using var available = _transform.GetOutputAvailableType(0, 0);
                        if (available is not null)
                        {
                            _transform.SetOutputType(0, available, 0);
                        }
                    }
                    catch
                    {
                        // 拿不到就退出本次 drain
                    }
                    continue;
                }

                if (result.Code == ResultCode.TransformNeedMoreInput.Code)
                {
                    break;
                }

                if (result.Failure)
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
                    try
                    {
                        if (length > 0)
                        {
                            var chunk = new byte[length];
                            Marshal.Copy(pointer, chunk, 0, length);
                            collected.Write(chunk, 0, chunk.Length);
                        }
                    }
                    finally
                    {
                        contiguous.Unlock();
                    }
                }
            }
            finally
            {
                providedBuffer?.Dispose();
                provided?.Dispose();
            }
        }

        return collected.Length == 0 ? [] : collected.ToArray();
    }

    public void Dispose()
    {
        try
        {
            if (_started)
            {
                _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
                _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
            }
        }
        catch
        {
            // 关闭阶段的异常无意义
        }
        _transform.Dispose();
    }
}
