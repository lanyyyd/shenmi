using System.Net.Http;
using System.Text;
using System.Text.Json;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

/// <summary>统一账号：登录 / 注册 / 离线昵称登录。</summary>
internal sealed class CloudAuthService
{
    private static readonly HttpClient HttpClient = new();

    public async Task<(bool ok, AuthResultDto? result, string? error)> LoginAsync(
        string username, string password, CancellationToken ct = default)
        => await PostAsync("/api/auth/login",
            JsonSerializer.Serialize(new AuthRequestDto { Username = username, Password = password },
                CloudApiJsonContext.Default.AuthRequestDto), ct);

    /// <summary>
    /// 注册。直接复用网页版聊天已有的 <c>/api/register</c>（服务端既有逻辑，含邀请码校验、
    /// 与后台「注册必须填写邀请码」开关联动），成功后用同一套账号调用 <c>/api/auth/login</c>
    /// 换取 JWT —— 全程不需要改动或重新部署服务端。
    /// 账号数据同属 users 表，因此网页版与客户端共用同一套账号体系。
    /// </summary>
    public async Task<(bool ok, AuthResultDto? result, string? error)> RegisterAsync(
        string username, string password, string nickname, string inviteCode = "", CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(
            new AuthRequestDto
            {
                Username = username,
                Password = password,
                Nickname = nickname,
                InviteCode = string.IsNullOrWhiteSpace(inviteCode) ? null : inviteCode.Trim()
            },
            CloudApiJsonContext.Default.AuthRequestDto);

        var (registered, error) = await PostForResultAsync("/api/register", json, ct);
        if (!registered)
        {
            return (false, null, error);
        }
        // 网页版注册只下发 cookie 会话，客户端各处都需要 JWT，这里再登录一次换取。
        return await LoginAsync(username, password, ct);
    }

    /// <summary>只看成功与否的 POST（用于返回结构不是 JWT 的接口）。</summary>
    private static async Task<(bool ok, string? error)> PostForResultAsync(
        string path, string json, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await HttpClient.PostAsync(
                SessionState.OnlineBaseUrl.TrimEnd('/') + path, content, ct);
            if (resp.IsSuccessStatusCode)
            {
                return (true, null);
            }
            var body = await resp.Content.ReadAsStringAsync(ct);
            return (false, ExtractError(body) ?? $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// 注册是否需要邀请码 + 是否还有可用码（GET /api/invite-info，服务端既有接口）。
    /// 与网页版注册读的是同一个后台开关，用于决定客户端注册页是否显示邀请码输入框。
    /// </summary>
    public async Task<InviteInfoDto> GetInviteInfoAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await HttpClient.GetAsync(
                SessionState.OnlineBaseUrl.TrimEnd('/') + "/api/invite-info", ct);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync(stream, CloudApiJsonContext.Default.InviteInfoDto, ct)
                   ?? new InviteInfoDto { Required = true };
        }
        catch
        {
            // 查询失败时宁可显示输入框：留空也能注册（服务端未开启必填时不会校验），
            // 但若确实需要邀请码而这里误判为“不需要”，用户将无从填写。
            return new InviteInfoDto { Required = true };
        }
    }

    /// <summary>离线模式：仅凭昵称登录本地网关。</summary>
    public async Task<(bool ok, AuthResultDto? result, string? error)> LocalLoginAsync(
        string nickname, CancellationToken ct = default)
        => await PostAsync("/api/auth/local",
            JsonSerializer.Serialize(new LocalLoginRequestDto { Nickname = nickname },
                CloudApiJsonContext.Default.LocalLoginRequestDto), ct, useLocalGateway: true);

    private static async Task<(bool ok, AuthResultDto? result, string? error)> PostAsync(
        string path, string json, CancellationToken ct, bool useLocalGateway = false)
    {
        var baseUrl = useLocalGateway ? SessionState.LocalGatewayUrl : SessionState.OnlineBaseUrl;
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await HttpClient.PostAsync(baseUrl.TrimEnd('/') + path, content, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                return (false, null, ExtractError(body) ?? $"HTTP {(int)resp.StatusCode}");
            }
            var result = JsonSerializer.Deserialize(body, CloudApiJsonContext.Default.AuthResultDto);
            return (true, result, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    private static string? ExtractError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var key in new[] { "detail", "msg", "error" })
            {
                if (doc.RootElement.TryGetProperty(key, out var v))
                {
                    return v.GetString();
                }
            }
        }
        catch
        {
            // 非 JSON，忽略
        }
        return null;
    }
}