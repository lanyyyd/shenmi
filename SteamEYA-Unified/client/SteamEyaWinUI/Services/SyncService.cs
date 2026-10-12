using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 离线 → 在线 同步：把本地（局域网）期间产生的消息幂等回传到远端，
/// 成功后标记本地已同步（第二个用户不再重复上传）。
/// </summary>
internal sealed class SyncService
{
    private static readonly HttpClient HttpClient = new();

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

        // 2) 推送到远端（幂等：服务端按 install_id + client_msg_id 去重）
        var pushReq = new SyncPushRequest { InstallId = SessionState.InstallId, Messages = messages };
        var pushJson = JsonSerializer.Serialize(pushReq, CloudApiJsonContext.Default.SyncPushRequest);
        using var req = new HttpRequestMessage(HttpMethod.Post, SessionState.OnlineBaseUrl + "/api/sync/push")
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
                ClientMsgIds = messages.Select(m => m.ClientMsgId).ToArray()
            };
            var markJson = JsonSerializer.Serialize(markReq, CloudApiJsonContext.Default.SyncMarkRequest);
            using var markContent = new StringContent(markJson, Encoding.UTF8, "application/json");
            await HttpClient.PostAsync(SessionState.LocalGatewayUrl + "/api/sync/mark_synced", markContent, ct);
        }
        catch
        {
            // 标记失败下次会重试；服务端幂等，不会产生重复消息
        }

        return (true, messages.Length, null);
    }
}