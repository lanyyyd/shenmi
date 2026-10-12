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
    public string? AccessToken { get; set; }
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
}

internal sealed class LocalLoginRequestDto
{
    public string Nickname { get; set; } = string.Empty;
}

internal sealed class CloudGameItem
{
    public string Game { get; set; } = string.Empty;
    public long UpdatedAt { get; set; }
    public int? Valid { get; set; }
    public long CheckedAt { get; set; }
    public string Nickname { get; set; } = string.Empty;
    public string GameUid { get; set; } = string.Empty;
    public int? FreeDuration { get; set; }

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

    public string DurationText => FreeDuration is null ? string.Empty : $"剩余免费 {FreeDuration} 分钟";
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