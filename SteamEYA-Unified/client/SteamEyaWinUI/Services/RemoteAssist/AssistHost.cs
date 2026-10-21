using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services.RemoteAssist;

/// <summary>
/// 被控端的协助服务：监听 TCP（默认 8900），接收邀请、等用户点"同意"、开一条会话。
///
/// 有意不做"免确认"：每个邀请都必须人工点同意，会话令牌一次性，断开即失效。
/// </summary>
internal sealed partial class AssistHost : IDisposable
{
    private const int DefaultPort = 8900;
    private static readonly TimeSpan InviteTimeout = TimeSpan.FromMinutes(5);

    private sealed class PendingInvite
    {
        public required TcpClient Tcp { get; init; }
        public required string RequestId { get; init; }
        public required string Uid { get; init; }
        public required string Nick { get; init; }
        public required string Ip { get; init; }
        public required DateTime ExpiresAtUtc { get; init; }
        public SemaphoreSlim WriteGate { get; } = new(1, 1);
        public CancellationTokenSource Cts { get; } = new();
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, PendingInvite> _pending = new(StringComparer.Ordinal);
    private TcpListener? _listener;
    private CancellationTokenSource? _acceptCts;
    private System.Threading.Timer? _expiryTimer;
    private AssistSession? _session;
    private string _nick = string.Empty;
    private string _uid = string.Empty;

    public AssistRequestStore Store { get; } = new();

    /// <summary>收到新邀请（后台线程）。UI 负责弹右下角窗口。</summary>
    public event Action<AssistRequestRecord>? InviteReceived;

    /// <summary>被控端会话已建立（后台线程）。UI 负责显示"正在被协助"悬浮条。</summary>
    public event Action<AssistSession>? SessionStarted;

    /// <summary>会话结束。</summary>
    public event Action<string>? SessionEnded;

    /// <summary>服务本身出错（端口占用、权限等），用于给用户提示。</summary>
    public event Action<string>? Faulted;

    public bool IsRunning => _listener is not null;

    public int Port { get; private set; }

    public bool InSession => _session is not null;

    /// <summary>启动监听。端口被占则自动往后探测，实际端口写进局域网广播，对端无需配置。</summary>
    public bool Start(string nick, string uid)
    {
        if (_listener is not null)
        {
            return true;
        }

        _nick = nick;
        _uid = uid;

        // 固定 8900：这个端口会随 UDP 广播发出去（见 server/app/lan.py），对端照着连，
        // 所以不能动态换端口，否则广播和实际监听会对不上。
        try
        {
            var listener = new TcpListener(IPAddress.Any, DefaultPort);
            listener.Start();
            _listener = listener;
            Port = DefaultPort;
        }
        catch (SocketException ex)
        {
            Faulted?.Invoke($"端口 {DefaultPort} 被占用，远程协助不可用（{ex.Message}）。");
            return false;
        }
        catch (Exception ex)
        {
            Faulted?.Invoke(ex.Message);
            return false;
        }

        _acceptCts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(_acceptCts.Token));
        _expiryTimer = new System.Threading.Timer(_ => ExpirePending(), null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        AppLog.Info($"远程协助服务已启动：端口 {Port}");
        return true;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"接受远程协助连接失败：{ex.Message}");
                await Task.Delay(200, ct).ConfigureAwait(false);
                continue;
            }

            _ = Task.Run(() => HandshakeAsync(tcp, ct));
        }
    }

    private async Task HandshakeAsync(TcpClient tcp, CancellationToken ct)
    {
        var writeGate = new SemaphoreSlim(1, 1);
        try
        {
            tcp.NoDelay = true;
            var stream = tcp.GetStream();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            var helloFrame = await AssistFraming.ReadAsync(stream, timeout.Token).ConfigureAwait(false);
            if (helloFrame is null || helloFrame.Value.Type != AssistMessageType.Hello)
            {
                tcp.Close();
                return;
            }

            var hello = AssistFraming.ReadJson(helloFrame.Value.Payload, AssistJsonContext.Default.AssistHello);
            if (hello is null || hello.V < 1)
            {
                tcp.Close();
                return;
            }

            var ack = new AssistHelloAck
            {
                Uid = _uid,
                Nick = _nick,
                Width = GetSystemMetrics(0),
                Height = GetSystemMetrics(1),
                DpiScale = 1f,
            };
            await AssistFraming.WriteJsonAsync(stream, AssistMessageType.HelloAck, ack,
                AssistJsonContext.Default.AssistHelloAck, writeGate, timeout.Token).ConfigureAwait(false);

            var next = await AssistFraming.ReadAsync(stream, timeout.Token).ConfigureAwait(false);
            if (next is null || next.Value.Type != AssistMessageType.Invite)
            {
                tcp.Close();
                return;
            }

            var invite = AssistFraming.ReadJson(next.Value.Payload, AssistJsonContext.Default.AssistInviteMessage);
            if (invite is null || string.IsNullOrWhiteSpace(invite.RequestId))
            {
                tcp.Close();
                return;
            }

            // 同一时间只允许一个会话；已经在被协助时直接回 Busy。
            if (InSession)
            {
                await AssistFraming.WriteJsonAsync(stream, AssistMessageType.Busy,
                    new AssistBusy { Reason = "in_session" }, AssistJsonContext.Default.AssistBusy,
                    writeGate, timeout.Token).ConfigureAwait(false);
                tcp.Close();
                return;
            }

            var ip = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? string.Empty;
            var nick = string.IsNullOrWhiteSpace(invite.FromNick) ? ip : invite.FromNick;
            var record = Store.Add(invite.RequestId, invite.FromUid, nick, ip, inbound: true);

            var pending = new PendingInvite
            {
                Tcp = tcp,
                RequestId = invite.RequestId,
                Uid = invite.FromUid,
                Nick = nick,
                Ip = ip,
                ExpiresAtUtc = DateTime.UtcNow.Add(InviteTimeout),
            };

            lock (_gate)
            {
                // 最多同时挂 8 个待处理邀请，多出来的直接拒绝，避免占满连接。
                if (_pending.Count >= 8)
                {
                    Store.UpdateStatus(invite.RequestId, AssistRequestStatus.Failed);
                    tcp.Close();
                    return;
                }
                _pending[invite.RequestId] = pending;
            }

            await AssistFraming.WriteJsonAsync(stream, AssistMessageType.InviteAck,
                new AssistInviteAck
                {
                    RequestId = invite.RequestId,
                    ExpiresAt = record.ExpiresAt,
                },
                AssistJsonContext.Default.AssistInviteAck, writeGate, timeout.Token).ConfigureAwait(false);

            InviteReceived?.Invoke(record);

            // 守候这条连接：主控端放弃（断开）时把申请标成已取消。
            _ = Task.Run(() => WatchPendingAsync(pending));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"远程协助握手失败：{ex.Message}");
            try
            {
                tcp.Close();
            }
            catch
            {
                // 忽略
            }
        }
        finally
        {
            writeGate.Dispose();
        }
    }

    private async Task WatchPendingAsync(PendingInvite pending)
    {
        try
        {
            var stream = pending.Tcp.GetStream();
            while (!pending.Cts.IsCancellationRequested)
            {
                var frame = await AssistFraming.ReadAsync(stream, pending.Cts.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    break;
                }

                if (frame.Value.Type == AssistMessageType.Bye)
                {
                    break;
                }
            }
        }
        catch
        {
            // 连接断开是正常路径之一
        }

        lock (_gate)
        {
            if (_pending.TryGetValue(pending.RequestId, out var current) && ReferenceEquals(current, pending))
            {
                _pending.Remove(pending.RequestId);
                if (Store.Find(pending.RequestId)?.Status == AssistRequestStatus.Pending)
                {
                    Store.UpdateStatus(pending.RequestId, AssistRequestStatus.Cancelled);
                }
            }
        }
    }

    /// <summary>用户点"同意"：回令牌并建立会话。</summary>
    public async Task<bool> ApproveAsync(string requestId)
    {
        PendingInvite? pending;
        lock (_gate)
        {
            _pending.TryGetValue(requestId, out pending);
        }

        var record = Store.Find(requestId);
        if (pending is null || record is null || record.Status != AssistRequestStatus.Pending)
        {
            return false;
        }

        if (InSession)
        {
            await RejectAsync(requestId, "in_session").ConfigureAwait(false);
            return false;
        }

        var token = Guid.NewGuid().ToString("N");

        // 先建会话（采集/编码都在这儿初始化），成功了才回"同意"。
        // 反过来的话，采集一失败，对端已经收到 Accepted，只能一直卡在"连接中"黑屏。
        var session = CreateSession(pending, token);
        if (session is null)
        {
            await SendResultAsync(pending, new AssistInviteResult
            {
                RequestId = requestId,
                Accepted = false,
                Reason = "capture_failed",
            }).ConfigureAwait(false);
            lock (_gate)
            {
                _pending.Remove(requestId);
            }
            ClosePending(pending);
            Store.UpdateStatus(requestId, AssistRequestStatus.Failed);
            return false;
        }

        try
        {
            var sent = await SendResultAsync(pending, new AssistInviteResult
            {
                RequestId = requestId,
                Accepted = true,
                Token = token,
            }).ConfigureAwait(false);
            if (!sent)
            {
                session.Dispose();
                lock (_gate)
                {
                    _pending.Remove(requestId);
                }
                ClosePending(pending);
                Store.UpdateStatus(requestId, AssistRequestStatus.Failed);
                return false;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"回执同意失败：{ex.Message}");
            session.Dispose();
            ClosePending(pending);
            Store.UpdateStatus(requestId, AssistRequestStatus.Failed);
            return false;
        }

        lock (_gate)
        {
            _pending.Remove(requestId);
        }

        session.PeerNick = pending.Nick;
        _session = session;
        session.Closed += reason =>
        {
            _session = null;
            SessionEnded?.Invoke(reason);
        };
        session.Start();

        Store.UpdateStatus(requestId, AssistRequestStatus.Accepted);
        SessionStarted?.Invoke(session);
        AppLog.Info($"远程协助会话已建立：{pending.Nick}（{pending.Ip}），硬件编码={session.HardwareEncode}");
        return true;
    }

    private AssistSession? CreateSession(PendingInvite pending, string token)
    {
        try
        {
            return AssistSession.CreateHost(pending.Tcp, token);
        }
        catch (Exception ex)
        {
            AppLog.Error("创建被控会话失败。", ex);
            Faulted?.Invoke($"无法启动屏幕采集：{ex.Message}");
            return null;
        }
    }

    /// <summary>给对端回一条 InviteResult；连接已断时返回 false（不抛）。</summary>
    private static async Task<bool> SendResultAsync(PendingInvite pending, AssistInviteResult result)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await AssistFraming.WriteJsonAsync(pending.Tcp.GetStream(), AssistMessageType.InviteResult,
                result, AssistJsonContext.Default.AssistInviteResult, pending.WriteGate, timeout.Token)
                .ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task RejectAsync(string requestId, string reason = "rejected")
    {
        PendingInvite? pending;
        lock (_gate)
        {
            _pending.TryGetValue(requestId, out pending);
            _pending.Remove(requestId);
        }

        if (pending is not null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await AssistFraming.WriteJsonAsync(pending.Tcp.GetStream(), AssistMessageType.InviteResult,
                    new AssistInviteResult { RequestId = requestId, Accepted = false, Reason = reason },
                    AssistJsonContext.Default.AssistInviteResult, pending.WriteGate, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch
            {
                // 对端可能已经走了
            }
            ClosePending(pending);
        }

        Store.UpdateStatus(requestId, reason == "timeout"
            ? AssistRequestStatus.Expired
            : AssistRequestStatus.Rejected);
    }

    /// <summary>每秒扫一次：超过 5 分钟没处理的邀请自动失效。</summary>
    private void ExpirePending()
    {
        List<PendingInvite> overdue;
        lock (_gate)
        {
            overdue = _pending.Values.Where(p => p.ExpiresAtUtc <= DateTime.UtcNow).ToList();
            foreach (var item in overdue)
            {
                _pending.Remove(item.RequestId);
            }
        }

        foreach (var item in overdue)
        {
            _ = NotifyTimeoutAsync(item);
        }

        Store.ExpireOverdue();
    }

    private async Task NotifyTimeoutAsync(PendingInvite pending)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await AssistFraming.WriteJsonAsync(pending.Tcp.GetStream(), AssistMessageType.InviteResult,
                new AssistInviteResult
                {
                    RequestId = pending.RequestId,
                    Accepted = false,
                    Reason = "timeout",
                },
                AssistJsonContext.Default.AssistInviteResult, pending.WriteGate, timeout.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            // 对端可能已经断开
        }
        ClosePending(pending);
    }

    private void ClosePending(PendingInvite pending)
    {
        try
        {
            pending.Cts.Cancel();
            pending.Tcp.Close();
        }
        catch
        {
            // 忽略
        }
        pending.WriteGate.Dispose();
    }

    public void Stop()
    {
        try
        {
            _acceptCts?.Cancel();
            _listener?.Stop();
        }
        catch
        {
            // 忽略
        }
        _listener = null;

        List<PendingInvite> all;
        lock (_gate)
        {
            all = [.. _pending.Values];
            _pending.Clear();
        }
        foreach (var item in all)
        {
            ClosePending(item);
        }

        _session?.Close("host_stopped");
        _session = null;
    }

    public void Dispose()
    {
        Stop();
        _expiryTimer?.Dispose();
        _acceptCts?.Dispose();
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);
}
