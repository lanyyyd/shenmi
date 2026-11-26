using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 灵动岛通知客户端：拉起 native/island/steameya-island.exe，通过命名管道发送通知。
/// 岛窗口本身由原生 Direct2D 绘制，客户端只负责数据与点击回调。
/// </summary>
internal static class IslandService
{
    private static readonly object Gate = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly SemaphoreSlim WriteLock = new(1, 1);
    private static readonly Channel<byte[]> Outbound = Channel.CreateUnbounded<byte[]>();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> AvatarGates = new(StringComparer.OrdinalIgnoreCase);

    private static NamedPipeServerStream? _pipe;
    private static Process? _process;
    private static CancellationTokenSource? _cts;
    private static string _pipeName = string.Empty;
    private static bool _connected;
    private static string? _avatarDir;

    /// <summary>尝试通过灵动岛显示；原生程序不可用时返回 false，调用方回退旧浮窗。</summary>
    public static bool TryShow(IslandNotice notice)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }
            var exe = Path.Combine(AppContext.BaseDirectory, "island", "steameya-island.exe");
            if (!File.Exists(exe))
            {
                return false;
            }

            lock (Gate)
            {
                if (!EnsureStartedLocked(exe))
                {
                    return false;
                }
            }

            _ = SendAsync(notice);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Debug($"灵动岛发送失败：{ex.Message}");
            return false;
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            try
            {
                _cts?.Cancel();
            }
            catch
            {
                // 已取消
            }
            try
            {
                _pipe?.Dispose();
            }
            catch
            {
                // ignore
            }
            _pipe = null;
            _connected = false;
            try
            {
                if (_process is { HasExited: false })
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(1500);
                }
            }
            catch
            {
                // 可能已退出
            }
            _process = null;
        }
    }

    private static bool EnsureStartedLocked(string exe)
    {
        if (_process is { HasExited: false })
        {
            return true;
        }

        _connected = false;
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch
        {
            // ignore
        }
        _cts = null;
        try
        {
            _pipe?.Dispose();
        }
        catch
        {
            // ignore
        }
        _pipe = null;

        _pipeName = $"SteamEYA.Island.{Environment.ProcessId}.{Guid.NewGuid():N}";
        try
        {
            _pipe = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--pipe \"\\\\.\\pipe\\{_pipeName}\" --parent {Environment.ProcessId}",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            _process = Process.Start(psi);
            if (_process is null)
            {
                _pipe.Dispose();
                _pipe = null;
                return false;
            }
            ChildProcessJob.Assign(_process);
            _process.EnableRaisingEvents = true;
            _process.Exited += (_, _) =>
            {
                lock (Gate)
                {
                    _connected = false;
                    try
                    {
                        _cts?.Cancel();
                    }
                    catch
                    {
                        // ignore
                    }
                }
            };

            _cts = new CancellationTokenSource();
            var pipe = _pipe;
            _ = ConnectAsync(pipe, _cts.Token);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Debug($"启动灵动岛失败：{ex.Message}");
            _process?.Dispose();
            _process = null;
            _pipe?.Dispose();
            _pipe = null;
            return false;
        }
    }

    private static async Task ConnectAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            lock (Gate)
            {
                if (!ReferenceEquals(_pipe, pipe))
                {
                    return;
                }
                _connected = true;
            }
            AppLog.Debug("灵动岛已连接。");
            _ = WriteLoopAsync(pipe, ct);
            _ = ReadLoopAsync(pipe, ct);
        }
        catch (Exception ex)
        {
            AppLog.Debug($"灵动岛连接失败：{ex.Message}");
            lock (Gate)
            {
                _connected = false;
            }
        }
    }

    private static async Task SendAsync(IslandNotice notice)
    {
        try
        {
            notice.AvatarUrl = await ResolveAvatarAsync(notice.AvatarUrl).ConfigureAwait(false);
            var frame = BuildShowFrame(notice);
            Outbound.Writer.TryWrite(frame);
        }
        catch (Exception ex)
        {
            AppLog.Debug($"准备灵动岛通知失败：{ex.Message}");
        }
    }

    private static async Task WriteLoopAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await Outbound.Reader.ReadAsync(ct).ConfigureAwait(false);
                await WriteLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await pipe.WriteAsync(frame, ct).ConfigureAwait(false);
                    await pipe.FlushAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    WriteLock.Release();
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug($"灵动岛写出断开：{ex.Message}");
            lock (Gate)
            {
                _connected = false;
            }
        }
    }

    private static async Task ReadLoopAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            var header = new byte[4];
            while (!ct.IsCancellationRequested)
            {
                await ReadExactAsync(pipe, header, ct).ConfigureAwait(false);
                var length = BitConverter.ToInt32(header);
                if (length <= 0 || length > 1024 * 1024)
                {
                    break;
                }
                var payload = new byte[length];
                await ReadExactAsync(pipe, payload, ct).ConfigureAwait(false);
                using var ms = new MemoryStream(payload);
                using var reader = new BinaryReader(ms, Encoding.UTF8);
                var type = reader.ReadByte();
                if (type != 2)
                {
                    continue;
                }
                var action = ReadString(reader);
                _ = ReadString(reader);   // id，当前不需要
                if (!string.IsNullOrWhiteSpace(action))
                {
                    AppLog.Debug($"灵动岛点击：{action}");
                    NotificationService.InvokeFromIsland(action);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug($"灵动岛读取断开：{ex.Message}");
        }
        finally
        {
            lock (Gate)
            {
                _connected = false;
            }
            try
            {
                _cts?.Cancel();
            }
            catch
            {
                // ignore
            }
        }
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read <= 0)
            {
                throw new EndOfStreamException();
            }
            offset += read;
        }
    }

    private static byte[] BuildShowFrame(IslandNotice notice)
    {
        using var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0);   // 长度占位
            writer.Write((byte)1);
            WriteString(writer, notice.Id, 64);
            WriteString(writer, notice.Heading, 128);
            WriteString(writer, notice.Title, 512);
            WriteString(writer, notice.Body, 1024);
            WriteString(writer, notice.AvatarUrl, 1024);
            WriteString(writer, notice.Glyph, 16);
            WriteString(writer, notice.CoalesceKey, 64);
            writer.Write(notice.DurationMs);
            WriteString(writer, notice.Action, 1024);
        }
        var frame = ms.ToArray();
        BitConverter.GetBytes(frame.Length - 4).CopyTo(frame, 0);
        return frame;
    }

    private static void WriteString(BinaryWriter writer, string? value, int maxChars)
    {
        var text = value ?? string.Empty;
        if (text.Length > maxChars)
        {
            text = text[..maxChars];
        }
        var bytes = Encoding.UTF8.GetBytes(text);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length <= 0 || length > 65536 || reader.BaseStream.Position + length > reader.BaseStream.Length)
        {
            return string.Empty;
        }
        return Encoding.UTF8.GetString(reader.ReadBytes(length));
    }

    /// <summary>把头像下载到本地缓存，原生窗口用 WIC 读取本地文件。</summary>
    private static async Task<string> ResolveAvatarAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            if (url.StartsWith('/'))
            {
                url = SessionState.BaseUrl.TrimEnd('/') + url;
            }
            else
            {
                return File.Exists(url) ? url : string.Empty;
            }
        }

        // WIC 没有 SVG 解码器，下载了也显示不出来，直接走原生默认图标。
        if (url.Split('?', '#')[0].EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Debug($"灵动岛跳过 SVG 头像：{url}");
            return string.Empty;
        }

        var path = string.Empty;
        var temp = string.Empty;
        try
        {
            _avatarDir ??= Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SteamEYA", "island-avatars");
            Directory.CreateDirectory(_avatarDir);

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
            var ext = Path.GetExtension(new Uri(url).AbsolutePath);
            if (string.IsNullOrWhiteSpace(ext) || ext.Length > 8)
            {
                ext = ".png";
            }
            path = Path.Combine(_avatarDir, hash[..32] + ext.ToLowerInvariant());

            // 同一条消息的连续通知会并发下载同一个头像：串行化，避免共用 .tmp
            // 互相覆盖/截断后把损坏的图片永久缓存下来。
            var gate = AvatarGates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > 0)
                {
                    return path;
                }

                using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    AppLog.Debug($"灵动岛头像下载失败：HTTP {(int)response.StatusCode} {url}");
                    return string.Empty;
                }
                if (response.Content.Headers.ContentLength is > 4 * 1024 * 1024)
                {
                    return string.Empty;
                }
                var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (bytes.Length is 0 or > 4 * 1024 * 1024)
                {
                    return string.Empty;
                }
                if (!LooksLikeImage(bytes))
                {
                    AppLog.Debug($"灵动岛头像不是可识别的图片，已忽略：{url}");
                    return string.Empty;
                }

                temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
                File.Move(temp, path, overwrite: true);
                return path;
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug($"灵动岛头像处理失败：{url} / {ex.Message}");
            return string.Empty;
        }
        finally
        {
            if (temp.Length > 0)
            {
                try
                {
                    if (File.Exists(temp))
                    {
                        File.Delete(temp);
                    }
                }
                catch
                {
                    // 临时文件清理失败不影响通知显示
                }
            }
        }
    }

    /// <summary>只缓存真正的图片，防止错误页/代理响应被当成头像缓存下来。</summary>
    private static bool LooksLikeImage(byte[] data)
    {
        if (data.Length < 12)
        {
            return false;
        }
        if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return true; // PNG
        if (data[0] == 0xFF && data[1] == 0xD8) return true;                                       // JPEG
        if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46) return true;                     // GIF
        if (data[0] == 0x42 && data[1] == 0x4D) return true;                                       // BMP
        return data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50;          // WEBP
    }
}
