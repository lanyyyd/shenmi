namespace SteamEyaWinUI.Services;

/// <summary>
/// 一条灵动岛通知。字段和 native/island/island.cpp 的协议一一对应。
/// </summary>
internal sealed class IslandNotice
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>顶部小字：聊天室里的发送者、下载来源等。</summary>
    public string Heading { get; set; } = string.Empty;

    /// <summary>主标题：聊天消息预览、文件名等。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>展开后的正文/补充说明。</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>头像 URL（相对路径会自动补服务器地址）。</summary>
    public string AvatarUrl { get; set; } = string.Empty;

    /// <summary>没有头像时的 Segoe MDL2 图标。</summary>
    public string Glyph { get; set; } = string.Empty;

    /// <summary>同一组通知在岛上合并（例如 chat:group）。</summary>
    public string CoalesceKey { get; set; } = string.Empty;

    /// <summary>点击后回传给客户端的动作，格式与系统通知的 argument 一致。</summary>
    public string Action { get; set; } = string.Empty;

    public int DurationMs { get; set; } = 9000;
}
