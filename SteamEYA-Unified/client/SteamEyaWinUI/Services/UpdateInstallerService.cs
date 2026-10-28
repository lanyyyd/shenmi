using System.Diagnostics;
using System.Security.Cryptography;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 下载并启动更新安装包（自建更新源）。安装包下载后**保留**在
/// %TEMP%\SteamEYA\updates\&lt;版本&gt;\ 下，方便用户以后再次手动运行。
///
/// 下载走 <see cref="DownloadEngine"/>：5 个线程分片并行 + 断点续传（状态文件与 .part 同目录），
/// 服务器不支持 Range 时自动退化为单流下载。
/// </summary>
internal sealed class UpdateInstallerService
{
    /// <summary>分片线程数（也是分片数）。</summary>
    private const int DownloadThreads = 5;

    public async Task<string> DownloadInstallerAsync(
        AppUpdateInfo update,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var url = UpdateService.ResolveDownloadUrl(update.Url);
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException("更新下载地址为空。");
        }

        var fileName = ResolveInstallerFileName(update);
        if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("当前更新产物不是安装包。");
        }

        var version = string.IsNullOrWhiteSpace(update.Version) ? "latest" : update.Version;
        var tempRoot = Path.Combine(Path.GetTempPath(), "SteamEYA", "updates", version);
        Directory.CreateDirectory(tempRoot);

        var targetPath = Path.Combine(tempRoot, fileName);

        // 已经下过同一个安装包且校验通过：直接复用，省一次下载。
        if (File.Exists(targetPath) &&
            (string.IsNullOrWhiteSpace(update.Sha256) || VerifySha256(targetPath, update.Sha256)))
        {
            Report(progress, new FileInfo(targetPath).Length, new FileInfo(targetPath).Length);
            return targetPath;
        }

        // 旧包校验不过（可能是上次下载被截断）→ 删掉，让续传/重下都落在同一个文件名上。
        TryDeletePartial(targetPath);

        var engine = new DownloadEngine(url, tempRoot, DownloadThreads, DownloadThreads);
        using var registration = cancellationToken.Register(engine.Cancel);

        var runTask = engine.RunAsync();
        while (!runTask.IsCompleted)
        {
            Report(progress, engine.ReceivedBytes, engine.TotalBytes > 0 ? engine.TotalBytes : null);
            await Task.WhenAny(runTask, Task.Delay(250, CancellationToken.None));
        }

        await runTask;
        cancellationToken.ThrowIfCancellationRequested();

        if (engine.Status != DownloadStatus.Completed || engine.TargetPath is not { } downloaded)
        {
            throw new InvalidOperationException(DescribeFailure(engine));
        }

        var total = engine.TotalBytes > 0 ? engine.TotalBytes : (long?)null;
        Report(progress, engine.ReceivedBytes, total);

        if (!string.IsNullOrWhiteSpace(update.Sha256) && !VerifySha256(downloaded, update.Sha256))
        {
            TryDeletePartial(downloaded);
            throw new InvalidOperationException("安装包校验失败，请稍后重试。");
        }

        return downloaded;
    }

    private static void Report(IProgress<UpdateDownloadProgress>? progress, long received, long? total)
        => progress?.Report(new UpdateDownloadProgress(received, total));

    private static string DescribeFailure(DownloadEngine engine)
    {
        var error = engine.Error;
        if (string.IsNullOrWhiteSpace(error))
        {
            return engine.Status == DownloadStatus.Canceled ? "下载已取消。" : "下载失败，请稍后重试。";
        }

        if (error == "DiskFull")
        {
            return "磁盘空间不足，请清理后重试。";
        }

        if (error.StartsWith("Incomplete:", StringComparison.Ordinal))
        {
            return "下载不完整，请稍后重试（已保留进度，下次可续传）。";
        }

        if (error == "RangeNotSupported")
        {
            return "下载源不支持断点续传，请稍后重试。";
        }

        return error;
    }

    private static void TryDeletePartial(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            var part = DownloadPaths.PartPathFor(path);
            if (File.Exists(part))
            {
                File.Delete(part);
            }
            // 进度与 .part 必须一起删：只删数据而留着偏移状态，续传会把空洞当成已完成。
            var state = DownloadPaths.StatePathFor(path);
            if (File.Exists(state))
            {
                File.Delete(state);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("清理未完成的更新下载临时文件失败。", ex);
        }
    }

    /// <summary>启动安装包。参数让 Inno 安装器自动关闭占用文件的进程，无需用户手动退出。</summary>
    public bool LaunchInstaller(string installerPath)
    {
        if (!File.Exists(installerPath))
        {
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath),
            Arguments = "/CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS /NORESTARTAPPLICATIONS"
        };

        using var process = Process.Start(startInfo);
        return process is not null;
    }

    private static string ResolveInstallerFileName(AppUpdateInfo update)
    {
        if (!string.IsNullOrWhiteSpace(update.FileName))
        {
            return Path.GetFileName(update.FileName);
        }

        var url = UpdateService.ResolveDownloadUrl(update.Url);
        if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var fileName = Path.GetFileName(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }
        }

        return "SteamEYA-Installer.exe";
    }

    private static bool VerifySha256(string path, string expected)
    {
        try
        {
            return string.Equals(ComputeSha256(path), expected.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"校验安装包失败：{ex.Message}");
            return false;
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }
}

internal sealed record UpdateDownloadProgress(long BytesReceived, long? TotalBytes);
