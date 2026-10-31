using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using QuickAnswer.Core.Models;

namespace QuickAnswer.Core.Api;

/// <summary>token 校验结果。</summary>
public enum TokenState
{
    /// <summary>有效。</summary>
    Valid,
    /// <summary>服务端明确拒绝（过期 / 被封）—— 该回登录页。</summary>
    Unauthorized,
    /// <summary>连不上服务端 —— 保留 token，别把用户踢下线。</summary>
    NetworkError,
}

public sealed class ApiException : Exception
{
    public HttpStatusCode Status { get; }
    public ApiException(HttpStatusCode status, string message) : base(message) => Status = status;
}

/// <summary>
/// 后端访问。BaseUrl 指向用户自己的 shenmi-server 网关，
/// 登录走它现有的 /api/auth/login，题库走 /api/v1/qa/*。
/// </summary>
public sealed class ApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;

    public string BaseUrl { get; private set; } = "";
    public string? AccessToken { get; private set; }
    public UserDto? CurrentUser { get; private set; }

    public ApiClient()
    {
        _http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public void Configure(string baseUrl, string? token)
    {
        BaseUrl = (baseUrl ?? "").TrimEnd('/');
        AccessToken = token;
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, BaseUrl + path);
        if (!string.IsNullOrEmpty(AccessToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        return req;
    }

    // ---------------------------------------------------------------- 认证

    public async Task<UserDto> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Post, "/api/auth/login");
        req.Content = JsonContent.Create(new { username, password }, options: JsonOpts);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new ApiException(resp.StatusCode, ExtractDetail(body, resp.StatusCode));

        var login = JsonSerializer.Deserialize<LoginResponse>(body, JsonOpts)
                    ?? throw new ApiException(resp.StatusCode, "登录返回内容无法解析");

        if (string.IsNullOrEmpty(login.AccessToken) || login.User is null)
            throw new ApiException(resp.StatusCode, "登录返回缺少 token");

        AccessToken = login.AccessToken;
        CurrentUser = login.User;
        return login.User;
    }

    /// <summary>
    /// 启动时校验已保存的 token。
    /// 必须区分「token 失效」和「网连不上」：后者踢用户下线，等于断网就打不开软件。
    /// </summary>
    public async Task<(TokenState State, UserDto? User)> VerifyTokenAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(AccessToken))
            return (TokenState.Unauthorized, null);

        try
        {
            using var req = Request(HttpMethod.Get, "/api/auth/me");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return (TokenState.Unauthorized, null);

            if (!resp.IsSuccessStatusCode)
                return (TokenState.NetworkError, null);

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("user", out var u))
            {
                CurrentUser = JsonSerializer.Deserialize<UserDto>(u.GetRawText(), JsonOpts);
                return CurrentUser is null
                    ? (TokenState.NetworkError, null)
                    : (TokenState.Valid, CurrentUser);
            }

            return (TokenState.NetworkError, null);
        }
        catch
        {
            return (TokenState.NetworkError, null);
        }
    }

    // ---------------------------------------------------------------- 题库

    public async Task<SearchResponse?> SearchAsync(ParsedQuestion q, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Post, "/api/v1/qa/search");
        req.Content = JsonContent.Create(new SearchRequest
        {
            Stem = q.Stem,
            Options = q.Options.Select(o => new OptionItem { Key = o.Key, Text = o.Text }).ToList(),
            Type = TypeName(q.Type),
            RawOcr = q.RawOcr,
        }, options: JsonOpts);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return null;

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<SearchResponse>(body, JsonOpts);
    }

    /// <summary>流式求解。onDelta 用于「首字一到就先给用户反馈」。</summary>
    public async Task<SolveResponse?> SolveAsync(
        ParsedQuestion q, string? modelId, Action<string>? onDelta, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Post, "/api/v1/qa/solve");
        req.Content = JsonContent.Create(new
        {
            stem = q.Stem,
            options = q.Options.Select(o => new { key = o.Key, text = o.Text }).ToList(),
            type = TypeName(q.Type),
            model_id = modelId,
            raw_ocr = q.RawOcr,
        }, options: JsonOpts);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new ApiException(resp.StatusCode, ExtractDetail(err, resp.StatusCode));
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        SolveResponse? final = null;
        var acc = new StringBuilder();

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var payload = line[5..].Trim();
            if (payload.Length == 0 || payload == "[DONE]")
                continue;

            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;

                if (root.TryGetProperty("delta", out var d))
                {
                    var piece = d.GetString() ?? "";
                    if (piece.Length > 0)
                    {
                        acc.Append(piece);
                        onDelta?.Invoke(acc.ToString());
                    }
                }

                if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
                {
                    if (root.TryGetProperty("result", out var r))
                    {
                        final = new SolveResponse
                        {
                            Result = JsonSerializer.Deserialize<ApiAnswerDto>(r.GetRawText(), JsonOpts),
                            Model = root.TryGetProperty("model", out var m) ? m.GetString() : null,
                            ElapsedMs = root.TryGetProperty("elapsed_ms", out var e) ? e.GetInt64() : 0,
                        };
                    }
                }
            }
            catch (JsonException)
            {
                // 半行数据，跳过
            }
        }

        // 有些中转会忽略 stream 直接返回整包 JSON，这里兜一层
        if (final is null && acc.Length > 0)
        {
            try
            {
                var dto = JsonSerializer.Deserialize<ApiAnswerDto>(acc.ToString(), JsonOpts);
                if (dto is not null)
                    final = new SolveResponse { Result = dto };
            }
            catch (JsonException)
            {
            }
        }

        return final;
    }

    /// <summary>上报题库。拿到答案后立即调用，不等任何后续操作。</summary>
    public async Task ContributeAsync(
        ParsedQuestion q, string? letter, string text, string? model, double confidence,
        CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Post, "/api/v1/qa/contribute");
        req.Content = JsonContent.Create(new ContributeRequest
        {
            Stem = q.Stem,
            Options = q.Options.Select(o => new OptionItem { Key = o.Key, Text = o.Text }).ToList(),
            Type = TypeName(q.Type),
            AnswerLetter = string.IsNullOrWhiteSpace(letter) ? null : letter,
            AnswerText = text,
            Source = "ai",
            Model = model,
            Confidence = confidence,
            RawOcr = q.RawOcr,
        }, options: JsonOpts);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        // 上报失败不影响用户，吞掉即可（有日志即可，不阻塞主流程）
    }

    /// <summary>
    /// 拉模型列表。返回 null 表示这次请求失败（路由不存在 / 服务端报错），
    /// 返回空列表表示请求成功但后台还没配模型 —— 这两种情况对用户的提示完全不同。
    /// </summary>
    public async Task<List<ModelInfo>?> GetModelsAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = Request(HttpMethod.Get, "/api/v1/qa/models");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var list = JsonSerializer.Deserialize<List<ModelInfo>>(body, JsonOpts);
            if (list is null)
                return null;

            return list.Where(m => m.Enabled).ToList();
        }
        catch
        {
            return null;
        }
    }

    public static string TypeName(QuestionType t) => t switch
    {
        QuestionType.Choice => "choice",
        QuestionType.Multi => "multi",
        QuestionType.Judge => "judge",
        QuestionType.Fill => "fill",
        QuestionType.Short => "short",
        _ => "choice",
    };

    private static string ExtractDetail(string body, HttpStatusCode status)
    {
        try
        {
            var err = JsonSerializer.Deserialize<ErrorDto>(body, JsonOpts);
            if (!string.IsNullOrWhiteSpace(err?.Detail))
                return err!.Detail!;
        }
        catch
        {
        }

        // 500 一律把服务端返回的原文带出来 —— 否则只有一个状态码，
        // 根本分不清是「后端没装题库模块」「AI 没配」还是「代码报错」。
        var snippet = (body ?? "").Trim();
        if (snippet.Length > 220)
            snippet = snippet[..220];

        return status switch
        {
            HttpStatusCode.Unauthorized => "未登录或登录已过期",
            HttpStatusCode.Forbidden => "账号已被禁用",
            HttpStatusCode.NotFound => "接口不存在——后端可能还没装题库模块，或服务器地址少了路径",
            _ => snippet.Length > 0
                ? $"服务端错误 HTTP {(int)status}：{snippet}"
                : $"请求失败（HTTP {(int)status}）",
        };
    }

    public void Dispose() => _http.Dispose();
}
