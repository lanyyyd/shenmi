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

    public async Task<(bool ok, AuthResultDto? result, string? error)> RegisterAsync(
        string username, string password, string nickname, CancellationToken ct = default)
        => await PostAsync("/api/auth/register",
            JsonSerializer.Serialize(new AuthRequestDto { Username = username, Password = password, Nickname = nickname },
                CloudApiJsonContext.Default.AuthRequestDto), ct);

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