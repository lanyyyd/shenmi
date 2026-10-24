using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using SteamEyaWinUI.Localization;

namespace SteamEyaWinUI.Services;

/// <summary>服务端 <c>/api/tips/next</c> 的响应。</summary>
internal sealed class TipResponse
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }
}

[JsonSerializable(typeof(TipResponse))]
internal sealed partial class TipJsonContext : JsonSerializerContext;

/// <summary>
/// 导航栏底部气泡的文案来源。
///
/// 服务端按权重从「后台手动条目」与「一言 API」中抽一条（手动权重默认 10，一言默认 1，
/// 所以正常情况下以手动内容为主）。这里只负责取回并做本地兜底：
/// 离线模式（本机网关没有 tips 数据）、服务端 404、超时、返回空串，都会退回内置短句，
/// 保证气泡里永远有字，不会出现空白块。
/// </summary>
internal static class TipService
{
    private static readonly HttpClient Http = new()
    {
        // 气泡刷新是后台行为，超时要短：卡住只会让文字迟迟不换，不如早点回退本地文案。
        Timeout = TimeSpan.FromSeconds(6)
    };

    private static readonly string[] FallbackKeys =
    [
        "Tip_Fallback_1",
        "Tip_Fallback_2",
        "Tip_Fallback_3",
        "Tip_Fallback_4"
    ];

    /// <summary>取一条文案；任何失败都返回本地兜底短句。</summary>
    public static async Task<string> FetchAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // 离线模式连的是本机网关，它没有 tips 数据，没必要发这次请求。
            var baseUrl = SessionState.BaseUrl;
            if (SessionState.Offline || string.IsNullOrWhiteSpace(baseUrl))
            {
                return LocalFallback();
            }

            var url = baseUrl.TrimEnd('/') + "/api/tips/next";
            using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                AppLog.Debug($"tips 接口返回 {(int)response.StatusCode}，改用本地文案。");
                return LocalFallback();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var dto = await JsonSerializer
                .DeserializeAsync(stream, TipJsonContext.Default.TipResponse, cancellationToken)
                .ConfigureAwait(false);

            var text = dto?.Content?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                return LocalFallback();
            }

            AppLog.Debug($"tips 命中（{dto?.Source}）：{text}");
            return text;
        }
        catch (Exception ex)
        {
            AppLog.Debug($"取 tips 失败，改用本地文案：{ex.Message}");
            return LocalFallback();
        }
    }

    private static string LocalFallback()
    {
        var key = FallbackKeys[Random.Shared.Next(FallbackKeys.Length)];
        var text = Loc.T(key);

        // 语言包万一缺键，Loc.T 会回显键名；这时给个不空的最小兜底。
        return string.Equals(text, key, StringComparison.Ordinal) ? "…" : text;
    }
}
