using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 下载目录与文件名处理：默认落在软件安装目录下的 Downloads，
/// 允许设置页改成任意可写目录；文件名做 Windows 非法字符清洗与重名避让。
/// </summary>
internal static class DownloadPaths
{
    private const int MaxFileNameLength = 180;

    private const string DownloadFolderName = "Downloads";

    /// <summary>安装目录下的默认下载目录（AppContext.BaseDirectory 即 exe 所在目录）。</summary>
    public static string DefaultDirectory { get; } =
        Path.Combine(AppContext.BaseDirectory, DownloadFolderName);

    /// <summary>解析生效的下载目录：设置里有值就用，否则用安装目录下的 Downloads。</summary>
    public static string ResolveDirectory(AppSettings settings)
    {
        var configured = settings.DownloadDirectory;
        return string.IsNullOrWhiteSpace(configured)
            ? DefaultDirectory
            : configured.Trim();
    }

    /// <summary>确保目录存在且可写；失败返回 false 并给出原因（供 UI 提示，不静默回退）。</summary>
    public static bool TryEnsureWritable(string directory, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(directory);

            // 用一次性探针文件验证真实可写性：某些目录能创建子目录却因 ACL 写不进文件。
            var probe = Path.Combine(directory, ".steameya-write-test-" + Guid.NewGuid().ToString("N"));
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }

            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>清洗为合法文件名：去非法字符与控制字符、去首尾空白与结尾点、限长（保留扩展名）。</summary>
    public static string SanitizeFileName(string? raw, string fallbackExtension = ".bin")
    {
        var name = (raw ?? string.Empty).Trim();

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        // 控制字符（部分不在 GetInvalidFileNameChars 里，但资源管理器不接受）。
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        name = name.TrimEnd('.', ' ');

        if (string.IsNullOrWhiteSpace(name))
        {
            name = "download-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            if (!name.EndsWith(fallbackExtension, StringComparison.OrdinalIgnoreCase))
            {
                name += fallbackExtension;
            }
        }

        if (name.Length > MaxFileNameLength)
        {
            var extension = Path.GetExtension(name);
            if (extension.Length > 32)
            {
                extension = string.Empty;
            }

            var stemLength = Math.Max(1, MaxFileNameLength - extension.Length);
            name = name[..Math.Min(name.Length, stemLength)] + extension;
        }

        return name;
    }

    /// <summary>
    /// 同名文件避免覆盖：已存在则生成 "name (1).ext"、"name (2).ext"。
    /// 只判断最终文件与续传状态文件都不存在的情况。
    /// </summary>
    public static string ResolveUniquePath(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path) && !File.Exists(StatePathFor(path)))
        {
            return path;
        }

        var extension = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        for (var i = 1; i < 10000; i++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate) && !File.Exists(StatePathFor(candidate)))
            {
                return candidate;
            }
        }

        throw new IOException("同名文件过多，无法生成唯一文件名。");
    }

    /// <summary>下载中使用的临时文件后缀（完成后再改名为最终文件，避免用户拿到半截文件）。</summary>
    public const string PartExtension = ".steameya-part";

    public const string StateExtension = ".steameya-dl";

    public static string PartPathFor(string targetPath) => targetPath + PartExtension;

    public static string StatePathFor(string targetPath) => targetPath + StateExtension;
}
