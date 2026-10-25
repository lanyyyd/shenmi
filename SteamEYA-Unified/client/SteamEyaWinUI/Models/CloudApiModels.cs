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

/// <summary>GET /api/invite-info：注册是否需要邀请码、是否还有可用码（与网页版注册共用同一开关）。</summary>
internal sealed class InviteInfoDto
{
    public bool Ok { get; set; }
    public bool Required { get; set; }
    public int Available { get; set; }
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

    /// <summary>服务端解析好的显示名（含用户自定义游戏）；老服务端不返回时为空。</summary>
    [JsonPropertyName("game_name")]
    public string GameNameFromServer { get; set; } = string.Empty;

    /// <summary>
    /// 显示名优先级：服务端 → 客户端已知列表（/api/cloud/games 拉到的）→ 内置硬编码 → key。
    /// 之前只有内置 switch，自定义游戏（key 形如 custom_xxxxxxxx）会直接把 key 显示给用户。
    /// </summary>
    public string GameName => !string.IsNullOrWhiteSpace(GameNameFromServer)
        ? GameNameFromServer
        : Services.CloudGameService.DisplayName(Game);

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

/// <summary>
/// 离线同步消息。服务端与本地网关均使用 snake_case 字段名（client_msg_id / to_username /
/// from_username / created_at），而 JSON 上下文用 camelCase 策略不会转换下划线，
/// 故必须显式映射，否则推送时缺少 install_id 之外的字段，同步会静默失败。
/// </summary>
internal sealed class SyncMessage
{
    [JsonPropertyName("client_msg_id")]
    public string ClientMsgId { get; set; } = string.Empty;

    public string Room { get; set; } = "group";

    [JsonPropertyName("to_username")]
    public string ToUsername { get; set; } = string.Empty;

    [JsonPropertyName("from_username")]
    public string FromUsername { get; set; } = string.Empty;

    public string Type { get; set; } = "text";
    public string Content { get; set; } = string.Empty;
    public JsonElement? Mentions { get; set; }
    public JsonElement? File { get; set; }

    [JsonPropertyName("created_at")]
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
    [JsonPropertyName("install_id")]
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
    [JsonPropertyName("install_id")]
    public string InstallId { get; set; } = string.Empty;

    [JsonPropertyName("client_msg_ids")]
    public string[] ClientMsgIds { get; set; } = [];
}

/// <summary>
/// 同步消息里的文件引用（url/name/size）。离线消息经本地网关上传，file.url 指向本机相对路径；
/// 同步时要先把文件本体转存到远端并改写成远端地址，故需要一个可序列化（AOT 源生成）的载体来重建 file。
/// </summary>
internal sealed class SyncFileRef
{
    public string Url { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
}

/// <summary>GET /api/update/latest 的返回（自有服务器更新源）。</summary>
internal sealed class UpdateLatestDto
{
    public bool Ok { get; set; }

    /// <summary>服务器是否已配置下发版本；false 时其余字段无意义。</summary>
    public bool Available { get; set; }

    public string Version { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>版本发布时间（毫秒时间戳）。</summary>
    public long UpdatedAt { get; set; }

    /// <summary>安装包下载地址（相对路径，如 /api/update/download/12）。</summary>
    public string Url { get; set; } = string.Empty;
}

/// <summary>云游戏列表项（内置 + 用户自定义）。</summary>
internal sealed class CloudGameItemDto
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public bool Builtin { get; set; }
    public bool Visible { get; set; }
    public bool Mine { get; set; }
}

internal sealed class CloudGamesDto
{
    public bool Ok { get; set; }
    public List<CloudGameItemDto>? Games { get; set; }
}

internal sealed class CloudCreateGameDto
{
    public bool Ok { get; set; }
    public string? Key { get; set; }
    public bool Existing { get; set; }
    public bool Visible { get; set; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(UserDto))]
[JsonSerializable(typeof(AuthResultDto))]
[JsonSerializable(typeof(AuthRequestDto))]
[JsonSerializable(typeof(InviteInfoDto))]
[JsonSerializable(typeof(LocalLoginRequestDto))]
[JsonSerializable(typeof(CloudMyDto))]
[JsonSerializable(typeof(CloudDownloadDto))]
[JsonSerializable(typeof(CloudActionResultDto))]
[JsonSerializable(typeof(CloudGamesDto))]
[JsonSerializable(typeof(CloudCreateGameDto))]
[JsonSerializable(typeof(SyncMessage))]
[JsonSerializable(typeof(SyncPendingDto))]
[JsonSerializable(typeof(SyncPushRequest))]
[JsonSerializable(typeof(SyncPushResultDto))]
[JsonSerializable(typeof(SyncMarkRequest))]
[JsonSerializable(typeof(SyncFileRef))]
[JsonSerializable(typeof(UpdateLatestDto))]
internal sealed partial class CloudApiJsonContext : JsonSerializerContext;