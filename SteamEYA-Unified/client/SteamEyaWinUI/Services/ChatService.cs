using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 原生聊天室客户端：统一网关的 REST（用户/历史/上传/已读）+ WebSocket（实时收发/撤回）。
/// 取代原先内嵌 WebView2 的方案，避免部分 Win10 上 WebView2 初始化卡死。
/// 在线走 <see cref="SessionState.OnlineBaseUrl"/>，离线走本机网关，鉴权统一用 JWT（WS 走 ?jwt=）。
/// </summary>
internal sealed class ChatService : IAsyncDisposable
{
    private const int PingIntervalMs = 25000;
    private const int MaxBackoffMs = 15000;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>收到一条新消息（含自己发送后服务器回显）。</summary>
    public event Action<ChatMessageDto>? MessageReceived;

    /// <summary>在线列表可能变化（收到 online 广播 / ready）。</summary>
    public event Action? UsersChanged;

    /// <summary>收到撤回事件（消息 id）。</summary>
    public event Action<long>? MessageRecalled;

    /// <summary>连接状态变化。</summary>
    public event Action<bool>? ConnectionChanged;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    private static string BaseUrl => SessionState.BaseUrl.TrimEnd('/');

    private static string? Token => SessionState.Token;

    private static string HttpUrl(string path) => BaseUrl + path;

    private string WsUrl()
    {
        var u = new Uri(SessionState.BaseUrl);
        var scheme = u.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        return $"{scheme}://{u.Host}:{u.Port}/ws?jwt={Uri.EscapeDataString(Token ?? string.Empty)}&deviceId=winui";
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, HttpUrl(path));
        if (!string.IsNullOrWhiteSpace(Token))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        }
        return req;
    }

    // ---------------- REST ----------------
    public async Task<ChatUserDto?> GetMeAsync(CancellationToken ct = default)
    {
        using var req = NewRequest(HttpMethod.Get, "/api/me");
        using var resp = await _http.SendAsync(req, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync(stream, ChatJsonContext.Default.ChatMeResponse, ct);
        return dto?.User;
    }

    public async Task<ChatUserDto[]> GetUsersAsync(CancellationToken ct = default)
    {
        using var req = NewRequest(HttpMethod.Get, "/api/users");
        using var resp = await _http.SendAsync(req, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync(stream, ChatJsonContext.Default.ChatUsersResponse, ct);
        return dto?.Users ?? [];
    }

    public async Task<ChatHistoryResponse> GetHistoryAsync(string room, int limit = 80, long before = 0, CancellationToken ct = default)
    {
        var path = $"/api/history?room={Uri.EscapeDataString(room)}&limit={limit}";
        if (before > 0)
        {
            path += $"&before={before}";
        }
        using var req = NewRequest(HttpMethod.Get, path);
        using var resp = await _http.SendAsync(req, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(stream, ChatJsonContext.Default.ChatHistoryResponse, ct)
               ?? new ChatHistoryResponse();
    }

    public async Task<ChatUploadResultDto> UploadAsync(string filePath, CancellationToken ct = default)
    {
        using var req = NewRequest(HttpMethod.Post, "/api/upload");
        using var form = new MultipartFormDataContent();
        var bytes = await File.ReadAllBytesAsync(filePath, ct);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", Path.GetFileName(filePath));
        req.Content = form;
        using var resp = await _http.SendAsync(req, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(stream, ChatJsonContext.Default.ChatUploadResultDto, ct)
               ?? new ChatUploadResultDto();
    }

    public async Task MarkReadAsync(string room, CancellationToken ct = default)
    {
        using var req = NewRequest(HttpMethod.Post, "/api/read");
        var body = JsonSerializer.Serialize(new ChatReadRequest { Room = room }, ChatJsonContext.Default.ChatReadRequest);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct);
    }

    public async Task<ChatUnreadResponse> GetUnreadAsync(CancellationToken ct = default)
    {
        using var req = NewRequest(HttpMethod.Get, "/api/unread");
        using var resp = await _http.SendAsync(req, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(stream, ChatJsonContext.Default.ChatUnreadResponse, ct)
               ?? new ChatUnreadResponse();
    }

    // ---------------- WebSocket ----------------
    public void Start()
    {
        if (_cts is not null)
        {
            return;
        }
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        _cts = null;
        if (cts is not null)
        {
            await cts.CancelAsync();
            cts.Dispose();
        }
        var ws = _ws;
        if (ws is { State: WebSocketState.Open })
        {
            try
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch
            {
                // 忽略关闭异常
            }
        }
        ws?.Dispose();
        _ws = null;
        if (_loop is not null)
        {
            try { await _loop; } catch { /* 忽略 */ }
            _loop = null;
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = 1000;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ws = new ClientWebSocket();
                _ws = ws;
                await ws.ConnectAsync(new Uri(WsUrl()), ct);
                backoff = 1000;
                ConnectionChanged?.Invoke(true);
                await ReceiveLoopAsync(ws, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // 连接/接收异常：走重连
            }
            finally
            {
                ConnectionChanged?.Invoke(false);
                _ws?.Dispose();
                _ws = null;
            }
            if (ct.IsCancellationRequested)
            {
                break;
            }
            try
            {
                await Task.Delay(backoff, ct);
            }
            catch
            {
                break;
            }
            backoff = Math.Min(backoff * 2, MaxBackoffMs);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            string? json;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(PingIntervalMs);
                try
                {
                    json = await ReceiveMessageAsync(ws, timeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // 空闲超时：发心跳，维持 online.last_seen（左侧“上次登录时间”依赖它）
                    await SendRawAsync(JsonSerializer.Serialize(new ChatPingDto(), ChatJsonContext.Default.ChatPingDto), ct);
                    continue;
                }
            }
            if (json is null)
            {
                return;
            }
            Dispatch(json);
        }
    }

    private static async Task<string?> ReceiveMessageAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16384];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private void Dispatch(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
            switch (type)
            {
                case "chat":
                    var msg = doc.RootElement.Deserialize(ChatJsonContext.Default.ChatMessageDto);
                    if (msg is not null)
                    {
                        MessageReceived?.Invoke(msg);
                    }
                    break;
                case "online":
                case "ready":
                    UsersChanged?.Invoke();
                    break;
                case "recall":
                    if (doc.RootElement.TryGetProperty("id", out var rid) && rid.TryGetInt64(out var id))
                    {
                        MessageRecalled?.Invoke(id);
                    }
                    break;
            }
        }
        catch
        {
            // 单条消息解析失败不影响后续接收
        }
    }

    private async Task SendRawAsync(string json, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is not { State: WebSocketState.Open })
        {
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(ct);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        catch
        {
            // 发送失败由接收循环触发重连
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public Task SendChatAsync(string kind, string content, ChatFileDto? file = null, int? to = null, CancellationToken ct = default)
    {
        var dto = new ChatSendDto { MsgType = kind, Content = content, File = file, To = to };
        return SendRawAsync(JsonSerializer.Serialize(dto, ChatJsonContext.Default.ChatSendDto), ct);
    }

    public Task RecallAsync(long id, CancellationToken ct = default)
    {
        var dto = new ChatRecallDto { Id = id };
        return SendRawAsync(JsonSerializer.Serialize(dto, ChatJsonContext.Default.ChatRecallDto), ct);
    }

    // ---------------- 局域网（本机网关 /api/lan/*） ----------------
    private static string LanUrl(string path) => SessionState.LocalGatewayUrl.TrimEnd('/') + path;

    public async Task<LanInfoDto> GetLanInfoAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(LanUrl("/api/lan/info"), ct);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync(stream, ChatJsonContext.Default.LanInfoDto, ct) ?? new LanInfoDto();
        }
        catch
        {
            return new LanInfoDto();
        }
    }

    public async Task<LanPeersResponse> GetLanPeersAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(LanUrl("/api/lan/peers"), ct);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync(stream, ChatJsonContext.Default.LanPeersResponse, ct)
                   ?? new LanPeersResponse();
        }
        catch
        {
            return new LanPeersResponse();
        }
    }

    public async Task<LanAddPeerResponse> AddLanPeerAsync(string ip, int port, string name, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, LanUrl("/api/lan/peers/add"));
        var dto = new LanAddPeerRequest { Ip = ip, Port = port, Name = name };
        var body = JsonSerializer.Serialize(dto, ChatJsonContext.Default.LanAddPeerRequest);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        try
        {
            using var resp = await _http.SendAsync(req, ct);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync(stream, ChatJsonContext.Default.LanAddPeerResponse, ct)
                   ?? new LanAddPeerResponse();
        }
        catch
        {
            return new LanAddPeerResponse { Msg = "请求失败" };
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _http.Dispose();
        _sendLock.Dispose();
    }
}
