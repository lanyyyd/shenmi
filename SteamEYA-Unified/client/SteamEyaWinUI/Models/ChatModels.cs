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

    /// <summary>等级（0–6），侧栏与消息发送人旁的徽章。</summary>
    public int Level { get; set; }

    /// <summary>经验值（用于个人资料里的等级卡）。</summary>
    public long Exp { get; set; }

    /// <summary>是否是小火人（AI）账号。</summary>
    public bool IsAi { get; set; }

    /// <summary>当前账号是否是管理员（决定撤回样式与表情删除按钮）。</summary>
    public bool IsAdmin { get; set; }
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

    /// <summary>发送人等级（群聊消息名称旁显示）。</summary>
    public int FromLevel { get; set; }

    /// <summary>所属会话的显示名（搜索结果里展示，群聊为「群聊大厅」）。</summary>
    public string? RoomName { get; set; }

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

// ---------------- 局域网点对点直传（/api/lan/tr/*） ----------------

/// <summary>POST /api/lan/tr/begin 的请求体：建立一次传输。</summary>
internal sealed class LanTransferBeginRequest
{
    public string TransferId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long Size { get; set; }
    public string FromName { get; set; } = string.Empty;
    public string FromIp { get; set; } = string.Empty;
}

internal sealed class LanTransferBeginResponse
{
    public bool Ok { get; set; }
    public string? Id { get; set; }
    public string? Msg { get; set; }
}

internal sealed class LanTransferFinishResponse
{
    public bool Ok { get; set; }
    public string? Name { get; set; }
    public string? Path { get; set; }
    public string? Dir { get; set; }
    public string? Msg { get; set; }
}

/// <summary>接收端进行中的传输（GET /api/lan/tr/inbox）。</summary>
internal sealed class LanTransferActiveDto
{
    public string Id { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long Size { get; set; }
    public long Written { get; set; }
    public string From { get; set; } = string.Empty;
    public string FromIp { get; set; } = string.Empty;
}

/// <summary>接收端已完成的文件。</summary>
internal sealed class LanTransferReceivedDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public string From { get; set; } = string.Empty;
    public string FromIp { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public long FinishedAt { get; set; }
}

internal sealed class LanTransferInboxResponse
{
    public bool Ok { get; set; }
    public bool Enabled { get; set; }
    public string Dir { get; set; } = string.Empty;
    public LanTransferActiveDto[]? Active { get; set; }
    public LanTransferReceivedDto[]? Received { get; set; }
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

    /// <summary>表情包消息只发这个 id，由服务端回填 file 字段。</summary>
    public long? StickerId { get; set; }
}

/// <summary>GET /api/stickers 里的单个表情包。</summary>
internal sealed class ChatStickerDto
{
    public long Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public int SendCount { get; set; }
}

internal sealed class ChatStickersResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public ChatStickerDto[]? Stickers { get; set; }
    public int Total { get; set; }
    public bool IsAdmin { get; set; }
}

/// <summary>POST /api/stickers 的返回。</summary>
internal sealed class ChatStickerUploadResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public ChatStickerDto? Sticker { get; set; }
    public bool Dedup { get; set; }
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

// ---------------- 已读回执（GET /api/read-state） ----------------

internal sealed class ChatReadStateDto
{
    public int UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public long LastReadId { get; set; }
    public long UpdatedAt { get; set; }
}

internal sealed class ChatReadStateResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public ChatReadStateDto[]? States { get; set; }
}

// ---------------- 聊天记录搜索（GET /api/search） ----------------

internal sealed class ChatSearchResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public ChatMessageDto[]? Messages { get; set; }
    public int Count { get; set; }
    public bool HasMore { get; set; }
    public int NextOffset { get; set; }
}

// ---------------- 个人资料（POST /api/profile、/api/profile/avatar） ----------------

internal sealed class ChatProfileRequest
{
    public string? Nickname { get; set; }
    public string? OldPassword { get; set; }
    public string? NewPassword { get; set; }
}

internal sealed class ChatProfileResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Avatar { get; set; }
    public string? Nickname { get; set; }
    public ChatUserDto? User { get; set; }
}

// ---------------- 联系人备注 / 置顶（GET|POST /api/contacts） ----------------

internal sealed class ChatContactsResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public Dictionary<string, string>? Notes { get; set; }
    public int[]? Pinned { get; set; }
    public int AiId { get; set; }
}

internal sealed class ChatContactsNoteRequest
{
    public int PeerId { get; set; }
    public string Note { get; set; } = string.Empty;
}

internal sealed class ChatContactsPinRequest
{
    public int PeerId { get; set; }
    public bool Pinned { get; set; }
}

/// <summary>通用 {ok,error} 返回（删除表情、备注、置顶等）。</summary>
internal sealed class ChatSimpleResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
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
[JsonSerializable(typeof(ChatStickersResponse))]
[JsonSerializable(typeof(ChatStickerUploadResponse))]
[JsonSerializable(typeof(ChatReadStateResponse))]
[JsonSerializable(typeof(ChatReadStateDto))]
[JsonSerializable(typeof(ChatSearchResponse))]
[JsonSerializable(typeof(ChatProfileRequest))]
[JsonSerializable(typeof(ChatProfileResponse))]
[JsonSerializable(typeof(ChatContactsResponse))]
[JsonSerializable(typeof(ChatContactsNoteRequest))]
[JsonSerializable(typeof(ChatContactsPinRequest))]
[JsonSerializable(typeof(ChatSimpleResponse))]
[JsonSerializable(typeof(LanInfoDto))]
[JsonSerializable(typeof(LanPeersResponse))]
[JsonSerializable(typeof(LanAddPeerResponse))]
[JsonSerializable(typeof(LanAddPeerRequest))]
[JsonSerializable(typeof(LanTransferBeginRequest))]
[JsonSerializable(typeof(LanTransferBeginResponse))]
[JsonSerializable(typeof(LanTransferFinishResponse))]
[JsonSerializable(typeof(LanTransferActiveDto))]
[JsonSerializable(typeof(LanTransferReceivedDto))]
[JsonSerializable(typeof(LanTransferInboxResponse))]
internal sealed partial class ChatJsonContext : JsonSerializerContext;
