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

    /// <summary>扫描结果缓存：浅扫磁盘较慢，短时间内重复打开弹窗直接复用。</summary>
    private static List<string>? _rootCache;
    private static DateTime _rootCacheAt;

    /// <summary>手动刷新/安装完新启动器后清缓存，下次重新扫描。</summary>
    public static void InvalidateSearchCache()
    {
        _rootCache = null;
    }

    /// <summary>
    /// 分层收集本机可能的 .minecraft 根目录 / 整合包实例目录：
    /// 环境变量常见位置 → 启动器配置里的自定义目录 → 固定磁盘浅扫（限时）。
    /// </summary>
    public static IEnumerable<string> SearchRoots()
    {
        if (_rootCache is not null && (DateTime.UtcNow - _rootCacheAt).TotalSeconds < 30)
        {
            return _rootCache;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        var roots = new List<string>();
        void Add(string? dir)
        {
            if (!string.IsNullOrWhiteSpace(dir))
            {
                roots.Add(dir);
            }
        }

        // 1) 环境变量 + 常见安装位置
        Add(Path.Combine(appData, ".minecraft"));
        Add(Path.Combine(localAppData, ".minecraft"));
        Add(Path.Combine(home, ".minecraft"));
        Add(Path.Combine(docs, ".minecraft"));
        Add(Path.Combine(home, "Desktop", ".minecraft"));
        Add(Path.Combine(home, "Downloads", ".minecraft"));
        Add(Path.Combine(docs, "Minecraft"));

        // 2) 常见启动器目录（启动器自己的目录 + 它默认的 .minecraft）
        foreach (var launcher in new[]
                 {
                     Path.Combine(appData, "HMCL"),
                     Path.Combine(appData, "PCL"),
                     Path.Combine(appData, "PCL2"),
                     Path.Combine(localAppData, "PCL"),
                     Path.Combine(localAppData, "PCL2"),
                     Path.Combine(appData, "BakaXL"),
                     Path.Combine(appData, "PrismLauncher"),
                     Path.Combine(appData, "MultiMC"),
                     Path.Combine(home, "curseforge", "minecraft"),
                     Path.Combine(home, "curseforge", "minecraft", "Instances"),
                     Path.Combine(appData, "ModrinthApp"),
                     Path.Combine(appData, "ModrinthApp", "profiles"),
                 })
        {
            Add(launcher);
            Add(Path.Combine(launcher, ".minecraft"));
        }

        // 3) 启动器配置里用户自己改过的游戏目录（PCL/HMCL 等）
        foreach (var dir in LauncherConfiguredDirs(appData, localAppData, home))
        {
            Add(dir);
        }

        // 4) 固定磁盘浅扫兜底（限时 3 秒，跳过系统目录）
        foreach (var dir in ShallowDriveScan())
        {
            Add(dir);
        }

        _rootCache = roots
            .Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r) && LooksLikeRoot(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _rootCacheAt = DateTime.UtcNow;
        return _rootCache;
    }

    /// <summary>目录里可能有版本：标准 versions、启动器 instances，或自己就是整合包实例。</summary>
    private static bool LooksLikeRoot(string dir)
    {
        return Directory.Exists(Path.Combine(dir, "versions"))
            || Directory.Exists(Path.Combine(dir, "instances"))
            || Directory.Exists(Path.Combine(dir, "profiles"))
            || LooksLikeInstanceDir(dir);
    }

    /// <summary>整合包实例的典型标记文件/目录。</summary>
    private static bool LooksLikeInstanceDir(string dir)
    {
        return File.Exists(Path.Combine(dir, "instance.cfg"))
            || File.Exists(Path.Combine(dir, "minecraftinstance.json"))
            || File.Exists(Path.Combine(dir, "profile.json"))
            || Directory.Exists(Path.Combine(dir, "mods"))
            || Directory.Exists(Path.Combine(dir, "saves"));
    }

    /// <summary>读取 PCL / HMCL / BakaXL 配置里记录的自定义游戏目录。</summary>
    private static IEnumerable<string> LauncherConfiguredDirs(string appData, string localAppData, string home)
    {
        var results = new List<string>();
        var files = new[]
        {
            Path.Combine(appData, "PCL", "PCL.ini"),
            Path.Combine(appData, "PCL", "Setup.ini"),
            Path.Combine(appData, "PCL2", "PCL.ini"),
            Path.Combine(localAppData, "PCL", "PCL.ini"),
            Path.Combine(localAppData, "PCL2", "PCL.ini"),
            Path.Combine(appData, ".hmcl.json"),
            Path.Combine(appData, "HMCL", ".hmcl.json"),
            Path.Combine(home, ".hmcl.json"),
            Path.Combine(appData, "BakaXL", "BakaXL.json"),
        };
        var pathRegex = new Regex(@"[A-Za-z]:[\\/][^""'\r\n,;]+");

        foreach (var file in files)
        {
            try
            {
                if (!File.Exists(file))
                {
                    continue;
                }
                var text = File.ReadAllText(file);
                if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    var m = Regex.Match(text,
                        @"""(?:gameDir|game_dir|minecraftDir|gamePath|launchFolder)""\s*:\s*""([^""]+)""",
                        RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        AddConfiguredDir(results, m.Groups[1].Value);
                    }
                }
                foreach (Match m in pathRegex.Matches(text))
                {
                    AddConfiguredDir(results, m.Value);
                }
            }
            catch
            {
                // 单个启动器配置损坏不影响其它来源
            }
        }
        return results;
    }

    private static void AddConfiguredDir(List<string> results, string raw)
    {
        var path = raw.Trim().Trim('"').Replace('/', '\\');
        if (path.Length is > 2 and < 400 && (path.EndsWith(".minecraft", StringComparison.OrdinalIgnoreCase)
                                             || Directory.Exists(path)))
        {
            results.Add(path);
        }
    }

    /// <summary>固定磁盘浅扫：只看根目录和一层，关键字目录再看两层；超时立即停止。</summary>
    private static IEnumerable<string> ShallowDriveScan()
    {
        var results = new List<string>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "windows", "program files", "program files (x86)", "programdata", "$recycle.bin",
            "system volume information", "recovery", "perflogs", "users", "intel", "amd", "nvidia",
            "msocache", "boot", "efi",
        };
        string[] keywords = ["minecraft", ".minecraft", "pcl", "pcl2", "hmcl", "bakaxl",
            "我的世界", "整合包", "游戏", "games", "mc"];

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (watch.ElapsedMilliseconds > 3000)
            {
                break;
            }
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                {
                    continue;
                }
            }
            catch
            {
                continue;
            }

            foreach (var top in SafeDirs(drive.RootDirectory.FullName))
            {
                if (watch.ElapsedMilliseconds > 3000)
                {
                    break;
                }
                var leaf = Path.GetFileName(top).Trim();
                if (string.IsNullOrEmpty(leaf) || skip.Contains(leaf))
                {
                    continue;
                }

                if (LooksLikeRoot(top))
                {
                    results.Add(top);
                }
                // 只有名字像游戏目录的才继续往下看，避免全盘遍历。
                if (!keywords.Any(k => leaf.Contains(k, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                results.Add(top);
                foreach (var child in SafeDirs(top))
                {
                    if (watch.ElapsedMilliseconds > 3000)
                    {
                        break;
                    }
                    if (LooksLikeRoot(child))
                    {
                        results.Add(child);
                    }
                    foreach (var grand in SafeDirs(child))
                    {
                        if (watch.ElapsedMilliseconds > 3000)
                        {
                            break;
                        }
                        if (LooksLikeRoot(grand))
                        {
                            results.Add(grand);
                        }
                    }
                }
            }
        }
        return results;
    }

    private static string[] SafeDirs(string dir)
    {
        try
        {
            return Directory.GetDirectories(dir);
        }
        catch
        {
            return [];
        }
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

    /// <summary>
    /// 扫描一个目录下的实例，兼容三种结构：
    /// 1) 标准 .minecraft/versions/*；
    /// 2) Prism/MultiMC/CurseForge/Modrinth 的 instances（profiles）/*；
    /// 3) 目录本身就是一个整合包实例（手动选择时）。
    /// </summary>
    public static List<McInstanceInfo> ScanInstances(string mcDir)
    {
        var list = new List<McInstanceInfo>();
        if (string.IsNullOrWhiteSpace(mcDir) || !Directory.Exists(mcDir))
        {
            return list;
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(McInstanceInfo? instance)
        {
            if (instance is not null && seen.Add(instance.GameDir))
            {
                list.Add(instance);
            }
        }

        // 1) 标准 versions
        foreach (var dir in SafeDirs(Path.Combine(mcDir, "versions")))
        {
            Add(ReadInstance(dir));
        }

        // 2) 启动器 instances / profiles 容器
        var instancesDir = Path.Combine(mcDir, "instances");
        var profilesDir = Path.Combine(mcDir, "profiles");
        var leaf = Path.GetFileName(mcDir.TrimEnd('\\', '/'));
        var containerDir =
            Directory.Exists(instancesDir) ? instancesDir :
            Directory.Exists(profilesDir) ? profilesDir :
            string.Equals(leaf, "instances", StringComparison.OrdinalIgnoreCase)
                || string.Equals(leaf, "profiles", StringComparison.OrdinalIgnoreCase) ? mcDir : "";
        foreach (var dir in SafeDirs(containerDir))
        {
            foreach (var instance in ReadThirdParty(dir))
            {
                Add(instance);
            }
        }

        // 3) 目录自己就是整合包实例（手动选择 / 直接选中实例目录）
        if (list.Count == 0)
        {
            Add(ReadInstance(mcDir));
            if (list.Count == 0)
            {
                foreach (var instance in ReadThirdParty(mcDir))
                {
                    Add(instance);
                }
            }
        }

        return list.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>读取第三方启动器实例（Prism/MultiMC、CurseForge、Modrinth 或普通整合包目录）。</summary>
    private static IEnumerable<McInstanceInfo> ReadThirdParty(string dir)
    {
        var leaf = Path.GetFileName(dir.TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(leaf) || !Directory.Exists(dir))
        {
            yield break;
        }

        // Prism / MultiMC：instance.cfg + mmc-pack.json，游戏目录在 .minecraft
        if (File.Exists(Path.Combine(dir, "instance.cfg")))
        {
            var gameDir = Path.Combine(dir, ".minecraft");
            if (!Directory.Exists(gameDir))
            {
                gameDir = dir;
            }
            var (mcVersion, loader) = ReadMmcPack(Path.Combine(dir, "mmc-pack.json"));
            yield return new McInstanceInfo
            {
                Name = ReadIniValue(Path.Combine(dir, "instance.cfg"), "name") ?? leaf,
                McVersion = mcVersion,
                Loader = loader,
                InstanceDir = gameDir,
                GameDir = gameDir,
                Isolated = true
            };
            yield break;
        }

        // CurseForge
        var cfPath = Path.Combine(dir, "minecraftinstance.json");
        if (File.Exists(cfPath))
        {
            var (name, mcVersion, loader) = ReadCurseForge(cfPath);
            yield return new McInstanceInfo
            {
                Name = string.IsNullOrWhiteSpace(name) ? leaf : name,
                McVersion = mcVersion,
                Loader = loader,
                InstanceDir = dir,
                GameDir = dir,
                Isolated = true
            };
            yield break;
        }

        // Modrinth
        var mrPath = Path.Combine(dir, "profile.json");
        if (File.Exists(mrPath))
        {
            var (name, mcVersion, loader) = ReadModrinth(mrPath);
            yield return new McInstanceInfo
            {
                Name = string.IsNullOrWhiteSpace(name) ? leaf : name,
                McVersion = mcVersion,
                Loader = loader,
                InstanceDir = dir,
                GameDir = dir,
                Isolated = true
            };
            yield break;
        }

        // 普通整合包/手动选中的游戏目录
        if (Directory.Exists(Path.Combine(dir, "mods"))
            || Directory.Exists(Path.Combine(dir, "saves"))
            || Directory.Exists(Path.Combine(dir, "config")))
        {
            yield return new McInstanceInfo
            {
                Name = leaf,
                McVersion = "",
                Loader = "unknown",
                InstanceDir = dir,
                GameDir = dir,
                Isolated = true
            };
        }
    }

    private static string? ReadIniValue(string path, string key)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var idx = line.IndexOf('=');
                if (idx <= 0)
                {
                    continue;
                }
                if (line[..idx].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    return line[(idx + 1)..].Trim();
                }
            }
        }
        catch
        {
            // 配置损坏按没有处理
        }
        return null;
    }

    private static (string McVersion, string Loader) ReadMmcPack(string path)
    {
        var mcVersion = "";
        var loader = "vanilla";
        try
        {
            if (!File.Exists(path)
                || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root
                || root["components"] is not JsonArray components)
            {
                return (mcVersion, loader);
            }
            foreach (var node in components)
            {
                if (node is not JsonObject comp)
                {
                    continue;
                }
                var uid = (comp["uid"]?.GetValue<string>() ?? "").ToLowerInvariant();
                var version = comp["version"]?.GetValue<string>() ?? "";
                if (uid == "net.minecraft")
                {
                    mcVersion = version;
                }
                else if (uid.Contains("fabric-loader") || uid.Contains("quilt-loader"))
                {
                    loader = "fabric";
                }
                else if (uid.Contains("neoforge"))
                {
                    loader = "neoforge";
                }
                else if (uid.Contains("forge"))
                {
                    loader = "forge";
                }
            }
        }
        catch
        {
            // 解析失败保留默认值
        }
        return (mcVersion, loader);
    }

    private static (string Name, string McVersion, string Loader) ReadCurseForge(string path)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
            {
                return ("", "", "unknown");
            }
            var name = root["name"]?.GetValue<string>() ?? "";
            var mcVersion = root["gameVersion"]?.GetValue<string>() ?? "";
            var loaderName = (root["baseModLoader"]?["name"]?.GetValue<string>() ?? "").ToLowerInvariant();
            return (name, mcVersion, LoaderFromText(loaderName));
        }
        catch
        {
            return ("", "", "unknown");
        }
    }

    private static (string Name, string McVersion, string Loader) ReadModrinth(string path)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
            {
                return ("", "", "unknown");
            }
            var name = root["name"]?.GetValue<string>() ?? "";
            var mcVersion = root["game_version"]?.GetValue<string>() ?? "";
            var loader = (root["loader"]?.GetValue<string>() ?? "").ToLowerInvariant();
            return (name, mcVersion, LoaderFromText(loader));
        }
        catch
        {
            return ("", "", "unknown");
        }
    }

    private static string LoaderFromText(string text)
    {
        if (text.Contains("neoforge", StringComparison.Ordinal))
        {
            return "neoforge";
        }
        if (text.Contains("fabric", StringComparison.Ordinal) || text.Contains("quilt", StringComparison.Ordinal))
        {
            return "fabric";
        }
        if (text.Contains("forge", StringComparison.Ordinal))
        {
            return "forge";
        }
        return "unknown";
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
