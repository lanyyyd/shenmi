using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamEyaWinUI.Models;

internal sealed class UserDto
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public string Avatar { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
}

/// <summary>统一账号登录/注册/离线登录的返回。</summary>
internal sealed class AuthResultDto
{
    // 服务端返回下划线命名（access_token / token_type），而 JSON 上下文用 camelCase 策略，
    // 不会把下划线转成驼峰——必须显式映射，否则 AccessToken 永远反序列化为 null，
    // 登录流程会因 result.AccessToken is null 而弹出兜底文案「操作失败」。
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    public UserDto? User { get; set; }
    public string? Msg { get; set; }
    public string? Detail { get; set; }
}

internal sealed class AuthRequestDto
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? Nickname { get; set; }

    /// <summary>注册邀请码（与网页版注册共用；后台未开启必填时可留空）。</summary>
    public string? InviteCode { get; set; }
}

internal sealed class LocalLoginRequestDto
{
    public string Nickname { get; set; } = string.Empty;
}

/// <summary>
/// 云游戏账号状态（GET /api/cloud/my）。服务端直接透出数据库列名（snake_case），
/// 而 JSON 上下文用 camelCase 策略不会转换下划线，故 snake_case 字段必须显式映射，
/// 否则 FreeDuration 等恒为 null（剩余免费时长就不显示）。
/// </summary>
internal sealed class CloudGameItem
{
    public string Game { get; set; } = string.Empty;

    [JsonPropertyName("updated_at")]
    public long UpdatedAt { get; set; }

    public int? Valid { get; set; }

    [JsonPropertyName("checked_at")]
    public long CheckedAt { get; set; }

    public string Nickname { get; set; } = string.Empty;

    [JsonPropertyName("game_uid")]
    public string GameUid { get; set; } = string.Empty;

    /// <summary>剩余免费时长（分钟）；null 表示未查询到（未检测过 / 该游戏无此概念）。</summary>
    [JsonPropertyName("free_duration")]
    public int? FreeDuration { get; set; }

    /// <summary>剩余总时长（分钟，完美世界类游戏才有）。</summary>
    [JsonPropertyName("total_duration")]
    public int? TotalDuration { get; set; }

    public string GameName => Game switch
    {
        "genshin" => "原神",
        "starrail" => "星穹铁道",
        "yihuan" => "异环",
        "bilibili" => "哔哩哔哩",
        _ => Game
    };

    public string StatusText => Valid switch
    {
        1 => "Cookie 有效",
        0 => "Cookie 失效",
        _ => "未检测"
    };

    public string InfoText => string.IsNullOrEmpty(Nickname) && string.IsNullOrEmpty(GameUid)
        ? "暂无账号信息"
        : $"{Nickname} {GameUid}".Trim();

    public string DurationText => FreeDuration is not null
        ? $"剩余免费 {FreeDuration} 分钟"
        : (TotalDuration is not null ? $"剩余时长 {TotalDuration} 分钟" : string.Empty);
}

internal sealed class CloudMyDto
{
    public bool Ok { get; set; }
    public CloudGameItem[]? Games { get; set; }
}

internal sealed class CloudDownloadDto
{
    public bool Ok { get; set; }
    public JsonElement? Cookies { get; set; }

    [JsonPropertyName("combo_token")]
    public string? ComboToken { get; set; }

    public string? Msg { get; set; }
}

internal sealed class CloudActionResultDto
{
    public bool Ok { get; set; }
    public string? Msg { get; set; }
    public string? Detail { get; set; }
    public bool? Valid { get; set; }
    public string? Nickname { get; set; }

    /// <summary>检测（/api/cloud/check）随结果一并返回的剩余免费时长（分钟）。</summary>
    [JsonPropertyName("free_duration")]
    public int? FreeDuration { get; set; }

    [JsonPropertyName("total_duration")]
    public int? TotalDuration { get; set; }
}

internal sealed class SyncMessage
{
    public string ClientMsgId { get; set; } = string.Empty;
    public string Room { get; set; } = "group";
    public string ToUsername { get; set; } = string.Empty;
    public string FromUsername { get; set; } = string.Empty;
    public string Type { get; set; } = "text";
    public string Content { get; set; } = string.Empty;
    public JsonElement? Mentions { get; set; }
    public JsonElement? File { get; set; }
    public long CreatedAt { get; set; }
}

internal sealed class SyncPendingDto
{
    public bool Ok { get; set; }
    public SyncMessage[]? Messages { get; set; }
    public int Count { get; set; }
}

internal sealed class SyncPushRequest
{
    public string InstallId { get; set; } = string.Empty;
    public SyncMessage[] Messages { get; set; } = [];
}

internal sealed class SyncPushResultDto
{
    public bool Ok { get; set; }
    public int Accepted { get; set; }
    public int Skipped { get; set; }
    public string? Msg { get; set; }
}

internal sealed class SyncMarkRequest
{
    public string InstallId { get; set; } = string.Empty;
    public string[] ClientMsgIds { get; set; } = [];
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(UserDto))]
[JsonSerializable(typeof(AuthResultDto))]
[JsonSerializable(typeof(AuthRequestDto))]
[JsonSerializable(typeof(LocalLoginRequestDto))]
[JsonSerializable(typeof(CloudMyDto))]
[JsonSerializable(typeof(CloudDownloadDto))]
[JsonSerializable(typeof(CloudActionResultDto))]
[JsonSerializable(typeof(SyncMessage))]
[JsonSerializable(typeof(SyncPendingDto))]
[JsonSerializable(typeof(SyncPushRequest))]
[JsonSerializable(typeof(SyncPushResultDto))]
[JsonSerializable(typeof(SyncMarkRequest))]
internal sealed partial class CloudApiJsonContext : JsonSerializerContext;