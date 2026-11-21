using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SteamEyaWinUI.Services;

/// <summary>本机扫描到的一个我的世界版本实例。</summary>
internal sealed class McInstanceInfo
{
    public string Name { get; init; } = "";
    public string McVersion { get; init; } = "";
    public string Loader { get; init; } = "vanilla";
    /// <summary>versions 下的版本目录（含版本隔离时的 mods/config/saves）。</summary>
    public string InstanceDir { get; init; } = "";
    /// <summary>实际游戏目录：版本隔离用 InstanceDir，否则用 .minecraft 根目录。</summary>
    public string GameDir { get; init; } = "";
    public bool Isolated { get; init; }
    public string SavesDir => Path.Combine(GameDir, "saves");
    public int Score { get; set; }
}

/// <summary>
/// 按整合包名 + 游戏版本在本机搜索存档实例。
/// 备份记录的绝对路径换台电脑就会失效，这里改为在常见 .minecraft 目录里查找同名实例。
/// </summary>
internal static class MinecraftLocator
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TeaTT", "minecraft-instances.json");

    private static readonly char[] TrimChars = [' ', '_', '-', '·', '•', '.', '(', ')', '（', '）', '「', '」'];

    /// <summary>常见启动器/安装位置的 .minecraft 根目录（去重）。</summary>
    public static IEnumerable<string> SearchRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        var roots = new[]
        {
            Path.Combine(appData, ".minecraft"),
            Path.Combine(localAppData, ".minecraft"),
            Path.Combine(home, ".minecraft"),
            Path.Combine(docs, ".minecraft"),
            Path.Combine(appData, "HMCL", ".minecraft"),
            Path.Combine(appData, "PCL2", ".minecraft"),
            Path.Combine(docs, "Minecraft"),
        };

        return roots
            .Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(Path.Combine(r, "versions")))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>搜索可能与给定整合包/版本匹配的实例，按匹配度从高到低排序。</summary>
    public static List<McInstanceInfo> FindCandidates(string? versionName, string? gameVersion)
    {
        var found = new Dictionary<string, McInstanceInfo>(StringComparer.OrdinalIgnoreCase);

        var remembered = TryGetRemembered(versionName, gameVersion);
        if (!string.IsNullOrWhiteSpace(remembered) && Directory.Exists(remembered))
        {
            var instance = ReadInstance(remembered) ?? new McInstanceInfo
            {
                Name = Path.GetFileName(remembered.TrimEnd('\\', '/')),
                InstanceDir = remembered,
                GameDir = remembered,
                Isolated = true
            };
            found[remembered] = new McInstanceInfo
            {
                Name = instance.Name,
                McVersion = instance.McVersion,
                Loader = instance.Loader,
                InstanceDir = instance.InstanceDir,
                GameDir = instance.GameDir,
                Isolated = instance.Isolated,
                Score = 200
            };
        }

        if (string.IsNullOrWhiteSpace(versionName) && string.IsNullOrWhiteSpace(gameVersion))
        {
            return found.Values.ToList();
        }

        foreach (var root in SearchRoots())
        {
            foreach (var instance in ScanInstances(root))
            {
                // 只认版本隔离的实例，避免还原进原版共享的 saves。
                if (!instance.Isolated)
                {
                    continue;
                }

                var score = Score(instance, versionName, gameVersion);
                if (score < 70)
                {
                    continue;
                }

                if (!found.TryGetValue(instance.InstanceDir, out var existing) || existing.Score < score)
                {
                    instance.Score = score;
                    found[instance.InstanceDir] = instance;
                }
            }
        }

        return found.Values
            .OrderByDescending(i => i.Score)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>扫描一个 .minecraft 根目录下的全部版本实例。</summary>
    public static List<McInstanceInfo> ScanInstances(string mcDir)
    {
        var list = new List<McInstanceInfo>();
        var versionsDir = Path.Combine(mcDir, "versions");
        if (!Directory.Exists(versionsDir))
        {
            return list;
        }

        string[] dirs;
        try
        {
            dirs = Directory.GetDirectories(versionsDir);
        }
        catch
        {
            return list;
        }

        foreach (var dir in dirs)
        {
            var instance = ReadInstance(dir);
            if (instance != null)
            {
                list.Add(instance);
            }
        }

        return list.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static McInstanceInfo? ReadInstance(string versionDir)
    {
        var name = Path.GetFileName(versionDir.TrimEnd('\\', '/'));
        var jsonPath = Path.Combine(versionDir, name + ".json");
        if (!File.Exists(jsonPath))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var root = doc.RootElement;

            var loader = "vanilla";
            if (root.TryGetProperty("libraries", out var libs) && libs.ValueKind == JsonValueKind.Array)
            {
                foreach (var lib in libs.EnumerateArray())
                {
                    if (!lib.TryGetProperty("name", out var nameNode))
                    {
                        continue;
                    }
                    var libName = nameNode.GetString() ?? "";
                    if (libName.StartsWith("net.fabricmc:fabric-loader", StringComparison.Ordinal))
                    {
                        loader = "fabric";
                    }
                    else if (libName.StartsWith("net.neoforged:neoforge", StringComparison.Ordinal))
                    {
                        loader = "neoforge";
                    }
                    else if (loader == "vanilla"
                             && libName.StartsWith("net.minecraftforge:", StringComparison.Ordinal))
                    {
                        loader = "forge";
                    }
                }
            }

            var mcVersion = ReadMcVersion(root);
            if (string.IsNullOrWhiteSpace(mcVersion))
            {
                return null;
            }

            var isolated = Directory.Exists(Path.Combine(versionDir, "saves"))
                || Directory.Exists(Path.Combine(versionDir, "mods"))
                || Directory.Exists(Path.Combine(versionDir, "config"));
            var mcDir = Directory.GetParent(versionDir)?.Parent?.FullName ?? versionDir;

            return new McInstanceInfo
            {
                Name = name,
                McVersion = mcVersion,
                Loader = loader,
                InstanceDir = versionDir,
                GameDir = isolated ? versionDir : mcDir,
                Isolated = isolated
            };
        }
        catch
        {
            // 单个实例 JSON 损坏不影响其他实例
            return null;
        }
    }

    private static string ReadMcVersion(JsonElement root)
    {
        foreach (var key in new[] { "inheritsFrom", "clientVersion", "id" })
        {
            if (!root.TryGetProperty(key, out var node))
            {
                continue;
            }
            var text = node.GetString() ?? "";
            var match = Regex.Match(text, @"\b(1\.\d+(?:\.\d+)?)\b");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }
        return "";
    }

    private static int Score(McInstanceInfo instance, string? versionName, string? gameVersion)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(versionName))
        {
            if (string.Equals(instance.Name, versionName, StringComparison.OrdinalIgnoreCase))
            {
                score = 100;
            }
            else
            {
                var a = Normalize(instance.Name);
                var b = Normalize(versionName);
                if (a.Length > 0 && a == b)
                {
                    score = 90;
                }
                else if (a.Length > 0 && b.Length > 0
                         && (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal)))
                {
                    score = 70;
                }
            }
        }
        else if (string.IsNullOrWhiteSpace(gameVersion))
        {
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(gameVersion)
            && string.Equals(instance.McVersion, gameVersion.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
        }

        return score;
    }

    /// <summary>去掉空格、标点并做全角归一化，用于宽松比较整合包名。</summary>
    private static string Normalize(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch) && !TrimChars.Contains(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- 记住用户选择
    private static string Key(string? versionName, string? gameVersion)
        => Normalize(versionName ?? "") + "|" + (gameVersion ?? "").Trim().ToLowerInvariant();

    public static string? TryGetRemembered(string? versionName, string? gameVersion)
    {
        if (string.IsNullOrWhiteSpace(versionName))
        {
            return null;
        }

        try
        {
            if (!File.Exists(StorePath))
            {
                return null;
            }
            if (JsonNode.Parse(File.ReadAllText(StorePath)) is not JsonObject store)
            {
                return null;
            }
            if (store[Key(versionName, gameVersion)] is JsonValue value
                && value.TryGetValue<string>(out var dir)
                && !string.IsNullOrWhiteSpace(dir))
            {
                return dir;
            }
        }
        catch
        {
            // 配置损坏时按未记住处理
        }
        return null;
    }

    /// <summary>记住「整合包名 + 游戏版本 → 实例目录」，下次还原/装模组直接命中。</summary>
    public static void Remember(string? versionName, string? gameVersion, string? instanceDir)
    {
        if (string.IsNullOrWhiteSpace(versionName) || string.IsNullOrWhiteSpace(instanceDir)
            || !Directory.Exists(instanceDir))
        {
            return;
        }

        try
        {
            var store = File.Exists(StorePath)
                && JsonNode.Parse(File.ReadAllText(StorePath)) is JsonObject obj
                ? obj
                : new JsonObject();
            store[Key(versionName, gameVersion)] = instanceDir;

            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, store.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 记住失败不影响还原
        }
    }
}
