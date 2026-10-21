using System.Diagnostics;
using System.Net.Sockets;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services.RemoteAssist;

internal enum AssistRole
{
    /// <summary>被控端：采集屏幕、编码、注入键鼠。</summary>
    Host,
    /// <summary>主控端：解码、显示、发送键鼠事件。</summary>
    Controller,
}

/// <summary>
/// 一条已经建立的协助会话（双方共用）。被控端跑"采集→编码→发送"，主控端跑"接收→解码→上屏"。
///
/// 网络上只有一条 TCP：视频、输入、心跳都在上面多路复用，既省一次打洞/防火墙放行，
/// 也保证"输入事件一定发生在对应画面之后"。
/// </summary>
internal sealed class AssistSession : IDisposable
{
    private const int TargetFps = 30;
    private const int Bitrate = 8_000_000;
    private const int HeartbeatMs = 3_000;
    private const int IdleTimeoutMs = 20_000;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly string _token;

    private readonly DxContext _dx;
    private readonly DesktopCapture? _capture;
    private readonly FrameConverter _converter;
    private readonly H264Encoder? _encoder;
    private readonly H264Decoder? _decoder;

    private long _lastReceiveTicks = Environment.TickCount64;
    private int _lastCursorSent = -1;
    private volatile bool _disposed;

    private AssistSession(TcpClient tcp, AssistRole role, string token, DxContext dx)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _tcp.NoDelay = true;
        Role = role;
        _token = token;
        _dx = dx;

        if (role == AssistRole.Host)
        {
            _capture = DesktopCapture.Create(dx);
            _converter = FrameConverter.CreateBgraToNv12(dx);
            _encoder = H264Encoder.Create(_capture.Width, _capture.Height, TargetFps, Bitrate);
        }
        else
        {
            _converter = FrameConverter.CreateNv12ToBgra(dx);
            _decoder = H264Decoder.Create();
        }
    }

    public AssistRole Role { get; }

    /// <summary>对端昵称（被控端用于"正在被某某协助"提示）。</summary>
    public string PeerNick { get; set; } = string.Empty;

    public string Token => _token;

    public int ScreenWidth => _capture?.Width ?? _frameWidth;

    public int ScreenHeight => _capture?.Height ?? _frameHeight;

    public bool HardwareEncode => _encoder?.IsHardware ?? false;

    public bool HardwareDecode => _decoder?.IsHardware ?? false;

    /// <summary>主控端：解出一帧 BGRA（宽×高×4），交给 UI 画。</summary>
    public event Action<byte[], int, int>? FrameReady;

    /// <summary>主控端：远端光标位置（归一化）。</summary>
    public event Action<bool, float, float>? CursorMoved;

    /// <summary>任一端：会话结束（reason 用于提示）。</summary>
    public event Action<string>? Closed;

    private int _frameWidth;
    private int _frameHeight;

    public static AssistSession CreateHost(TcpClient tcp, string token, DxContext dx)
        => new(tcp, AssistRole.Host, token, dx);

    public static AssistSession CreateController(TcpClient tcp, string token, DxContext dx)
        => new(tcp, AssistRole.Controller, token, dx);

    public void Start()
    {
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(HeartbeatLoopAsync);
        if (Role == AssistRole.Host)
        {
            _ = Task.Run(CaptureLoopAsync);
        }
    }

    // ---------------- 被控端：采集 → 编码 → 发送 ----------------
    private async Task CaptureLoopAsync()
    {
        var ct = _cts.Token;
        var frameInterval = 1000.0 / TargetFps;
        var clock = Stopwatch.StartNew();
        var forceKey = true;   // 首帧必须是 IDR，否则对端解不出来

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var started = clock.Elapsed.TotalMilliseconds;

                if (!_capture!.TryAcquire(16, out var texture, out var desktopUpdated, out var cursor))
                {
                    await Task.Delay(4, ct).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    var cursorX = cursor.Visible && _capture.Width > 0
                        ? cursor.X / _capture.Width
                        : 0f;
                    var cursorY = cursor.Visible && _capture.Height > 0
                        ? cursor.Y / _capture.Height
                        : 0f;
                    var cursorKey = cursor.Visible
                        ? HashCode.Combine((int)cursor.X, (int)cursor.Y)
                        : -1;

                    if (desktopUpdated || forceKey)
                    {
                        var nv12 = _converter.ConvertFromTexture(texture!, _capture.Width, _capture.Height);
                        var encoded = _encoder!.Encode(nv12, (long)clock.Elapsed.TotalMilliseconds, forceKey);
                        if (encoded.Length > 0)
                        {
                            await SendVideoAsync(encoded, (long)clock.Elapsed.TotalMilliseconds,
                                forceKey, cursor.Visible, cursorX, cursorY, ct).ConfigureAwait(false);
                            forceKey = false;
                            _lastCursorSent = cursorKey;
                        }
                    }
                    else if (cursorKey != _lastCursorSent)
                    {
                        // 画面没变但鼠标动了：只发 32 字节帧头，对端据此移动光标。
                        await SendVideoAsync([], clock.ElapsedMilliseconds, false,
                            cursor.Visible, cursorX, cursorY, ct).ConfigureAwait(false);
                        _lastCursorSent = cursorKey;
                    }
                }
                finally
                {
                    _capture.Release();
                }

                var elapsed = clock.Elapsed.TotalMilliseconds - started;
                var wait = frameInterval - elapsed;
                if (wait > 1)
                {
                    await Task.Delay((int)wait, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        catch (Exception ex)
        {
            AppLog.Warn($"远程协助采集循环结束：{ex.Message}");
            Close("capture_error");
        }
    }

    private async Task SendVideoAsync(byte[] data, long ptsMs, bool keyFrame,
        bool cursorVisible, float cursorX, float cursorY, CancellationToken ct)
    {
        var header = AssistVideoHeader.Build(ptsMs, keyFrame, _capture!.Width, _capture.Height,
            data.Length, cursorVisible, cursorX, cursorY);
        var payload = new byte[header.Length + data.Length];
        header.CopyTo(payload, 0);
        data.CopyTo(payload, header.Length);
        await AssistFraming.WriteAsync(_stream, AssistMessageType.Video, 0, payload, _writeGate, ct)
            .ConfigureAwait(false);
    }

    // ---------------- 接收循环 ----------------
    private async Task ReadLoopAsync()
    {
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await AssistFraming.ReadAsync(_stream, ct).ConfigureAwait(false);
                if (frame is null)
                {
                    break;
                }

                _lastReceiveTicks = Environment.TickCount64;
                var value = frame.Value;

                switch (value.Type)
                {
                    case AssistMessageType.Video:
                        HandleVideo(value.Payload);
                        break;

                    case AssistMessageType.Input:
                        HandleInput(value.Payload);
                        break;

                    case AssistMessageType.KeyframeRequest:
                        // 目前编码器只能靠 flush 强制 IDR，代价是丢一帧；不做额外处理，
                        // 等自然 GOP 刷新（2 秒内）即可。
                        break;

                    case AssistMessageType.Ping:
                        await AssistFraming.WriteAsync(_stream, AssistMessageType.Pong, 0, ReadOnlyMemory<byte>.Empty,
                            _writeGate, ct).ConfigureAwait(false);
                        break;

                    case AssistMessageType.Pong:
                    case AssistMessageType.Bye:
                        if (value.Type == AssistMessageType.Bye)
                        {
                            Close("peer_bye");
                            return;
                        }
                        break;
                }
            }

            Close("stream_ended");
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        catch (Exception ex)
        {
            AppLog.Warn($"远程协助接收循环异常：{ex.Message}");
            Close("network_error");
        }
    }

    private void HandleVideo(byte[] payload)
    {
        if (Role != AssistRole.Controller || payload.Length < AssistVideoHeader.Size)
        {
            return;
        }

        var header = AssistVideoHeader.Parse(payload);
        if (header.Width > 0 && header.Height > 0)
        {
            _frameWidth = header.Width;
            _frameHeight = header.Height;
        }

        if (header.CursorVisible)
        {
            CursorMoved?.Invoke(true, header.CursorX, header.CursorY);
        }

        if (header.DataLength <= 0 || payload.Length < AssistVideoHeader.Size + header.DataLength)
        {
            return;   // 纯光标更新
        }

        var bitstream = new byte[header.DataLength];
        Buffer.BlockCopy(payload, AssistVideoHeader.Size, bitstream, 0, header.DataLength);

        List<DecodedFrame> frames;
        try
        {
            frames = _decoder!.Decode(bitstream, header.PtsMs);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"解码失败：{ex.Message}");
            return;
        }

        foreach (var frame in frames)
        {
            _frameWidth = frame.Width;
            _frameHeight = frame.Height;
            var bgra = _converter.ConvertFromNv12(frame.Nv12, frame.Width, frame.Height);
            FrameReady?.Invoke(bgra, frame.Width, frame.Height);
        }
    }

    private void HandleInput(byte[] payload)
    {
        if (Role != AssistRole.Host)
        {
            return;
        }

        var input = AssistInputEvent.Parse(payload);
        switch (input.Kind)
        {
            case AssistInputEvent.KindMouseMove:
                InputInjector.MoveMouse(_capture!.DesktopX, _capture.DesktopY,
                    _capture.Width, _capture.Height, input.X, input.Y);
                break;

            case AssistInputEvent.KindMouseButton:
                InputInjector.MouseButton(input.Button, input.Down == 1);
                break;

            case AssistInputEvent.KindMouseWheel:
                InputInjector.MouseWheel(input.Delta);
                break;

            case AssistInputEvent.KindKey:
                InputInjector.Key(input.Button, input.Down == 1, (input.Flags & 1) != 0);
                break;
        }
    }

    // ---------------- 发送输入 / 心跳 ----------------
    public ValueTask SendInputAsync(AssistInputEvent input)
    {
        if (_cts.IsCancellationRequested)
        {
            return ValueTask.CompletedTask;
        }
        return AssistFraming.WriteAsync(_stream, AssistMessageType.Input, 0,
            input.ToBytes(), _writeGate, _cts.Token);
    }

    private async Task HeartbeatLoopAsync()
    {
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatMs, ct).ConfigureAwait(false);

                if (Environment.TickCount64 - _lastReceiveTicks > IdleTimeoutMs)
                {
                    Close("timeout");
                    return;
                }

                await AssistFraming.WriteAsync(_stream, AssistMessageType.Ping, 0, ReadOnlyMemory<byte>.Empty,
                    _writeGate, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        catch (Exception ex)
        {
            AppLog.Warn($"远程协助心跳结束：{ex.Message}");
            Close("heartbeat_error");
        }
    }

    public void Close(string reason)
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        try
        {
            _cts.Cancel();
        }
        catch
        {
            // 忽略
        }

        try
        {
            _tcp.Close();
        }
        catch
        {
            // 忽略
        }

        Closed?.Invoke(reason);
    }

    public void Dispose()
    {
        Close("disposed");
        _capture?.Dispose();
        _encoder?.Dispose();
        _decoder?.Dispose();
        _converter.Dispose();
        _dx.Dispose();
        _writeGate.Dispose();
        _cts.Dispose();
    }
}
