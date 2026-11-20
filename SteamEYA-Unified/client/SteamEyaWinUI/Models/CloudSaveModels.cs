using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;

namespace SteamEyaWinUI.Models;

/// <summary>云存档条目（对应服务端 /api/save/list、/api/save/shared 返回的单项）。</summary>
internal sealed class CloudSaveItem
{
    public int Id { get; set; }

    public string Game { get; set; } = string.Empty;

    /// <summary>显示名，默认按上传时间生成，如“2026年10月11日14:32”。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>备注；共享后“共享存档”里显示的就是上传者自己的备注。</summary>
    public string Remark { get; set; } = string.Empty;

    public long Size { get; set; }

    public string FileName { get; set; } = string.Empty;

    public bool Shared { get; set; }

    public long? SharedAt { get; set; }

    public long CreatedAt { get; set; }

    public int OwnerId { get; set; }

    /// <summary>共享列表中显示上传者；自己的列表为空。</summary>
    public string OwnerName { get; set; } = string.Empty;

    // ---- 我的世界模组上传的元数据（老存档为空） ----

    public string Source { get; set; } = "auto";

    public string GameVersion { get; set; } = string.Empty;

    public string VersionName { get; set; } = string.Empty;

    public string SaveName { get; set; } = string.Empty;

    public string SaveFolder { get; set; } = string.Empty;

    public string SavePath { get; set; } = string.Empty;

    /// <summary>备份时间；老存档退回上传时间。</summary>
    public long SavedAt { get; set; }

    /// <summary>供列表显示的可读大小。</summary>
    public string SizeText => FormatSize(Size);

    /// <summary>是否共享的徽标可见性。</summary>
    public Visibility SharedBadgeVisibility => Shared ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>我的世界列表里显示的版本信息。</summary>
    public string VersionText => string.IsNullOrWhiteSpace(VersionName) ? "未识别版本" : VersionName;

    public string GameVersionText => string.IsNullOrWhiteSpace(GameVersion) ? "" : GameVersion;

    public string SavedAtText => DateTimeOffset
        .FromUnixTimeMilliseconds(SavedAt > 0 ? SavedAt : CreatedAt)
        .ToLocalTime()
        .ToString("yyyy-MM-dd HH:mm");

    /// <summary>分组键：版本名 + 存档名。</summary>
    public string GroupKey => $"{VersionName}\u0001{SaveName}";

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var i = 0;
        while (value >= 1024 && i < units.Length - 1)
        {
            value /= 1024;
            i++;
        }
        return i == 0 ? $"{bytes} B" : $"{value:0.#} {units[i]}";
    }
}

internal sealed class CloudSaveListDto
{
    public bool Ok { get; set; }
    public CloudSaveItem[]? Saves { get; set; }
    public string? Msg { get; set; }
}

/// <summary>我的世界存档组：同一个版本名 + 存档名的多次备份折叠显示。</summary>
internal sealed class CloudSaveGroup
{
    public string SaveName { get; set; } = string.Empty;

    public string VersionName { get; set; } = string.Empty;

    public string GameVersion { get; set; } = string.Empty;

    /// <summary>按备份时间倒序，第一项是最新存档。</summary>
    public List<CloudSaveItem> Items { get; set; } = [];

    public CloudSaveItem Latest => Items[0];

    public string Title => string.IsNullOrWhiteSpace(SaveName) ? "未命名存档" : SaveName;

    public string Subtitle
    {
        get
        {
            var version = string.IsNullOrWhiteSpace(VersionName) ? "未识别版本" : VersionName;
            return string.IsNullOrWhiteSpace(GameVersion)
                ? version
                : $"{version} · {GameVersion}";
        }
    }

    public string TimeText => Latest.SavedAtText;

    public string SizeText => Latest.SizeText;

    public string CountText => $"{Items.Count} 份备份";
}

internal sealed class CloudSaveUploadDto
{
    public bool Ok { get; set; }
    public CloudSaveItem? Save { get; set; }
    public string? Msg { get; set; }
}

internal sealed class CloudSaveActionDto
{
    public bool Ok { get; set; }
    public string? Msg { get; set; }
    public int Changed { get; set; }
    public int Removed { get; set; }
}

internal sealed class CloudSaveDownloadUrlDto
{
    public bool Ok { get; set; }
    public string? Url { get; set; }
    public long Expires { get; set; }
    public string? Msg { get; set; }
}

internal sealed class CloudSaveShareRequest
{
    public int[] Ids { get; set; } = [];
    public bool Shared { get; set; } = true;
}

internal sealed class CloudSaveRemarkRequest
{
    public int Id { get; set; }
    public string Remark { get; set; } = string.Empty;
}

internal sealed class CloudSaveDeleteRequest
{
    public int[] Ids { get; set; } = [];
}

internal sealed class CloudSaveGameDto
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>创建者填的存档目录，随游戏下发，别人还原时用同一路径。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("save_dir")]
    public string SaveDir { get; set; } = string.Empty;

    public bool Builtin { get; set; }
    public bool Visible { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("mine")]
    public bool Mine { get; set; }
}

internal sealed class CloudSaveGamesDto
{
    public bool Ok { get; set; }
    public List<CloudSaveGameDto>? Games { get; set; }
}

internal sealed class CloudSaveRegisterDto
{
    public bool Ok { get; set; }
    public string? Key { get; set; }
    public string? Name { get; set; }
    public bool Existing { get; set; }
    public string? Msg { get; set; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(CloudSaveListDto))]
[JsonSerializable(typeof(CloudSaveUploadDto))]
[JsonSerializable(typeof(CloudSaveActionDto))]
[JsonSerializable(typeof(CloudSaveShareRequest))]
[JsonSerializable(typeof(CloudSaveRemarkRequest))]
[JsonSerializable(typeof(CloudSaveDeleteRequest))]
[JsonSerializable(typeof(CloudSaveGamesDto))]
[JsonSerializable(typeof(CloudSaveRegisterDto))]
[JsonSerializable(typeof(CloudSaveDownloadUrlDto))]
internal sealed partial class CloudSaveJsonContext : JsonSerializerContext;