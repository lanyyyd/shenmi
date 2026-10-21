using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Serialization;

namespace SteamEyaWinUI.Models;

/// <summary>
/// 远程协助的消息类型。控制类走 UTF-8 JSON，媒体/输入类走定长二进制。
/// </summary>
internal enum AssistMessageType : byte
{
    Hello = 1,
    HelloAck = 2,
    Invite = 3,
    InviteAck = 4,
    InviteResult = 5,
    Video = 6,
    Input = 7,
    Ping = 9,
    Pong = 10,
    Bye = 11,
    Busy = 12,
    KeyframeRequest = 13,
}

/// <summary>邀请（申请）的生命周期状态。</summary>
internal enum AssistRequestStatus
{
    /// <summary>等待被控方处理（5 分钟倒计时中）。</summary>
    Pending,
    /// <summary>已被同意，会话进行中或已结束。</summary>
    Accepted,
    /// <summary>被拒绝。</summary>
    Rejected,
    /// <summary>超过 5 分钟未处理。</summary>
    Expired,
    /// <summary>主控方主动取消。</summary>
    Cancelled,
    /// <summary>连接异常/对方离线等。</summary>
    Failed,
}

// ---------------- 控制通道 DTO（AOT：全部走下面的 source-gen context） ----------------

internal sealed class AssistHello
{
    public int V { get; set; } = 1;
    public string Uid { get; set; } = string.Empty;
    public string Nick { get; set; } = string.Empty;
    public string Fp { get; set; } = string.Empty;
    public int AssistPort { get; set; }
}

internal sealed class AssistHelloAck
{
    public string Uid { get; set; } = string.Empty;
    public string Nick { get; set; } = string.Empty;
    /// <summary>被控端采集区域的物理像素尺寸与 DPI 缩放，供主控端做坐标映射。</summary>
    public int Width { get; set; }
    public int Height { get; set; }
    public float DpiScale { get; set; } = 1f;
}

internal sealed class AssistInviteMessage
{
    public string RequestId { get; set; } = string.Empty;
    public string FromUid { get; set; } = string.Empty;
    public string FromNick { get; set; } = string.Empty;
    public string Mode { get; set; } = "control";
}

internal sealed class AssistInviteAck
{
    public string RequestId { get; set; } = string.Empty;
    /// <summary>Unix 毫秒；被控端承诺的失效时刻（申请时间 + 5 分钟）。</summary>
    public long ExpiresAt { get; set; }
}

internal sealed class AssistInviteResult
{
    public string RequestId { get; set; } = string.Empty;
    public bool Accepted { get; set; }
    public string? Reason { get; set; }
    public string? Token { get; set; }
}

internal sealed class AssistBusy
{
    public string Reason { get; set; } = "in_session";
}

internal sealed class AssistBye
{
    public string Reason { get; set; } = "bye";
}

internal sealed class AssistKeyframeRequest
{
    public string Reason { get; set; } = "loss";
}

/// <summary>落盘的邀请记录（申请列表的数据源）。</summary>
internal sealed class AssistRequestRecord
{
    public string RequestId { get; set; } = string.Empty;
    public string FromUid { get; set; } = string.Empty;
    public string FromNick { get; set; } = string.Empty;
    public string FromIp { get; set; } = string.Empty;
    public long CreatedAt { get; set; }
    public long ExpiresAt { get; set; }
    public AssistRequestStatus Status { get; set; }
    public bool Inbound { get; set; } = true;
}

internal sealed class AssistRequestFile
{
    public List<AssistRequestRecord> Items { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AssistHello))]
[JsonSerializable(typeof(AssistHelloAck))]
[JsonSerializable(typeof(AssistInviteMessage))]
[JsonSerializable(typeof(AssistInviteAck))]
[JsonSerializable(typeof(AssistInviteResult))]
[JsonSerializable(typeof(AssistBusy))]
[JsonSerializable(typeof(AssistBye))]
[JsonSerializable(typeof(AssistKeyframeRequest))]
[JsonSerializable(typeof(AssistRequestFile))]
internal partial class AssistJsonContext : JsonSerializerContext
{
}
