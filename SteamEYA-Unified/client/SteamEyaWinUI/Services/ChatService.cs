using System.Net;
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

    /// <summary>收到「已读」广播：房间、用户 id、用户名、最后已读消息 id。</summary>
    public event Action<string, int, string, long>? ReadStateChanged;

    /// <summary>连接状态变化。</summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>收到服务端广播的「保队友」指令：所有在线客户端应立即退出本软件。</summary>
    public event Action? PanicReceived;

    /// <summary>小火人流式回复：phase = start / delta / error。</summary>
    public event Action<ChatAiStreamDto>? AiStreamReceived;

    /// <summary>红包领取进度广播：红包 id、已领个数、状态（active / done / expired）。</summary>
    public event Action<long, int, string>? RedPacketChanged;

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

    /// <summary>上传聊天附件。<paramref name="progress"/> 回调 0–1 的发送进度（用于输入区进度条）。</summary>
    public async Task<ChatUploadResultDto> UploadAsync(string filePath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var req = NewRequest(HttpMethod.Post, "/api/upload");
        using var form = new MultipartFormDataContent();
        var bytes = await File.ReadAllBytesAsync(filePath, ct);
        var content = new ProgressByteArrayContent(bytes, progress);
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

    // ---------------- 已读回执 / 搜索 / 资料 / 联系人 ----------------
    /// <summary>当前房间各成员的已读位置（GET /api/read-state）。</summary>
    public async Task<ChatReadStateResponse> GetReadStateAsync(string room, CancellationToken ct = default)
    {
        using var req = NewRequest(HttpMethod.Get, "/api/read-state?room=" + Uri.EscapeDataString(room));
        using var resp = await _http.SendAsync(req, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(stream, ChatJsonContext.Default.ChatReadStateResponse, ct)
               ?? new ChatReadStateResponse();
    }

    /// <summary>搜索聊天记录（GET /api/search）。type 取 text/file/image/video/media。</summary>
    public async Task<ChatSearchResponse> SearchAsync(string? q, string? type, string? sender, string room,
        long from, long to, int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        var parts = new List<string> { "room=" + Uri.EscapeDataString(room), "limit=" + limit, "offset=" + offset };
        if (!string.IsNullOrWhiteSpace(q))
        {
            parts.Add("q=" + Uri.EscapeDataString(q));
        }
        if (!string.IsNullOrWhiteSpace(type))
        {
            parts.Add("type=" + Uri.EscapeDataString(type));
        }
        if (!string.IsNullOrWhiteSpace(sender))
        {
            parts.Add("sender=" + Uri.EscapeDataString(sender));
        }
        if (from > 0)
        {
            parts.Add("frm=" + from);   // 服务端参数名为 frm（起始时间，毫秒）
        }
        if (to > 0)
        {
            parts.Add("to=" + to);
        }
        using var req = NewRequest(HttpMethod.Get, "/api/search?" + string.Join("&", parts));
        using var resp = await _http.SendAsync(req, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(stream, ChatJsonContext.Default.ChatSearchResponse, ct)
               ?? new ChatSearchResponse();
    }

    /// <summary>修改昵称（POST /api/profile）。</summary>
    public Task<ChatProfileResponse> UpdateNicknameAsync(string nickname, CancellationToken ct = default)
        => PostProfileAsync(new ChatProfileRequest { Nickname = nickname }, ct);

    /// <summary>修改密码（POST /api/profile）。</summary>
    public Task<ChatProfileResponse> ChangePasswordAsync(string oldPassword, string newPassword, CancellationToken ct = default)
        => PostProfileAsync(new ChatProfileRequest { OldPassword = oldPassword, NewPassword = newPassword }, ct);

    private async Task<ChatProfileResponse> PostProfileAsync(ChatProfileRequest dto, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Post, "/api/profile");
        var body = JsonSerializer.Serialize(dto, ChatJsonContext.Default.ChatProfileRequest);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(stream, ChatJsonContext.Default.ChatProfileResponse, ct)
               ?? new ChatProfileResponse();
    }

    /// <summary>上传头像（POST /api/profile/avatar，multipart）。</summary>
    public async Task<ChatProfileResponse> UploadAvatarAsync(string filePath, CancellationToken ct = default)
    {
        using var req = NewRequest(HttpMethod.Post, "/api/profile/avatar");
        using var form = new MultipartFormDataContent();
        var bytes = await File.ReadAllBytesAsync(filePath, ct);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", Path.GetFileName(filePath));
        req.Content = form;
        using var resp = await _http.SendAsync(req, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(stream, ChatJsonContext.Default.ChatProfileResponse, ct)
               ?? new ChatProfileResponse();
    }

    /// <summary>联系人备注与置顶列表（GET /api/contacts）。</summary>
    public async Task<ChatContactsResponse> GetContactsAsync(CancellationToken ct = default)
    {
        using var req = NewRequest(HttpMethod.Get, "/api/contacts");
        using var resp = await _http.SendAsync(req, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(stream, ChatJsonContext.Default.ChatContactsResponse, ct)
               ?? new ChatContactsResponse();
    }

    /// <summary>小火人公开配置（公开接口）：名字、账号 id、头像、是否启用。</summary>
    public async Task<ChatAiConfigDto?> GetAiConfigAsync(CancellationToken ct = default)
    {
        using var req = NewRequest(HttpMethod.Get, "/api/ai/config");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(stream, ChatJsonContext.Default.ChatAiConfigDto, ct);
    }

    /// <summary>保存联系人备注（空字符串即清除备注）。</summary>
    public Task<ChatSimpleResponse> SaveNoteAsync(int peerId, string note, CancellationToken ct = default)
        => PostJsonAsync("/api/contacts/note",
            JsonSerializer.Serialize(new ChatContactsNoteRequest { PeerId = peerId, Note = note }, ChatJsonContext.Default.ChatContactsNoteRequest), ct);

    /// <summary>置顶 / 取消置顶联系人。</summary>
    public Task<ChatSimpleResponse> TogglePinAsync(int peerId, bool pinned, CancellationToken ct = default)
        => PostJsonAsync("/api/contacts/pin",
            JsonSerializer.Serialize(new ChatContactsPinRequest { PeerId = peerId, Pinned = pinned }, ChatJsonContext.Default.ChatContactsPinRequest), ct);

    /// <summary>删除表情（仅管理员，DELETE /api/stickers/{id}）。</summary>
    public async Task<ChatSimpleResponse> DeleteStickerAsync(long id, CancellationToken ct = default)
    {
        using var req = NewRequest(HttpMethod.Delete, $"/api/stickers/{id}");
        using var resp = await _http.SendAsync(req, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(stream, ChatJsonContext.Default.ChatSimpleResponse, ct)
               ?? new ChatSimpleResponse();
    }

    private async Task<ChatSimpleResponse> PostJsonAsync(string path, string body, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Post, path);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(stream, ChatJsonContext.Default.ChatSimpleResponse, ct)
               ?? new ChatSimpleResponse();
    }

    // ---------------- 积分 / 抽卡 / 等级 / 红包（玩法中心） ----------------
    /// <summary>GET 请求并反序列化；失败时返回空对象。</summary>
    private async Task<T> GetJsonAsync<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type,
        CancellationToken ct)
        where T : new()
    {
        try
        {
            using var req = NewRequest(HttpMethod.Get, path);
            using var resp = await _http.SendAsync(req, ct);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync(stream, type, ct) ?? new T();
        }
        catch
        {
            return new T();
        }
    }

    /// <summary>POST JSON 并反序列化；非 2xx 时把服务端 detail 填进 Error。</summary>
    private async Task<T> PostJsonAsync<T>(string path, string body,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken ct)
        where T : new()
    {
        using var req = NewRequest(HttpMethod.Post, path);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        T? result = null;
        try
        {
            result = JsonSerializer.Deserialize(text, type);
        }
        catch
        {
            // 解析失败按空结果处理
        }
        var value = result ?? new T();
        if (!resp.IsSuccessStatusCode && value is IChatResult chatResult && string.IsNullOrEmpty(chatResult.Error))
        {
            chatResult.Error = ErrorOf(text);
        }
        return value;
    }

    /// <summary>从 {"detail": "..."} / {"error": "..."} 中提取错误文案。</summary>
    private static string ErrorOf(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("detail", out var d)
                && d.ValueKind == JsonValueKind.String)
            {
                return d.GetString() ?? string.Empty;
            }
            if (doc.RootElement.TryGetProperty("error", out var e)
                && e.ValueKind == JsonValueKind.String)
            {
                return e.GetString() ?? string.Empty;
            }
        }
        catch
        {
            // 非 JSON 响应
        }
        return string.Empty;
    }

    public Task<PointsMeResponse> GetPointsMeAsync(CancellationToken ct = default)
        => GetJsonAsync("/api/points/me", ChatJsonContext.Default.PointsMeResponse, ct);

    public Task<PointsSigninResponse> SigninAsync(CancellationToken ct = default)
        => PostJsonAsync("/api/points/signin", "{}", ChatJsonContext.Default.PointsSigninResponse, ct);

    public Task<PointsItemsResponse> GetPointsItemsAsync(CancellationToken ct = default)
        => GetJsonAsync("/api/points/items", ChatJsonContext.Default.PointsItemsResponse, ct);

    public Task<PointsRedeemResponse> RedeemAsync(int itemId, CancellationToken ct = default)
        => PostJsonAsync("/api/points/redeem",
            JsonSerializer.Serialize(new PointsRedeemRequest { ItemId = itemId }, ChatJsonContext.Default.PointsRedeemRequest),
            ChatJsonContext.Default.PointsRedeemResponse, ct);

    public Task<PointsTransferResponse> TransferAsync(int amount, string to, string remark, CancellationToken ct = default)
        => PostJsonAsync("/api/points/transfer",
            JsonSerializer.Serialize(new PointsTransferRequest { Amount = amount, To = to, Remark = remark },
                ChatJsonContext.Default.PointsTransferRequest),
            ChatJsonContext.Default.PointsTransferResponse, ct);

    public Task<PointsLogsResponse> GetPointsLogsAsync(int limit = 30, int offset = 0, CancellationToken ct = default)
        => GetJsonAsync($"/api/points/logs?limit={limit}&offset={offset}", ChatJsonContext.Default.PointsLogsResponse, ct);

    public Task<GachaInfoResponse> GetGachaInfoAsync(CancellationToken ct = default)
        => GetJsonAsync("/api/gacha/info", ChatJsonContext.Default.GachaInfoResponse, ct);

    public Task<GachaDrawResponse> DrawGachaAsync(int poolId, int times, CancellationToken ct = default)
        => PostJsonAsync("/api/gacha/draw",
            JsonSerializer.Serialize(new GachaDrawRequest { PoolId = poolId, Times = times },
                ChatJsonContext.Default.GachaDrawRequest),
            ChatJsonContext.Default.GachaDrawResponse, ct);

    public Task<LevelMeResponse> GetLevelMeAsync(CancellationToken ct = default)
        => GetJsonAsync("/api/level/me", ChatJsonContext.Default.LevelMeResponse, ct);

    public Task<RedPacketSendResponse> SendRedPacketAsync(string asset, string mode, int total, int per, int count,
        string remark, CancellationToken ct = default)
        => PostJsonAsync("/api/redpacket/send",
            JsonSerializer.Serialize(new RedPacketSendRequest
            {
                Asset = asset, Mode = mode, Total = total, Per = per, Count = count, Remark = remark
            }, ChatJsonContext.Default.RedPacketSendRequest),
            ChatJsonContext.Default.RedPacketSendResponse, ct);

    public Task<RedPacketDetailResponse> GetRedPacketAsync(long id, CancellationToken ct = default)
        => GetJsonAsync($"/api/redpacket/{id}", ChatJsonContext.Default.RedPacketDetailResponse, ct);

    public Task<RedPacketGrabResponse> GrabRedPacketAsync(long id, CancellationToken ct = default)
        => PostJsonAsync($"/api/redpacket/{id}/grab", "{}", ChatJsonContext.Default.RedPacketGrabResponse, ct);

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
                case "panic":
                    PanicReceived?.Invoke();
                    break;
                case "recall":
                    if (doc.RootElement.TryGetProperty("id", out var rid) && rid.TryGetInt64(out var id))
                    {
                        MessageRecalled?.Invoke(id);
                    }
                    break;
                case "read":
                    var uid = doc.RootElement.TryGetProperty("userId", out var ue) && ue.TryGetInt32(out var u) ? u : 0;
                    var uname = doc.RootElement.TryGetProperty("username", out var ne) ? ne.GetString() ?? string.Empty : string.Empty;
                    var lastId = doc.RootElement.TryGetProperty("lastReadId", out var le) && le.TryGetInt64(out var l) ? l : 0;
                    var readRoom = doc.RootElement.TryGetProperty("room", out var re) ? re.GetString() ?? string.Empty : string.Empty;
                    if (uid > 0)
                    {
                        ReadStateChanged?.Invoke(readRoom, uid, uname, lastId);
                    }
                    break;
                case "ai_stream":
                    var stream = doc.RootElement.Deserialize(ChatJsonContext.Default.ChatAiStreamDto);
                    if (stream is not null)
                    {
                        AiStreamReceived?.Invoke(stream);
                    }
                    break;
                case "redpacket":
                    var pid = doc.RootElement.TryGetProperty("packetId", out var pe) && pe.TryGetInt64(out var pv) ? pv : 0;
                    var grabbed = doc.RootElement.TryGetProperty("grabbedCount", out var ge) && ge.TryGetInt32(out var gv) ? gv : 0;
                    var status = doc.RootElement.TryGetProperty("status", out var se) ? se.GetString() ?? string.Empty : string.Empty;
                    if (pid > 0)
                    {
                        RedPacketChanged?.Invoke(pid, grabbed, status);
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

    public Task SendChatAsync(string kind, string content, ChatFileDto? file = null, int? to = null,
        string[]? mentions = null, CancellationToken ct = default)
    {
        var dto = new ChatSendDto { MsgType = kind, Content = content, File = file, To = to, Mentions = mentions };
        return SendRawAsync(JsonSerializer.Serialize(dto, ChatJsonContext.Default.ChatSendDto), ct);
    }

    /// <summary>发送表情包：只传 stickerId，由服务端按 id 回填 file 字段并累加发送次数。</summary>
    public Task SendStickerAsync(long stickerId, int? to = null, CancellationToken ct = default)
    {
        var dto = new ChatSendDto { MsgType = "sticker", Content = string.Empty, StickerId = stickerId, To = to };
        return SendRawAsync(JsonSerializer.Serialize(dto, ChatJsonContext.Default.ChatSendDto), ct);
    }

    /// <summary>表情包列表（GET /api/stickers）。sort=hot 按发送次数，time 按最新。</summary>
    public async Task<ChatStickersResponse> GetStickersAsync(string sort = "hot", CancellationToken ct = default)
    {
        using var req = NewRequest(HttpMethod.Get, $"/api/stickers?sort={Uri.EscapeDataString(sort)}&limit=300");
        using var resp = await _http.SendAsync(req, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(stream, ChatJsonContext.Default.ChatStickersResponse, ct)
               ?? new ChatStickersResponse();
    }

    /// <summary>上传表情包（POST /api/stickers，multipart）。</summary>
    public async Task<ChatStickerUploadResponse> UploadStickerAsync(string filePath, CancellationToken ct = default)
    {
        using var req = NewRequest(HttpMethod.Post, "/api/stickers");
        using var form = new MultipartFormDataContent();
        var bytes = await File.ReadAllBytesAsync(filePath, ct);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", Path.GetFileName(filePath));
        req.Content = form;
        using var resp = await _http.SendAsync(req, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(stream, ChatJsonContext.Default.ChatStickerUploadResponse, ct)
               ?? new ChatStickerUploadResponse();
    }

    /// <summary>
    /// 发送「保队友」指令：服务端会广播给所有在线客户端（包括自己），收到后立即退出本软件。
    /// 返回 false 表示当前没有可用的在线连接。
    /// </summary>
    public async Task<bool> SendPanicAsync(CancellationToken ct = default)
    {
        if (!IsConnected)
        {
            return false;
        }
        await SendRawAsync("{\"type\":\"panic\"}", ct);
        return true;
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

    /// <summary>带进度的字节内容（分块写入请求流，供上传进度条使用）。</summary>
    private sealed class ProgressByteArrayContent : ByteArrayContent
    {
        private const int ChunkSize = 64 * 1024;

        private readonly byte[] _bytes;
        private readonly IProgress<double>? _progress;

        public ProgressByteArrayContent(byte[] bytes, IProgress<double>? progress) : base(bytes)
        {
            _bytes = bytes;
            _progress = progress;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var total = _bytes.Length;
            if (total == 0)
            {
                _progress?.Report(1);
                return;
            }
            var sent = 0;
            while (sent < total)
            {
                var count = Math.Min(ChunkSize, total - sent);
                await stream.WriteAsync(_bytes.AsMemory(sent, count));
                sent += count;
                _progress?.Report((double)sent / total);
            }
        }
    }
}
