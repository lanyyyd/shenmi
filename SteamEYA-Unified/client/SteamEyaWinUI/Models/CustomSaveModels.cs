using System.Text.Json.Serialization;

namespace SteamEyaWinUI.Models;

/// <summary>
/// 用户自定义的云存档游戏。名字会注册到服务端（别人也能看到这款游戏的共享存档），
/// 存档目录是纯本地信息，只存在客户端。
/// </summary>
internal sealed class CustomSaveGame
{
    /// <summary>服务端 game_key；服务端不可用时退回本地生成的 <c>local_xxxxxxxx</c>。</summary>
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>本机存档目录，自动备份/还原的目标。</summary>
    public string SaveDir { get; set; } = string.Empty;

    /// <summary>是否只在本地登记（服务端注册失败时为 true，上传会提示未知游戏）。</summary>
    public bool LocalOnly { get; set; }
}

internal sealed class CustomSaveDocument
{
    public List<CustomSaveGame> Games { get; set; } = [];
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(CustomSaveDocument))]
internal sealed partial class CustomSaveJsonContext : JsonSerializerContext;
