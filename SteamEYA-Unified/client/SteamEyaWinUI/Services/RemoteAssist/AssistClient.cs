using System.Net.Sockets;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services.RemoteAssist;

internal enum AssistConnectState
{
    Connecting,
    WaitingApproval,
    Accepted,
    Rejected,
    Timeout,
    Busy,
    Failed,
}

internal readonly record struct AssistConnectResult(AssistConnectState State, AssistSession? Session, string? Message);

/// <summary>
/// 主控端：连上对端的协助端口，发邀请，等对方点同意。
///
/// 这条 TCP 在等待期间保持打开——对方点"同意"的瞬间就能直接开流，不用二次连接（少一次握手、
/// 少一次防火墙交互，也避免"同意后又连不上"的尴尬）。
/// </summary>
internal sealed class AssistClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(8);

    public AssistClient(string selfUid, string selfNick)
    {
        _selfUid = selfUid;
        _selfNick = selfNick;
    }

    private readonly string _selfUid;
    private readonly string _selfNick;

    /// <summary>状态变化（后台线程），用于把"等待对方同意…"显示到界面上。</summary>
    public event Action<AssistConnectState, string>? StateChanged;

    public async Task<AssistConnectResult> ConnectAsync(string ip, int port, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        var peerNick = ip;
        try
        {
            StateChanged?.Invoke(AssistConnectState.Connecting, ip);

            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(ConnectTimeout);
                try
                {
                    await tcp.ConnectAsync(ip, port, connectCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    tcp.Close();
                    return new AssistConnectResult(AssistConnectState.Failed, null, "对方未开启远程协助或防火墙拦截");
                }
            }

            var stream = tcp.GetStream();
            var writeGate = new SemaphoreSlim(1, 1);

            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshake.CancelAfter(HandshakeTimeout);

            await AssistFraming.WriteJsonAsync(stream, AssistMessageType.Hello,
                new AssistHello { V = 1, Uid = _selfUid, Nick = _selfNick, AssistPort = port },
                AssistJsonContext.Default.AssistHello, writeGate, handshake.Token).ConfigureAwait(false);

            var helloAckFrame = await AssistFraming.ReadAsync(stream, handshake.Token).ConfigureAwait(false);
            if (helloAckFrame is null || helloAckFrame.Value.Type != AssistMessageType.HelloAck)
            {
                tcp.Close();
                return new AssistConnectResult(AssistConnectState.Failed, null, "对方响应异常（协议不匹配）");
            }

            var helloAck = AssistFraming.ReadJson(helloAckFrame.Value.Payload,
                AssistJsonContext.Default.AssistHelloAck);
            peerNick = string.IsNullOrWhiteSpace(helloAck?.Nick) ? ip : helloAck!.Nick;

            var requestId = Guid.NewGuid().ToString("N");
            await AssistFraming.WriteJsonAsync(stream, AssistMessageType.Invite,
                new AssistInviteMessage
                {
                    RequestId = requestId,
                    FromUid = _selfUid,
                    FromNick = _selfNick,
                    Mode = "control",
                },
                AssistJsonContext.Default.AssistInviteMessage, writeGate, handshake.Token).ConfigureAwait(false);

            var ackFrame = await AssistFraming.ReadAsync(stream, handshake.Token).ConfigureAwait(false);
            if (ackFrame is null)
            {
                tcp.Close();
                return new AssistConnectResult(AssistConnectState.Failed, null, "连接被对方关闭");
            }

            if (ackFrame.Value.Type == AssistMessageType.Busy)
            {
                tcp.Close();
                return new AssistConnectResult(AssistConnectState.Busy, null, "对方正在进行远程协助");
            }

            if (ackFrame.Value.Type != AssistMessageType.InviteAck)
            {
                tcp.Close();
                return new AssistConnectResult(AssistConnectState.Failed, null, "对方拒绝了本次连接");
            }

            StateChanged?.Invoke(AssistConnectState.WaitingApproval, _selfNick);

            // 等对方处理：最长 5 分钟（与对端的超时保持一致，再多留 10 秒余量等回执）。
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waitCts.CancelAfter(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(10));

            AssistFrame? resultFrame;
            try
            {
                resultFrame = await AssistFraming.ReadAsync(stream, waitCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                tcp.Close();
                return new AssistConnectResult(AssistConnectState.Timeout, null, "对方未在 5 分钟内处理，请求已失效");
            }

            if (resultFrame is null || resultFrame.Value.Type != AssistMessageType.InviteResult)
            {
                tcp.Close();
                return new AssistConnectResult(AssistConnectState.Failed, null, "对方已断开连接");
            }

            var result = AssistFraming.ReadJson(resultFrame.Value.Payload,
                AssistJsonContext.Default.AssistInviteResult);
            if (result is null || !result.Accepted)
            {
                tcp.Close();
                var state = result?.Reason == "timeout" ? AssistConnectState.Timeout : AssistConnectState.Rejected;
                var message = result?.Reason switch
                {
                    "timeout" => "对方未在 5 分钟内处理，请求已失效",
                    "in_session" => "对方正在进行远程协助",
                    _ => "对方拒绝了协助请求",
                };
                StateChanged?.Invoke(state, message);
                return new AssistConnectResult(state, null, message);
            }

            // 握手完成后 writeGate 交给会话接管（会话内部自建写锁），这里不再持有。
            writeGate.Dispose();

            DxContext dx;
            try
            {
                dx = DxContext.Create();
            }
            catch (Exception ex)
            {
                tcp.Close();
                return new AssistConnectResult(AssistConnectState.Failed, null, $"本机无法解码画面：{ex.Message}");
            }

            var session = AssistSession.CreateController(tcp, result.Token ?? string.Empty, dx);
            session.PeerNick = peerNick;
            StateChanged?.Invoke(AssistConnectState.Accepted, _selfNick);
            return new AssistConnectResult(AssistConnectState.Accepted, session, null);
        }
        catch (OperationCanceledException)
        {
            SafeClose(tcp);
            return new AssistConnectResult(AssistConnectState.Failed, null, "已取消");
        }
        catch (Exception ex)
        {
            SafeClose(tcp);
            return new AssistConnectResult(AssistConnectState.Failed, null, ex.Message);
        }
    }

    private static void SafeClose(TcpClient tcp)
    {
        try
        {
            tcp.Close();
        }
        catch
        {
            // 忽略
        }
    }
}
