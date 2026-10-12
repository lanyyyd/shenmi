using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamEyaWinUI.Models;

/// <summary>聊天室用户（GET /api/users）。</summary>
internal sealed class ChatUserDto
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public string Avatar { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool Online { get; set; }

    /// <summary>最后活跃时间（毫秒）：登录 / 在线 / 发言三者取最大；0 表示从未登录过。</summary>
    public long LastSeen { get; set; }
}

internal sealed class ChatUsersResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public ChatUserDto[]? Users { get; set; }
}

/// <summary>
/// 聊天消息。历史接口用 <c>type</c> 字段，实时推送用 <c>msgType</c>，两者都映射到同名属性，
/// 展示统一读 <see cref="EffectiveKind"/>。
/// </summary>
internal sealed class ChatMessageDto
{
    public long Id { get; set; }
    public string Room { get; set; } = "group";
    public int FromId { get; set; }
    public string FromName { get; set; } = string.Empty;
    public string FromAvatar { get; set; } = string.Empty;
    public int? ToId { get; set; }
    public string? Type { get; set; }
    public string? MsgType { get; set; }
    public string Content { get; set; } = string.Empty;
    public JsonElement? Mentions { get; set; }
    public string? FilePath { get; set; }
    public string? FileName { get; set; }
    public long? FileSize { get; set; }
    public bool Recalled { get; set; }
    public long CreatedAt { get; set; }

    /// <summary>归一化后的消息类型（text/image/video/file/sticker）。</summary>
    public string EffectiveKind
    {
        get
        {
            var kind = !string.IsNullOrEmpty(MsgType) ? MsgType : Type;
            return kind switch
            {
                "image" or "video" or "file" or "sticker" => kind,
                _ => "text"
            };
        }
    }
}

internal sealed class ChatHistoryResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public ChatMessageDto[]? Messages { get; set; }
    public bool HasMore { get; set; }
}

/// <summary>POST /api/upload 的返回。</summary>
internal sealed class ChatUploadResultDto
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public bool IsImage { get; set; }
    public bool IsVideo { get; set; }
    public int Code { get; set; }
}

internal sealed class ChatUnreadResponse
{
    public bool Ok { get; set; }
    public Dictionary<string, int>? Unread { get; set; }
    public bool MentionGroup { get; set; }
}

/// <summary>GET /api/me 的返回（取当前用户 id 用于私聊房间号与「我的消息」判定）。</summary>
internal sealed class ChatMeResponse
{
    public bool Ok { get; set; }
    public ChatUserDto? User { get; set; }
}

// ---------------- 局域网互传（/api/lan/*） ----------------

internal sealed class LanInfoDto
{
    public bool Ok { get; set; }
    public bool Enabled { get; set; }

    [JsonPropertyName("lan_mode")]
    public bool LanMode { get; set; }

    public int? Port { get; set; }
    public string Name { get; set; } = string.Empty;
}

internal sealed class LanPeerDto
{
    public string Ip { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Port { get; set; }
    public long LastSeen { get; set; }
}

internal sealed class LanPeersResponse
{
    public bool Ok { get; set; }
    public bool Enabled { get; set; }
    public LanPeerDto[]? Peers { get; set; }
    public string? Msg { get; set; }
}

internal sealed class LanAddPeerResponse
{
    public bool Ok { get; set; }
    public LanPeerDto? Peer { get; set; }
    public string? Msg { get; set; }
}

/// <summary>POST /api/lan/peers/add 的请求体。</summary>
internal sealed class LanAddPeerRequest
{
    public string Ip { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>WebSocket 发送：聊天消息。</summary>
internal sealed class ChatSendDto
{
    public string Type { get; set; } = "chat";
    public string MsgType { get; set; } = "text";
    public string Content { get; set; } = string.Empty;
    public ChatFileDto? File { get; set; }
    public int? To { get; set; }
    public string[]? Mentions { get; set; }
}

/// <summary>随消息发送的文件引用（服务器要求 url/name/size）。</summary>
internal sealed class ChatFileDto
{
    public string Url { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
}

/// <summary>WebSocket 发送：撤回消息。</summary>
internal sealed class ChatRecallDto
{
    public string Type { get; set; } = "recall";
    public long Id { get; set; }
}

/// <summary>WebSocket 发送：心跳。</summary>
internal sealed class ChatPingDto
{
    public string Type { get; set; } = "ping";
}

/// <summary>POST /api/read 的请求体。</summary>
internal sealed class ChatReadRequest
{
    public string Room { get; set; } = "group";
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ChatReadRequest))]
[JsonSerializable(typeof(ChatSendDto))]
[JsonSerializable(typeof(ChatFileDto))]
[JsonSerializable(typeof(ChatRecallDto))]
[JsonSerializable(typeof(ChatPingDto))]
[JsonSerializable(typeof(ChatMessageDto))]
[JsonSerializable(typeof(ChatUsersResponse))]
[JsonSerializable(typeof(ChatHistoryResponse))]
[JsonSerializable(typeof(ChatUploadResultDto))]
[JsonSerializable(typeof(ChatUnreadResponse))]
[JsonSerializable(typeof(ChatMeResponse))]
[JsonSerializable(typeof(LanInfoDto))]
[JsonSerializable(typeof(LanPeersResponse))]
[JsonSerializable(typeof(LanAddPeerResponse))]
[JsonSerializable(typeof(LanAddPeerRequest))]
internal sealed partial class ChatJsonContext : JsonSerializerContext;
