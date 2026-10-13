using System.Diagnostics;
using System.Security.Cryptography;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 下载并启动更新安装包（自建更新源）。安装包下载后**保留**在
/// %TEMP%\SteamEYA\updates\&lt;版本&gt;\ 下，方便用户以后再次手动运行。
/// </summary>
internal sealed class UpdateInstallerService
{
    private static readonly HttpClient HttpClient = CreateHttpClient();

    private const int DownloadBufferSize = 80 * 1024;

    // 单次读取之间无进展多久算「卡死」。ResponseHeadersRead 下 HttpClient.Timeout 只约束响应头阶段，
    // 响应体的 ReadAsync 不受其约束，故需自建空闲超时。
    private static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromSeconds(60);

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
        var downloadPath = targetPath + ".downloading";

        // 空闲超时：把用户取消与「每次成功读取后重置的 60s 无进展」合成一个令牌。
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idleCts.CancelAfter(DownloadIdleTimeout);
        var idleToken = idleCts.Token;

        try
        {
            using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, idleToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;

            await using (var source = await response.Content.ReadAsStreamAsync(idleToken))
            await using (var target = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await CopyWithProgressAsync(source, target, totalBytes, progress, idleCts, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(update.Sha256))
            {
                var actualHash = ComputeSha256(downloadPath);
                if (!string.Equals(actualHash, update.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("安装包校验失败，请稍后重试。");
                }
            }

            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }

            File.Move(downloadPath, targetPath);
            return targetPath;
        }
        catch (OperationCanceledException) when (idleToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryDeletePartial(downloadPath);
            throw new TimeoutException("下载超时：长时间无数据，请稍后重试。");
        }
        catch
        {
            TryDeletePartial(downloadPath);
            throw;
        }
    }

    private static void TryDeletePartial(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
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

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("SteamEYA-Installer");
        return client;
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

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static async Task CopyWithProgressAsync(
        Stream source,
        Stream target,
        long? totalBytes,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationTokenSource idleCts,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[DownloadBufferSize];
        long received = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), idleCts.Token);
            if (read == 0)
            {
                break;
            }

            // 每读到一段就把空闲超时窗口重置，只在「持续无进展」时才触发取消。
            idleCts.CancelAfter(DownloadIdleTimeout);

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;
            progress?.Report(new UpdateDownloadProgress(received, totalBytes));
        }
    }
}

internal sealed record UpdateDownloadProgress(long BytesReceived, long? TotalBytes);
