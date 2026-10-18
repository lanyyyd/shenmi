using System.Net.Http;
using System.Text;
using System.Text.Json;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 局域网点对点直传（客户端侧）。
///
/// 做法：把文件按字节区间切成若干段，**每段用一条独立的 HTTP 连接并发直传**到对端本机网关
/// （http://ip:port/api/lan/tr/*），对端按 offset 各自落盘，收齐后转正。这样单文件也能吃满多核 +
/// 多连接带宽，是局域网内最快的方式；文件间再叠加并发（由页面调度），实现"多文件并发 + 单文件多线程"。
///
/// 关键点：走原始字节流（不 multipart、不整文件入内存），所以**不限大小**；
/// 直连必须 <see cref="HttpClientHandler.UseProxy"/> = false，否则局域网 IP 可能被系统代理拦截。
/// </summary>
internal sealed class LanTransferService
{
    /// <summary>单段阈值：文件超过该值才切段（小文件单连接即可，避免无谓开销）。</summary>
    private const long SegmentThreshold = 8L * 1024 * 1024;

    /// <summary>单文件最多切多少段（即对该文件最多开多少条并发连接）。</summary>
    private const int MaxSegments = 8;

    private readonly HttpClient _http;

    public LanTransferService()
    {
        var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        // 大文件传输耗时不可预知，超时交给 CancellationToken 控制，不使用固定超时。
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static string Base(string ip, int port) => $"http://{ip}:{port}/api/lan/tr";

    /// <summary>发送单个文件：自动切段、并发直传、收尾转正。onBytes 报告本次新写入的字节数（后台线程回调）。</summary>
    public async Task<LanFileSendResult> SendFileAsync(string ip, int port, string filePath,
        string fromName, Action<long>? onBytes, CancellationToken ct)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(filePath);
            if (!info.Exists)
            {
                return new LanFileSendResult(false, "文件不存在");
            }
        }
        catch (Exception ex)
        {
            return new LanFileSendResult(false, ex.Message);
        }

        long size = info.Length;
        var tid = Guid.NewGuid().ToString("N");
        if (!await BeginAsync(ip, port, tid, info.Name, size, fromName, ct).ConfigureAwait(false))
        {
            return new LanFileSendResult(false, "对端未接受传输");
        }

        int segments = size <= SegmentThreshold
            ? 1
            : (int)Math.Min(MaxSegments, (size + SegmentThreshold - 1) / SegmentThreshold);
        long chunk = segments > 0 ? size / segments : size;

        var tasks = new List<Task>(segments);
        for (var i = 0; i < segments; i++)
        {
            long offset = chunk * i;
            long length = i == segments - 1 ? size - offset : chunk;
            if (length <= 0)
            {
                continue;
            }
            tasks.Add(SendSegmentAsync(ip, port, tid, filePath, offset, length, onBytes, ct));
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await AbortAsync(ip, port, tid).ConfigureAwait(false);
            return new LanFileSendResult(false, ct.IsCancellationRequested ? "已取消" : ex.Message);
        }

        var done = await FinishAsync(ip, port, tid, ct).ConfigureAwait(false);
        return new LanFileSendResult(done.Ok, done.Ok ? null : done.Msg ?? "收尾失败");
    }

    private async Task<bool> BeginAsync(string ip, int port, string transferId, string fileName,
        long size, string fromName, CancellationToken ct)
    {
        var dto = new LanTransferBeginRequest
        {
            TransferId = transferId,
            FileName = fileName,
            Size = size,
            FromName = fromName,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, Base(ip, port) + "/begin");
        req.Content = new StringContent(
            JsonSerializer.Serialize(dto, ChatJsonContext.Default.LanTransferBeginRequest),
            Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return false;
        }
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var result = await JsonSerializer.DeserializeAsync(
            stream, ChatJsonContext.Default.LanTransferBeginResponse, ct).ConfigureAwait(false);
        return result?.Ok == true;
    }

    private async Task SendSegmentAsync(string ip, int port, string transferId, string filePath,
        long offset, long length, Action<long>? onBytes, CancellationToken ct)
    {
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        fs.Seek(offset, SeekOrigin.Begin);

        using var req = new HttpRequestMessage(HttpMethod.Put,
            $"{Base(ip, port)}/{transferId}/data?offset={offset}");
        var content = new StreamContent(new BoundedReadStream(fs, length, onBytes));
        content.Headers.ContentLength = length;   // 让 HttpClient 发固定长度、不落成 chunked
        req.Content = content;

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    private async Task AbortAsync(string ip, int port, string transferId)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{Base(ip, port)}/{transferId}/abort");
            using var _ = await _http.SendAsync(req, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // 清理失败无所谓：对端 .part 不会转正
        }
    }

    private async Task<LanTransferFinishResponse> FinishAsync(string ip, int port, string transferId,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Base(ip, port)}/{transferId}/finish");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(
                   stream, ChatJsonContext.Default.LanTransferFinishResponse, ct).ConfigureAwait(false)
               ?? new LanTransferFinishResponse { Ok = false, Msg = "无响应" };
    }

    /// <summary>读取本机网关的接收页数据（进行中 + 已完成 + 接收目录）。</summary>
    public async Task<LanTransferInboxResponse> GetInboxAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(
                SessionState.LocalGatewayUrl.TrimEnd('/') + "/api/lan/tr/inbox", ct).ConfigureAwait(false);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync(
                       stream, ChatJsonContext.Default.LanTransferInboxResponse, ct).ConfigureAwait(false)
                   ?? new LanTransferInboxResponse();
        }
        catch
        {
            return new LanTransferInboxResponse();
        }
    }
}

/// <summary>单个文件的发送结果。</summary>
internal readonly record struct LanFileSendResult(bool Ok, string? Msg);

/// <summary>
/// 把底层流限制为只读 <c>length</c> 字节，并把已读字节数回报给进度回调。
/// HttpClient 顺序从本流读取，因此回调的累计值即"已发送字节数"，可驱动进度条。
/// </summary>
internal sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _length;
    private readonly Action<long>? _onBytes;
    private long _read;

    public BoundedReadStream(Stream inner, long length, Action<long>? onBytes)
    {
        _inner = inner;
        _length = length;
        _onBytes = onBytes;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var remaining = _length - _read;
        if (remaining <= 0)
        {
            return 0;
        }
        if (count > remaining)
        {
            count = (int)remaining;
        }
        var n = _inner.Read(buffer, offset, count);
        if (n > 0)
        {
            _read += n;
            _onBytes?.Invoke(n);
        }
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var remaining = _length - _read;
        if (remaining <= 0)
        {
            return 0;
        }
        var take = buffer.Length;
        if (take > remaining)
        {
            take = (int)remaining;
        }
        var n = await _inner.ReadAsync(buffer[..take], cancellationToken).ConfigureAwait(false);
        if (n > 0)
        {
            _read += n;
            _onBytes?.Invoke(n);
        }
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    // 底层 FileStream 由调用方（SendSegmentAsync）负责释放，这里不关闭，避免 using 双重释放。
    protected override void Dispose(bool disposing)
    {
    }
}
