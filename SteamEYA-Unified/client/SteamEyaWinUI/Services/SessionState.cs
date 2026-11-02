using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 全局会话与模式状态：统一账号 JWT、在线/离线模式、本机安装标识、各服务地址。
/// 登录成功后写入；离线模式仅凭昵称登录本地网关。
/// </summary>
internal static class SessionState
{
    /// <summary>在线统一网关地址（HTTPS 域名，安卓端与聊天页也已统一走这个）。</summary>
    public const string OnlineBaseUrl = "https://chat.teatt.top";

    /// <summary>离线/局域网本地网关地址（本机运行）。</summary>
    public const string LocalGatewayUrl = "http://127.0.0.1:8898";

    /// <summary>云游戏引擎本地服务地址。</summary>
    public const string EngineUrl = "http://127.0.0.1:6971";

    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(4) };

    public static string BaseUrl { get; private set; } = OnlineBaseUrl;

    public static string? Token { get; set; }

    public static string Username { get; set; } = string.Empty;

    public static string Nickname { get; set; } = string.Empty;

    /// <summary>本机安装唯一标识（离线同步幂等去重用）。</summary>
    public static string InstallId { get; private set; } = string.Empty;

    /// <summary>是否处于离线（局域网）模式。</summary>
    public static bool Offline { get; private set; }

    /// <summary>在线/离线运行态发生变化（切换完成后触发，供导航、页面按模式重算）。</summary>
    public static event Action? ModeChanged;

    /// <summary>最近一次在线会话（仅内存缓存）：离线→在线时可免密码直接恢复。</summary>
    private static (string Token, string Username, string Nickname)? _cachedOnline;

    public static (string Token, string Username, string Nickname)? CachedOnlineSession => _cachedOnline;

    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamEYA", "session.txt");

    public static void Initialize()
    {
        InstallId = LoadOrCreateInstallId();
        Username = LoadValue("username");
        Nickname = LoadValue("nickname");
        Apply();
    }

    /// <summary>把当前状态同步到 API 客户端（地址与令牌随模式切换）。</summary>
    public static void Apply()
    {
        CloudApi.BaseUrl = Offline ? LocalGatewayUrl : OnlineBaseUrl;
        CloudApi.Token = Token;
    }

    public static void EnterOnline(string token, string username, string nickname)
    {
        Offline = false;
        Token = token;
        Username = username;
        Nickname = nickname;
        BaseUrl = OnlineBaseUrl;
        _cachedOnline = (token, username, nickname);
        Apply();
        SaveValue("username", username);
        SaveValue("nickname", nickname);
        // 登录即拉起常驻聊天连接：之后切到任何页面都能收消息并弹系统提醒。
        ChatHub.EnsureStarted();
        ModeChanged?.Invoke();
    }

    public static void EnterOffline(string localToken, string nickname)
    {
        Offline = true;
        Token = localToken;
        Username = nickname;
        Nickname = nickname;
        BaseUrl = LocalGatewayUrl;
        Apply();
        SaveValue("nickname", nickname);
        // 离线同样拉起常驻聊天：BaseUrl 已指向本机网关（调用方此前已确保网关在跑）。
        ChatHub.EnsureStarted();
        ModeChanged?.Invoke();
    }

    /// <summary>退出登录：清空令牌与用户名（供「远程账号」退出使用）。</summary>
    public static void Clear()
    {
        ChatHub.Shutdown();
        _cachedOnline = null;
        Token = null;
        Username = string.Empty;
        Nickname = string.Empty;
        Apply();
        SaveValue("username", string.Empty);
        SaveValue("nickname", string.Empty);
        ModeChanged?.Invoke();
    }

    /// <summary>探测在线网关是否可达（离线时用）。</summary>
    public static async Task<bool> ProbeOnlineAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await HttpClient.GetAsync(OnlineBaseUrl + "/health", ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>校验缓存的在线 JWT 是否仍有效（GET /api/me），供离线→在线免密码恢复。</summary>
    public static async Task<bool> ValidateOnlineTokenAsync(string token, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, OnlineBaseUrl + "/api/me");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await HttpClient.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // ---------------- 本地持久化（纯文本，避免 AOT JSON 反射） ----------------
    private static string LoadOrCreateInstallId()
    {
        var id = LoadValue("install_id");
        if (string.IsNullOrWhiteSpace(id))
        {
            id = Guid.NewGuid().ToString("N");
            SaveValue("install_id", id);
        }
        return id;
    }

    private static string LoadValue(string key)
    {
        try
        {
            if (!File.Exists(StorePath))
            {
                return string.Empty;
            }
            foreach (var line in File.ReadAllLines(StorePath))
            {
                var idx = line.IndexOf('=');
                if (idx > 0 && line[..idx] == key)
                {
                    return line[(idx + 1)..];
                }
            }
        }
        catch
        {
            // 读取失败按空处理
        }
        return string.Empty;
    }

    private static void SaveValue(string key, string value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (File.Exists(StorePath))
            {
                foreach (var line in File.ReadAllLines(StorePath))
                {
                    var idx = line.IndexOf('=');
                    if (idx > 0)
                    {
                        map[line[..idx]] = line[(idx + 1)..];
                    }
                }
            }
            map[key] = value;
            File.WriteAllLines(StorePath, map.Select(kv => $"{kv.Key}={kv.Value}"));
        }
        catch
        {
            // 落盘失败不影响使用
        }
    }
}