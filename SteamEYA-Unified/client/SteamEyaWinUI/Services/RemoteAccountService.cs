using System.Net.Http;
using System.Text.Json;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 远程账号服务：用统一账号登录服务器后，从服务端领取一个 Steam 账号（EYA 令牌），用于一键上号。
/// 对接统一网关 /api/auth/*（登录/注册）与 /api/steam/account/get（取号），令牌走统一会话（SessionState）。
/// </summary>
internal sealed class RemoteAccountService
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly CloudAuthService _auth = new();

    /// <summary>是否已登录统一账号（有会话令牌即可取号）。</summary>
    public bool IsLoggedIn => !string.IsNullOrEmpty(SessionState.Token);

    /// <summary>当前登录用户名（未登录为空）。</summary>
    public string Username => SessionState.Username;

    /// <summary>登录统一账号，成功后写入会话。</summary>
    public async Task<RemoteAuthResult> LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        var (ok, result, error) = await _auth.LoginAsync(username, password, cancellationToken);
        if (!ok || result?.AccessToken is null)
        {
            return new RemoteAuthResult(false, error ?? "登录失败");
        }

        SessionState.EnterOnline(result.AccessToken, result.User?.Username ?? username, result.User?.Nickname ?? username);
        return new RemoteAuthResult(true, "登录成功");
    }

    /// <summary>注册统一账号，成功后自动登录并写入会话。</summary>
    public async Task<RemoteAuthResult> RegisterAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        var (ok, result, error) = await _auth.RegisterAsync(username, password, username, cancellationToken);
        if (!ok || result?.AccessToken is null)
        {
            return new RemoteAuthResult(false, error ?? "注册失败");
        }

        SessionState.EnterOnline(result.AccessToken, result.User?.Username ?? username, result.User?.Nickname ?? username);
        return new RemoteAuthResult(true, "注册成功");
    }

    /// <summary>从服务器领取一个账号（服务端按用户锁定/复用）。</summary>
    public async Task<RemoteAccountResult> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        if (!IsLoggedIn)
        {
            return new RemoteAccountResult(false, "未登录", null);
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, SessionState.OnlineBaseUrl.TrimEnd('/') + "/api/steam/account/get");
            request.Headers.Add("Authorization", $"Bearer {SessionState.Token}");

            using var response = await HttpClient.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                using var json = JsonDocument.Parse(content);
                var root = json.RootElement;
                var accountName = GetString(root, "account_name");
                var eyaToken = GetString(root, "eya_token");
                var steamId = GetString(root, "steam_id");
                return new RemoteAccountResult(true, "获取成功", new RemoteAccountData(accountName, eyaToken, steamId));
            }

            return new RemoteAccountResult(false, ExtractError(content) ?? $"HTTP {(int)response.StatusCode}", null);
        }
        catch (Exception ex)
        {
            return new RemoteAccountResult(false, $"请求失败: {ex.Message}", null);
        }
    }

    /// <summary>退出登录：清空统一会话令牌。</summary>
    public void Logout() => SessionState.Clear();

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? (value.GetString() ?? string.Empty) : string.Empty;

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

internal sealed record RemoteAuthResult(bool IsSuccess, string Message);

internal sealed record RemoteAccountResult(bool IsSuccess, string Message, RemoteAccountData? Account);

internal sealed record RemoteAccountData(string AccountName, string EyaToken, string SteamId);