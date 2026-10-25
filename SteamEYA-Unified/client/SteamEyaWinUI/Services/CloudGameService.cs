using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

/// <summary>云游戏：云端 Cookie 接口 + 本地引擎（Selenium）调用。</summary>
internal sealed class CloudGameService
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(90) };

    /// <summary>支持的云游戏目标。</summary>
    public static readonly (string Key, string Name)[] Games =
    [
        ("genshin", "原神"),
        ("starrail", "星穹铁道"),
        ("yihuan", "异环"),
        ("bilibili", "哔哩哔哩")
    ];

    private static HttpRequestMessage Auth(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, SessionState.BaseUrl.TrimEnd('/') + path);
        if (!string.IsNullOrWhiteSpace(SessionState.Token))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SessionState.Token);
        }
        return req;
    }

    private static async Task<T?> SendAsync<T>(
        HttpRequestMessage req,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken ct)
    {
        using var resp = await HttpClient.SendAsync(req, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(stream, typeInfo, ct);
    }

    // ---------------- 游戏列表 ----------------

    /// <summary>拉取可见游戏：内置 + 其他人已公开的自定义游戏 + 自己创建的。</summary>
    public async Task<CloudGamesDto> GamesAsync(CancellationToken ct = default)
        => await SendAsync(Auth(HttpMethod.Get, "/api/cloud/games"),
               CloudApiJsonContext.Default.CloudGamesDto, ct) ?? new CloudGamesDto();

    /// <summary>新增自定义游戏（地址由服务端补全 https://）。</summary>
    public async Task<CloudCreateGameDto?> CreateGameAsync(string name, string url, CancellationToken ct = default)
    {
        var json = new JsonObject { ["name"] = name, ["url"] = url }.ToJsonString();
        var req = Auth(HttpMethod.Post, "/api/cloud/games");
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await SendAsync(req, CloudApiJsonContext.Default.CloudCreateGameDto, ct);
    }

    // ---------------- 云端 Cookie ----------------
    public async Task<CloudMyDto> MyAsync(CancellationToken ct = default)
        => await SendAsync(Auth(HttpMethod.Get, "/api/cloud/my"), CloudApiJsonContext.Default.CloudMyDto, ct)
           ?? new CloudMyDto();

    public async Task<CloudDownloadDto> DownloadAsync(string game, CancellationToken ct = default)
        => await SendAsync(Auth(HttpMethod.Get, $"/api/cloud/download?game={game}"),
            CloudApiJsonContext.Default.CloudDownloadDto, ct) ?? new CloudDownloadDto();

    public async Task<CloudActionResultDto> CheckAsync(string game, CancellationToken ct = default)
    {
        var json = new JsonObject { ["game"] = game }.ToJsonString();
        using var req = Auth(HttpMethod.Post, "/api/cloud/check");
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await SendAsync(req, CloudApiJsonContext.Default.CloudActionResultDto, ct) ?? new CloudActionResultDto();
    }

    public async Task<CloudActionResultDto> UploadAsync(string game, JsonElement cookies, string comboToken, CancellationToken ct = default)
    {
        var obj = new JsonObject
        {
            ["game"] = game,
            ["combo_token"] = comboToken,
            ["cookies"] = JsonNode.Parse(cookies.GetRawText())
        };
        using var req = Auth(HttpMethod.Post, "/api/cloud/upload");
        req.Content = new StringContent(obj.ToJsonString(), Encoding.UTF8, "application/json");
        return await SendAsync(req, CloudApiJsonContext.Default.CloudActionResultDto, ct) ?? new CloudActionResultDto();
    }

    public async Task ReportLoginAsync(string game, CancellationToken ct = default)
    {
        var json = new JsonObject { ["game"] = game }.ToJsonString();
        using var req = Auth(HttpMethod.Post, "/api/cloud/report_login");
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        try
        {
            await HttpClient.SendAsync(req, ct);
        }
        catch
        {
            // 上报失败不影响上号
        }
    }

    // ---------------- 本地引擎（Selenium 服务） ----------------
    /// <param name="url">自定义游戏的打开地址；内置游戏传 null 由引擎查表。</param>
    public async Task<(bool ok, string body, string? error)> EngineOpenAsync(string game, string browser, string? url = null, CancellationToken ct = default)
        => await EnginePostAsync("/engine/browser/open", WithUrl(
            new JsonObject { ["game"] = game, ["browser"] = browser }, url), ct);

    public async Task<(bool ok, string body, string? error)> EngineCaptureAsync(string game, string? url = null, CancellationToken ct = default)
        => await EnginePostAsync("/engine/capture", WithUrl(new JsonObject { ["game"] = game }, url), ct);

    public async Task<(bool ok, string body, string? error)> EngineLoginAsync(
        string game, string browser, JsonElement cookies, string comboToken, string? url = null, CancellationToken ct = default)
        => await EnginePostAsync("/engine/login", WithUrl(new JsonObject
        {
            ["game"] = game,
            ["browser"] = browser,
            ["combo_token"] = comboToken,
            ["cookies"] = JsonNode.Parse(cookies.GetRawText())
        }, url), ct);

    /// <summary>自定义游戏才带 url，内置的保持原请求体不变（引擎查表更可靠）。</summary>
    private static JsonObject WithUrl(JsonObject body, string? url)
    {
        if (!string.IsNullOrWhiteSpace(url))
        {
            body["url"] = url;
        }

        return body;
    }

    public async Task<(bool ok, string body, string? error)> EngineRefreshAsync(string game, JsonElement cookies, CancellationToken ct = default)
        => await EnginePostAsync("/engine/refresh", new JsonObject
        {
            ["game"] = game,
            ["cookies"] = JsonNode.Parse(cookies.GetRawText())
        }, ct);

    private static async Task<(bool ok, string body, string? error)> EnginePostAsync(string path, JsonObject payload, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await HttpClient.PostAsync(SessionState.EngineUrl.TrimEnd('/') + path, content, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            return (resp.IsSuccessStatusCode, body, resp.IsSuccessStatusCode ? null : $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, string.Empty, ex.Message);
        }
    }
}