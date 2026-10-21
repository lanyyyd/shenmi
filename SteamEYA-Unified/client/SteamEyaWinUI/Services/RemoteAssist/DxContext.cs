using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SteamEyaWinUI.Services.RemoteAssist;

/// <summary>
/// 远程协助共用的 D3D11 上下文：采集（DXGI）、NV12 互转（D3D11 Video）和 Media Foundation 编解码
/// 全部绑在同一个 device 上，避免跨设备拷贝。
/// </summary>
internal sealed class DxContext : IDisposable
{
    private static readonly FeatureLevel[] Levels =
    [
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0,
    ];

    private DxContext(ID3D11Device device, ID3D11DeviceContext context,
        ID3D11VideoDevice videoDevice, ID3D11VideoContext videoContext)
    {
        Device = device;
        Context = context;
        VideoDevice = videoDevice;
        VideoContext = videoContext;
    }

    public ID3D11Device Device { get; }

    public ID3D11DeviceContext Context { get; }

    public ID3D11VideoDevice VideoDevice { get; }

    public ID3D11VideoContext VideoContext { get; }

    public static DxContext Create()
    {
        // BGRA：桌面复制要求；VideoSupport：NV12 转换要 ID3D11VideoDevice。
        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        var result = D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, Levels,
            out ID3D11Device device, out ID3D11DeviceContext context);

        if (result.Failure)
        {
            // 少数虚拟机上 VideoSupport 不受支持，退一档再试；VideoDevice 为空时由调用方报错。
            result = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                Levels, out device, out context);
        }

        result.CheckError();

        var videoDevice = device.QueryInterfaceOrNull<ID3D11VideoDevice>();
        var videoContext = context.QueryInterfaceOrNull<ID3D11VideoContext>();
        if (videoDevice is null || videoContext is null)
        {
            videoDevice?.Dispose();
            videoContext?.Dispose();
            context.Dispose();
            device.Dispose();
            throw new NotSupportedException("当前显卡驱动不支持 D3D11 视频处理（无法做 NV12 转换）。");
        }

        return new DxContext(device, context, videoDevice, videoContext);
    }

    /// <summary>创建一个可用于 VideoProcessor 的纹理（可作输入或输出视图）。</summary>
    public ID3D11Texture2D CreateTexture(Format format, int width, int height,
        BindFlags bindFlags, ResourceUsage usage = ResourceUsage.Default,
        CpuAccessFlags cpuFlags = CpuAccessFlags.None)
    {
        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = usage,
            BindFlags = bindFlags,
            CPUAccessFlags = cpuFlags,
            MiscFlags = ResourceOptionFlags.None,
        };
        return Device.CreateTexture2D(desc);
    }

    public void Dispose()
    {
        VideoContext.Dispose();
        VideoDevice.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}
