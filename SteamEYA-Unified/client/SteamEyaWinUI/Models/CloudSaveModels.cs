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

    /// <summary>供列表显示的可读大小。</summary>
    public string SizeText => FormatSize(Size);

    /// <summary>是否共享的徽标可见性。</summary>
    public Visibility SharedBadgeVisibility => Shared ? Visibility.Visible : Visibility.Collapsed;

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

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(CloudSaveListDto))]
[JsonSerializable(typeof(CloudSaveUploadDto))]
[JsonSerializable(typeof(CloudSaveActionDto))]
[JsonSerializable(typeof(CloudSaveShareRequest))]
[JsonSerializable(typeof(CloudSaveRemarkRequest))]
[JsonSerializable(typeof(CloudSaveDeleteRequest))]
internal sealed partial class CloudSaveJsonContext : JsonSerializerContext;