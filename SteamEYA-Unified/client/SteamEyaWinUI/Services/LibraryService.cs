using System.Net.Http;
using System.Text.Json;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

/// <summary>一次加载的结果，含来源与失败原因，供页面决定提示条怎么显示。</summary>
internal sealed class LibrarySnapshot
{
    public IReadOnlyList<LibraryResourceItem> Items { get; init; } = [];

    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>这批数据是什么时候从站点同步下来的（缓存时是旧时间）。</summary>
    public DateTimeOffset? SyncedAt { get; init; }

    /// <summary>true 表示本次没连上站点，用的是本地缓存。</summary>
    public bool FromCache { get; init; }

    /// <summary>同步失败的原因；成功时为 null。</summary>
    public string? Error { get; init; }
}

/// <summary>
/// 资源库数据源（t.teatt.top）。
///
/// 站点 <c>api.php</c> 是给网页用的公开接口，返回 JSON；客户端直接请求不受 CORS 约束
/// （CORS 只约束浏览器）。列表与分类都带 <c>?t=时间戳</c> 防缓存。
///
/// 三级缓存：内存 5 分钟 → 磁盘 library-cache.json → 都没有就报错。
/// 这样站点挂了或断网时，用户仍能看到上次的资源列表。
/// </summary>
internal static class LibraryService
{
    public const string SiteUrl = "https://t.teatt.top/";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    private static readonly TimeSpan MemoryTtl = TimeSpan.FromMinutes(5);
    private static LibrarySnapshot? _cached;
    private static DateTimeOffset _cachedAt;

    private static string CachePath =>
        Path.Combine(AppState.SettingsService.AppFolderPath, "library-cache.json");

    /// <summary>加载资源库。<paramref name="forceRefresh"/> 为 true 时跳过内存缓存。</summary>
    public static async Task<LibrarySnapshot> LoadAsync(bool forceRefresh, CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && _cached is not null && DateTimeOffset.Now - _cachedAt < MemoryTtl)
        {
            return _cached;
        }

        LibraryCacheDocument? cache = null;
        try
        {
            cache = await ReadCacheAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Debug($"读取资源库缓存失败：{ex.Message}");
        }

        try
        {
            var (entries, categories) = await FetchAsync(cancellationToken).ConfigureAwait(false);

            var snapshot = new LibrarySnapshot
            {
                Items = entries.Select(entry => new LibraryResourceItem(entry)).ToArray(),
                Categories = categories,
                SyncedAt = DateTimeOffset.Now,
                FromCache = false
            };

            _cached = snapshot;
            _cachedAt = DateTimeOffset.Now;
            AppLog.Debug($"资源库同步成功：{snapshot.Items.Count} 条，{categories.Count} 个分类。");

            await WriteCacheAsync(
                new LibraryCacheDocument
                {
                    SyncedAt = snapshot.SyncedAt!.Value.ToUnixTimeMilliseconds(),
                    Items = entries,
                    Categories = [.. categories]
                },
                cancellationToken).ConfigureAwait(false);

            return snapshot;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"资源库同步失败：{ex.Message}");

            if (cache is not null && cache.Items.Count > 0)
            {
                var fallback = new LibrarySnapshot
                {
                    Items = cache.Items.Select(entry => new LibraryResourceItem(entry)).ToArray(),
                    Categories = cache.Categories,
                    SyncedAt = DateTimeOffset.FromUnixTimeMilliseconds(cache.SyncedAt),
                    FromCache = true,
                    Error = ex.Message
                };

                _cached = fallback;
                _cachedAt = DateTimeOffset.Now;
                return fallback;
            }

            return new LibrarySnapshot { Error = ex.Message };
        }
    }

    private static async Task<(List<LibraryEntry> Entries, List<string> Categories)> FetchAsync(
        CancellationToken cancellationToken)
    {
        var stamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();

        var listTask = GetJsonAsync($"api.php?t={stamp}", LibraryJsonContext.Default.LibraryListResponse, cancellationToken);
        var categoryTask = GetJsonAsync($"api.php?action=categories&t={stamp}", LibraryJsonContext.Default.LibraryCategoryResponse, cancellationToken);

        await Task.WhenAll(listTask, categoryTask).ConfigureAwait(false);

        var list = await listTask.ConfigureAwait(false)
                   ?? throw new InvalidOperationException("资源列表接口没有返回有效数据");

        if (!string.Equals(list.Status, "success", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"资源列表接口返回 status={list.Status}");
        }

        // 只保留能用的条目：没有 url 的卡片点了也没意义。
        var entries = (list.Data ?? [])
            .Where(e => !string.IsNullOrWhiteSpace(e.Url))
            .ToList();

        var categories = (await categoryTask.ConfigureAwait(false))?.Data?
            .Where(c => !string.IsNullOrWhiteSpace(c.Category))
            .OrderByDescending(c => ParseSort(c.SortOrder))
            .Select(c => c.Category!)
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? [];

        return (entries, categories);
    }

    private static int ParseSort(string? value) =>
        int.TryParse(value, out var result) ? result : 0;

    private static async Task<T?> GetJsonAsync<T>(
        string relativePath,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
        where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, SiteUrl + relativePath);
        // 默认 UA 是 HttpClient 的空串，部分站点会直接 403。
        request.Headers.UserAgent.ParseAdd("TeaTT-Pocket/1.0");

        using var response = await Http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}");
        }

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        return await JsonSerializer
            .DeserializeAsync(stream, typeInfo, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<LibraryCacheDocument?> ReadCacheAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(CachePath))
        {
            return null;
        }

        await using var stream = File.OpenRead(CachePath);
        return await JsonSerializer
            .DeserializeAsync(stream, LibraryJsonContext.Default.LibraryCacheDocument, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteCacheAsync(LibraryCacheDocument document, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            await using var stream = File.Create(CachePath);
            await JsonSerializer
                .SerializeAsync(stream, document, LibraryJsonContext.Default.LibraryCacheDocument, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 缓存写失败不影响本次展示。
            AppLog.Debug($"写入资源库缓存失败：{ex.Message}");
        }
    }
}
