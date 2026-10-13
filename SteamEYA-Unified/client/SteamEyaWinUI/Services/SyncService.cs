using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 离线 → 在线 同步：把本地（局域网）期间产生的消息幂等回传到远端，
/// 成功后标记本地已同步（第二个用户不再重复上传）。
///
/// 注意：离线期间上传的图片/视频/文件，**本体**只存在本地网关磁盘上（消息里的 file.url 是本机
/// 相对路径）。因此同步时必须先把文件本体转存到远端、再把 file.url 改写成远端地址，否则远端
/// 只有消息记录、图片打不开、文件下载 404——即“只有文字能同步过去”的根因。
/// </summary>
internal sealed class SyncService
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>尝试同步；仅在在线且有令牌时执行。</summary>
    public async Task<(bool ok, int pushed, string? msg)> TrySyncAsync(CancellationToken ct = default)
    {
        if (SessionState.Offline || string.IsNullOrWhiteSpace(SessionState.Token))
        {
            return (false, 0, "未登录或不处于可同步状态");
        }

        // 1) 从本地网关读取待同步消息
        SyncPendingDto pending;
        try
        {
            var url = SessionState.LocalGatewayUrl + "/api/sync/pending?install_id=" + Uri.EscapeDataString(SessionState.InstallId);
            var body = await HttpClient.GetStringAsync(url, ct);
            pending = JsonSerializer.Deserialize(body, CloudApiJsonContext.Default.SyncPendingDto)!;
        }
        catch
        {
            return (true, 0, null); // 本地网关未运行则视为无需同步
        }

        var messages = pending.Messages ?? [];
        if (messages.Length == 0)
        {
            return (true, 0, null);
        }

        // 1.5) 转存媒体文件：仅当消息无文件、或文件已成功传到远端时才参与本次推送；
        //      本地文件暂时取不到/上传失败的消息留待下次同步重试，避免把坏链接推给服务端。
        var ready = new List<SyncMessage>(messages.Length);
        foreach (var m in messages)
        {
            if (await PrepareFileAsync(m, ct))
            {
                ready.Add(m);
            }
        }
        if (ready.Count == 0)
        {
            return (true, 0, null);
        }

        // 2) 推送到远端（幂等：服务端按 install_id + client_msg_id 去重）
        var pushReq = new SyncPushRequest { InstallId = SessionState.InstallId, Messages = [.. ready] };
        var pushJson = JsonSerializer.Serialize(pushReq, CloudApiJsonContext.Default.SyncPushRequest);
        using var req = new HttpRequestMessage(HttpMethod.Post, SessionState.OnlineBaseUrl.TrimEnd('/') + "/api/sync/push")
        {
            Content = new StringContent(pushJson, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SessionState.Token);

        SyncPushResultDto pushResult;
        try
        {
            using var resp = await HttpClient.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                return (false, 0, $"同步失败：HTTP {(int)resp.StatusCode}");
            }
            pushResult = JsonSerializer.Deserialize(body, CloudApiJsonContext.Default.SyncPushResultDto)!;
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }

        if (pushResult.Ok != true)
        {
            return (false, 0, pushResult.Msg ?? "同步失败");
        }

        // 3) 标记本地已同步（关键：避免第二次打开重复上传）
        try
        {
            var markReq = new SyncMarkRequest
            {
                InstallId = SessionState.InstallId,
                ClientMsgIds = ready.Select(m => m.ClientMsgId).ToArray()
            };
            var markJson = JsonSerializer.Serialize(markReq, CloudApiJsonContext.Default.SyncMarkRequest);
            using var markContent = new StringContent(markJson, Encoding.UTF8, "application/json");
            await HttpClient.PostAsync(SessionState.LocalGatewayUrl + "/api/sync/mark_synced", markContent, ct);
        }
        catch
        {
            // 标记失败下次会重试；服务端幂等，不会产生重复消息
        }

        return (true, ready.Count, null);
    }

    /// <summary>
    /// 确保消息引用的媒体文件在远端可用：无文件 → 直接就绪；指向本机的相对路径 → 转存到远端并
    /// 就地改写 file.url；返回是否就绪（false 表示本次先跳过、留待下次同步重试）。
    /// </summary>
    private static async Task<bool> PrepareFileAsync(SyncMessage m, CancellationToken ct)
    {
        if (m.File is not { } file || file.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        var url = file.TryGetProperty("url", out var u) ? u.GetString() : null;
        if (string.IsNullOrWhiteSpace(url))
        {
            return true;
        }
        // 已是远端/绝对地址：无需转存（例如消息本就在线产生，或之前已同步过）
        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        byte[] bytes;
        try
        {
            bytes = await HttpClient.GetByteArrayAsync(SessionState.LocalGatewayUrl.TrimEnd('/') + url, ct);
        }
        catch
        {
            return false;
        }

        var name = file.TryGetProperty("name", out var n) ? (n.GetString() ?? string.Empty) : string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = url[(url.LastIndexOf('/') + 1)..];
        }

        var uploaded = await UploadToRemoteAsync(bytes, name, ct);
        if (uploaded is null)
        {
            return false;
        }

        m.File = JsonSerializer.SerializeToElement(
            new SyncFileRef { Url = uploaded.Url, Name = uploaded.Name, Size = uploaded.Size },
            CloudApiJsonContext.Default.SyncFileRef);
        return true;
    }

    /// <summary>把文件本体 POST 到远端 /api/upload，返回远端文件引用（url/name/size）；失败返回 null。</summary>
    private static async Task<ChatUploadResultDto?> UploadToRemoteAsync(byte[] bytes, string name, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, SessionState.OnlineBaseUrl.TrimEnd('/') + "/api/upload");
            using var form = new MultipartFormDataContent();
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(content, "file", name);
            req.Content = form;
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SessionState.Token);

            using var resp = await HttpClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var dto = await JsonSerializer.DeserializeAsync(stream, ChatJsonContext.Default.ChatUploadResultDto, ct);
            return dto is { Ok: true } && !string.IsNullOrEmpty(dto.Url) ? dto : null;
        }
        catch
        {
            return null;
        }
    }
}
