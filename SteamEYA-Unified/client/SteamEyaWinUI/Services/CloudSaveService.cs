using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

/// <summary>统一网关的连接信息（登录成功后由登录流程写入 Token）。</summary>
internal static class CloudApi
{
    /// <summary>统一网关地址。生产环境可改为 https 域名；开发时指向本机。</summary>
    public static string BaseUrl { get; set; } = "http://47.104.210.199:8897";

    /// <summary>统一账号 JWT。未登录时为 null，仅离线/局域网功能可用。</summary>
    public static string? Token { get; set; }
}

/// <summary>云存档服务：调用统一网关 /api/save/* 。</summary>
internal sealed class CloudSaveService
{
    private static readonly HttpClient HttpClient = new();

    private static string Url(string path) => CloudApi.BaseUrl.TrimEnd('/') + path;

    private static HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, Url(path));
        if (!string.IsNullOrWhiteSpace(CloudApi.Token))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CloudApi.Token);
        }
        return req;
    }

    private static async Task<T> SendJsonAsync<T>(
        HttpRequestMessage req,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken ct)
    {
        using var resp = await HttpClient.SendAsync(req, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var value = await JsonSerializer.DeserializeAsync(stream, typeInfo, ct);
        return value!;
    }

    public async Task<CloudSaveListDto> ListAsync(string game, CancellationToken ct = default)
    {
        using var req = NewRequest(HttpMethod.Get, $"/api/save/list?game={Uri.EscapeDataString(game)}");
        return await SendJsonAsync(req, CloudSaveJsonContext.Default.CloudSaveListDto, ct);
    }

    public async Task<CloudSaveListDto> ListSharedAsync(string game, CancellationToken ct = default)
    {
        using var req = NewRequest(HttpMethod.Get, $"/api/save/shared?game={Uri.EscapeDataString(game)}");
        return await SendJsonAsync(req, CloudSaveJsonContext.Default.CloudSaveListDto, ct);
    }

    /// <summary>服务端已登记的游戏（内置 + 其他人加过的自定义游戏）。</summary>
    public async Task<CloudSaveGamesDto> GamesAsync(CancellationToken ct = default)
    {
        using var req = NewRequest(HttpMethod.Get, "/api/save/games");
        return await SendJsonAsync(req, CloudSaveJsonContext.Default.CloudSaveGamesDto, ct);
    }

    /// <summary>登记自定义游戏名；同名会返回已有 key（含内置游戏名）。</summary>
    public async Task<CloudSaveRegisterDto> RegisterGameAsync(
        string name, string? saveDir = null, CancellationToken ct = default)
    {
        using var req = NewRequest(HttpMethod.Post, "/api/save/games");
        // 用 JsonObject 手拼，避免为匿名对象新增源生成声明（AOT 下反射式序列化被禁用）。
        var json = new System.Text.Json.Nodes.JsonObject { ["name"] = name };
        if (!string.IsNullOrWhiteSpace(saveDir))
        {
            // 路径随游戏下发，别人还原时才知道该往哪个目录放。
            json["save_dir"] = saveDir;
        }

        req.Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json");
        return await SendJsonAsync(req, CloudSaveJsonContext.Default.CloudSaveRegisterDto, ct);
    }

    /// <param name="saveDir">
    /// 本机存档目录。首个上传者填的路径会被服务端记下，之后所有人还原都用这个路径。
    /// </param>
    public async Task<CloudSaveUploadDto> UploadAsync(
        string game, string zipPath, string? name, string? remark, string source,
        string? saveDir = null, CancellationToken ct = default)
    {
        using var req = NewRequest(HttpMethod.Post, "/api/save/upload");
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(game), "game");
        form.Add(new StringContent(name ?? string.Empty), "name");
        form.Add(new StringContent(remark ?? string.Empty), "remark");
        form.Add(new StringContent(source), "source");
        if (!string.IsNullOrWhiteSpace(saveDir))
        {
            form.Add(new StringContent(saveDir), "save_dir");
        }

        var bytes = await File.ReadAllBytesAsync(zipPath, ct);
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(fileContent, "file", Path.GetFileName(zipPath));
        req.Content = form;

        return await SendJsonAsync(req, CloudSaveJsonContext.Default.CloudSaveUploadDto, ct);
    }

    /// <summary>下载存档 zip 到指定文件路径；成功返回 true。</summary>
    public async Task<(bool ok, string? msg)> DownloadAsync(int id, string destFilePath, CancellationToken ct = default)
    {
        using var req = NewRequest(HttpMethod.Get, $"/api/save/download?id={id}");
        using var resp = await HttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return (false, $"下载失败：HTTP {(int)resp.StatusCode}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destFilePath)!);
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(destFilePath))
        {
            await src.CopyToAsync(dst, ct);
        }
        return (true, null);
    }

    public async Task<CloudSaveActionDto> SetRemarkAsync(int id, string remark, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new CloudSaveRemarkRequest { Id = id, Remark = remark },
            CloudSaveJsonContext.Default.CloudSaveRemarkRequest);
        using var req = NewRequest(HttpMethod.Post, "/api/save/remark");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await SendJsonAsync(req, CloudSaveJsonContext.Default.CloudSaveActionDto, ct);
    }

    public async Task<CloudSaveActionDto> ShareAsync(int[] ids, bool shared, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new CloudSaveShareRequest { Ids = ids, Shared = shared },
            CloudSaveJsonContext.Default.CloudSaveShareRequest);
        using var req = NewRequest(HttpMethod.Post, "/api/save/share");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await SendJsonAsync(req, CloudSaveJsonContext.Default.CloudSaveActionDto, ct);
    }

    public async Task<CloudSaveActionDto> DeleteAsync(int[] ids, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new CloudSaveDeleteRequest { Ids = ids },
            CloudSaveJsonContext.Default.CloudSaveDeleteRequest);
        using var req = NewRequest(HttpMethod.Post, "/api/save/delete");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await SendJsonAsync(req, CloudSaveJsonContext.Default.CloudSaveActionDto, ct);
    }
}