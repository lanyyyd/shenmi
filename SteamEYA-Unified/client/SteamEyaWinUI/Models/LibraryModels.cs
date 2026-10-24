using System.ComponentModel;
using System.Text.Json.Serialization;
using SteamEyaWinUI.Localization;

namespace SteamEyaWinUI.Models;

// ---------------- 服务端（t.teatt.top / api.php）数据契约 ----------------

/// <summary>资源库列表接口 <c>api.php?t=&lt;时间戳&gt;</c> 的单条记录。</summary>
internal sealed class LibraryEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>下载直链（一般指向 xiazai.teatt.top，公开可下、支持 Range）。</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>Font Awesome 类名，如 fa-download；客户端映射成系统图标。</summary>
    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    /// <summary>显示名。为空时用 title。</summary>
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }
}

internal sealed class LibraryCategoryEntry
{
    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("sort_order")]
    public string? SortOrder { get; set; }
}

internal sealed class LibraryListResponse
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("data")]
    public List<LibraryEntry>? Data { get; set; }
}

internal sealed class LibraryCategoryResponse
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("data")]
    public List<LibraryCategoryEntry>? Data { get; set; }
}

/// <summary>落盘缓存：站点打不开或断网时用上一次的成功结果。</summary>
internal sealed class LibraryCacheDocument
{
    [JsonPropertyName("syncedAt")]
    public long SyncedAt { get; set; }

    [JsonPropertyName("items")]
    public List<LibraryEntry> Items { get; set; } = [];

    [JsonPropertyName("categories")]
    public List<string> Categories { get; set; } = [];
}

// ---------------- 视图模型 ----------------

/// <summary>卡片上的下载按钮状态。</summary>
public enum LibraryDownloadState
{
    /// <summary>可以点「下载」。</summary>
    Idle,

    /// <summary>已经在百宝箱的下载队列里（含正在下载）。</summary>
    InQueue
}

/// <summary>
/// 资源卡片。字段都在构造时算好，避免绑定层反复做字符串拼装；
/// 只有下载状态会变，因此只对它触发通知。
/// </summary>
public sealed partial class LibraryResourceItem : INotifyPropertyChanged
{
    private LibraryDownloadState _state;

    internal LibraryResourceItem(LibraryEntry entry)
    {
        Id = entry.Id ?? string.Empty;
        Title = string.IsNullOrWhiteSpace(entry.DisplayName) ? (entry.Title ?? string.Empty) : entry.DisplayName!;
        Url = entry.Url ?? string.Empty;
        Category = string.IsNullOrWhiteSpace(entry.Category) ? string.Empty : entry.Category!;
        Glyph = MapGlyph(entry.Icon);

        var day = FormatDay(entry.CreatedAt);
        DetailText = string.IsNullOrEmpty(Category) ? day : (string.IsNullOrEmpty(day) ? Category : $"{Category} · {day}");
    }

    public string Id { get; }

    public string Title { get; }

    public string Url { get; }

    public string Category { get; }

    /// <summary>「分类 · MM-DD」。</summary>
    public string DetailText { get; }

    /// <summary>Segoe Fluent Icons 字形。</summary>
    public string Glyph { get; }

    public LibraryDownloadState State => _state;

    public string ActionText => _state == LibraryDownloadState.InQueue
        ? Loc.T("Lib_Action_Queued")
        : Loc.T("Lib_Action_Download");

    public bool CanDownload => _state == LibraryDownloadState.Idle;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void SetState(LibraryDownloadState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActionText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanDownload)));
    }

    /// <summary>语言切换后刷新按钮文案。</summary>
    internal void RefreshLocalizedText() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActionText)));

    private static string FormatDay(string? createdAt)
    {
        // 站点给的是 "2026-10-20 21:17:31"；取 MM-DD 即可，卡片上没空间放年份。
        if (string.IsNullOrWhiteSpace(createdAt) || createdAt.Length < 10)
        {
            return string.Empty;
        }

        return createdAt.Substring(5, 5);
    }

    /// <summary>站点存的是 Font Awesome 类名，映射到 Segoe Fluent Icons。</summary>
    private static string MapGlyph(string? icon)
    {
        var name = (icon ?? string.Empty).ToLowerInvariant();

        if (name.Contains("gamepad") || name.Contains("game")) return "\uE7FC";   // Game
        if (name.Contains("video") || name.Contains("film") || name.Contains("play")) return "\uE714"; // Video
        if (name.Contains("music") || name.Contains("audio")) return "\uE8D6";    // MusicInfo
        if (name.Contains("image") || name.Contains("photo")) return "\uEB9F";    // Photo
        if (name.Contains("folder") || name.Contains("archive")) return "\uE8B7"; // Folder
        if (name.Contains("book") || name.Contains("file-text")) return "\uE8A5"; // Document
        if (name.Contains("download") || name.Contains("cloud")) return "\uE896"; // Download
        if (name.Contains("link")) return "\uE71B";                              // Link
        return "\uE8A5";                                                          // Document（默认）
    }
}

/// <summary>源生成 JSON 上下文（项目禁用反射式序列化，AOT 下必须走这里）。</summary>
[JsonSerializable(typeof(LibraryListResponse))]
[JsonSerializable(typeof(LibraryCategoryResponse))]
[JsonSerializable(typeof(LibraryCacheDocument))]
internal sealed partial class LibraryJsonContext : JsonSerializerContext;
