using System.Text.Json;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 自定义云存档游戏的本地清单，落在 <c>%AppData%\SteamEYA\custom-saves.json</c>。
/// 只做本地读写：服务端只记游戏名，存档目录不联网。
/// </summary>
internal static class CustomSaveStore
{
    private static readonly object Gate = new();
    private static List<CustomSaveGame>? _cache;

    public static string FilePath =>
        Path.Combine(AppState.SettingsService.AppFolderPath, "custom-saves.json");

    /// <summary>读取清单；文件不存在/损坏时返回空列表（不抛异常，页面照常可用）。</summary>
    public static List<CustomSaveGame> Load()
    {
        lock (Gate)
        {
            if (_cache is not null)
            {
                return _cache;
            }

            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var doc = JsonSerializer.Deserialize(json, CustomSaveJsonContext.Default.CustomSaveDocument);
                    _cache = doc?.Games ?? [];
                }
                else
                {
                    _cache = [];
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"读取自定义云存档清单失败：{ex.Message}");
                _cache = [];
            }

            return _cache;
        }
    }

    public static void Save(List<CustomSaveGame> games)
    {
        lock (Gate)
        {
            _cache = games;

            try
            {
                Directory.CreateDirectory(AppState.SettingsService.AppFolderPath);
                var json = JsonSerializer.Serialize(
                    new CustomSaveDocument { Games = games }, CustomSaveJsonContext.Default.CustomSaveDocument);

                // 先写临时文件再替换：中途崩溃不会留下半个 JSON 把清单读废。
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                AppLog.Error("保存自定义云存档清单失败。", ex);
            }
        }
    }

    /// <summary>按 key 找自定义游戏；没命中返回 null。</summary>
    public static CustomSaveGame? Find(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return Load().FirstOrDefault(g => string.Equals(g.Key, key, StringComparison.Ordinal));
    }

    /// <summary>按名字找，用于「同名复用」判断。</summary>
    public static CustomSaveGame? FindByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return Load().FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public static void Upsert(CustomSaveGame game)
    {
        var games = Load().ToList();
        var index = games.FindIndex(g => string.Equals(g.Key, game.Key, StringComparison.Ordinal));

        if (index >= 0)
        {
            games[index] = game;
        }
        else
        {
            games.Add(game);
        }

        Save(games);
    }

    public static void Remove(string key)
    {
        var games = Load().Where(g => !string.Equals(g.Key, key, StringComparison.Ordinal)).ToList();
        Save(games);
    }
}
