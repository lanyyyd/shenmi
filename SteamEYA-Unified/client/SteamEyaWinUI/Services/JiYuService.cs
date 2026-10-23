using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 极域电子教室绕过模块的客户端侧托管：启动 32 位 JiYuHost.exe（由它向 StudentMain.exe 注入 Hook DLL），
/// 通过 WM_COPYDATA 下发配置，并读取宿主写出的状态文件供「小工具」页展示。
/// 与网关/云游戏引擎一样由客户端拉起、随客户端退出，不单独安装第三方软件。
/// </summary>
internal static partial class JiYuService
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    private const uint WmCopyData = 0x004A;
    private const uint SmtoAbortIfHung = 0x0002;
    private const string HostWindowTitle = "SteamEYA.JiYuHost.Window";

    private static readonly object Gate = new();
    private static Process? _process;
    private static DateTime _lastStartAttempt = DateTime.MinValue;

    public static string StatusFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SteamEYA", "jiyu-status.txt");

    public static string IniFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SteamEYA", "jiyu.ini");

    /// <summary>客户端启动即调用：拉起宿主并下发当前配置；失败不影响主界面（极域未安装时属正常）。</summary>
    public static void Start(AppSettings settings)
    {
        EnsureStarted();
        _ = ConfigureWhenReadyAsync(settings);
    }

    /// <summary>启动宿主进程；已在运行（包括上一次启动残留）时直接复用。</summary>
    public static void EnsureStarted()
    {
        lock (Gate)
        {
            if (HostWindow != 0)
            {
                return;
            }

            if (_process is { HasExited: false })
            {
                return;
            }

            // 失败后 10 秒内不重复拉起，避免页面刷新时反复弹进程创建错误。
            if (DateTime.UtcNow - _lastStartAttempt < TimeSpan.FromSeconds(10))
            {
                return;
            }

            _lastStartAttempt = DateTime.UtcNow;

            var hostExe = ResolveHostExe();
            var hookDll = ResolveHookDll();
            if (hostExe is null || hookDll is null)
            {
                AppLog.Warn("未找到极域绕过组件（jiyu/JiYuHost.exe 或 SteamEya.JiYuHook.dll）。");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(StatusFilePath)!);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = hostExe,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(hostExe)!
                };
                psi.ArgumentList.Add("--status");
                psi.ArgumentList.Add(StatusFilePath);
                psi.ArgumentList.Add("--ini");
                psi.ArgumentList.Add(IniFilePath);
                psi.ArgumentList.Add("--dll");
                psi.ArgumentList.Add(hookDll);

                _process = Process.Start(psi);
                if (_process is null)
                {
                    AppLog.Warn("极域绕过宿主启动失败：Process.Start 返回 null。");
                    return;
                }

                ChildProcessJob.Assign(_process);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"极域绕过宿主启动失败：{ex.Message}");
            }
        }
    }

    /// <summary>退出客户端时结束宿主；已注入的 Hook 会随极域进程结束而失效。</summary>
    public static void Stop()
    {
        lock (Gate)
        {
            // 即使宿主不是本次客户端拉起的（上次异常退出残留），也通过窗口消息让它正常退出。
            var hwnd = HostWindow;
            if (hwnd != 0)
            {
                PostMessageW(hwnd, 0x0010 /* WM_CLOSE */, 0, 0);
            }

            try
            {
                if (_process is { HasExited: false })
                {
                    _process.WaitForExit(1500);
                    if (!_process.HasExited)
                    {
                        _process.Kill(entireProcessTree: true);
                        _process.WaitForExit(2000);
                    }
                }
            }
            catch
            {
                // 忽略：作业对象也会在客户端退出时兜底结束宿主。
            }
            finally
            {
                _process = null;
            }

            // 宿主退出后状态文件已无意义，删除避免下次启动瞬间显示上一次的旧状态。
            try
            {
                File.Delete(StatusFilePath);
            }
            catch
            {
                // 忽略：文件被占用时下次读取仍会按新宿主写入的内容覆盖。
            }
        }
    }

    /// <summary>把设置下发给宿主：绕过开关、广播置顶许可、老板键；宿主未运行时返回 false。</summary>
    public static bool ApplySettings(AppSettings settings)
    {
        var ok = Send($"cfg:bypass:{(settings.JiYuBypassEnabled ? 1 : 0)}");
        ok &= Send($"cfg:allowtop:{(settings.JiYuAllowGbTop ? 1 : 0)}");
        ok &= Send($"cfg:hotkey:{(settings.JiYuBossKeyEnabled ? 1 : 0)}:{settings.JiYuBossKeyMods}:{settings.JiYuBossKeyVk}");
        return ok;
    }

    /// <summary>立即触发一次老板键（用于小工具页的“测试”按钮）。</summary>
    public static bool TriggerBossKey()
    {
        return Send("cmd:boss");
    }

    /// <summary>请求宿主刷新一次状态文件。</summary>
    public static bool RequestRefresh()
    {
        return Send("cmd:status");
    }

    /// <summary>向宿主窗口发送一条 WM_COPYDATA 命令；宿主未启动时返回 false。</summary>
    public static bool Send(string command)
    {
        var hwnd = HostWindow;
        if (hwnd == 0)
        {
            return false;
        }

        var buffer = Marshal.StringToHGlobalUni(command);
        try
        {
            var data = new CopyDataStruct
            {
                cbData = (uint)((command.Length + 1) * sizeof(char)),
                lpData = buffer
            };
            SendMessageTimeoutW(hwnd, WmCopyData, 0, ref data, SmtoAbortIfHung, 500, out _);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"向极域宿主发送命令失败：{ex.Message}");
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// 宿主刚启动时窗口可能还没建好，按短间隔重试下发配置；成功后不再重复。
    /// </summary>
    private static async Task ConfigureWhenReadyAsync(AppSettings settings)
    {
        for (var i = 0; i < 40; i++)
        {
            if (HostWindow != 0)
            {
                ApplySettings(settings);
                RequestRefresh();
                return;
            }

            if (!IsOwnedProcessAlive)
            {
                return;
            }

            try
            {
                await Task.Delay(250);
            }
            catch
            {
                return;
            }
        }
    }

    private static bool IsOwnedProcessAlive
    {
        get
        {
            lock (Gate)
            {
                return _process is { HasExited: false };
            }
        }
    }

    /// <summary>读取宿主写出的状态文件；宿主未运行或文件正在写入时返回“不可用”状态。</summary>
    public static JiYuStatus ReadStatus()
    {
        // 宿主窗口不在时状态文件只可能是上一次运行残留，直接忽略，避免页面显示过期状态。
        if (HostWindow == 0)
        {
            return JiYuStatus.Unavailable;
        }

        try
        {
            if (!File.Exists(StatusFilePath))
            {
                return JiYuStatus.Unavailable;
            }

            using var stream = new FileStream(StatusFilePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            // 宿主已改为 UTF-8 无 BOM 输出；detectEncodingFromByteOrderMarks 兼容旧的 UTF-16 状态文件。
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var status = new JiYuStatus { Available = true };

            while (reader.ReadLine() is { } line)
            {
                var index = line.IndexOf('=');
                if (index <= 0)
                {
                    continue;
                }

                var key = line[..index];
                var value = line[(index + 1)..];
                switch (key)
                {
                    case "student": status.StudentRunning = value == "1"; break;
                    case "pid": status.Pid = int.TryParse(value, out var pid) ? pid : 0; break;
                    case "studentPath": status.StudentPath = value; break;
                    case "injected": status.Injected = value == "1"; break;
                    case "controlled": status.Controlled = value == "1"; break;
                    case "hook": status.HookFound = value == "1"; break;
                    case "bypass": status.BypassEnabled = value == "1"; break;
                    case "allowTop": status.AllowGbTop = value == "1"; break;
                    case "gbTop": status.GbTopmost = value == "1"; break;
                    case "gbFull": status.GbFullscreen = value == "1"; break;
                    case "locked": status.InputLocked = value == "1"; break;
                    case "hotkeyEnabled": status.HotkeyEnabled = value == "1"; break;
                    case "hotkeyRegistered": status.HotkeyRegistered = value == "1"; break;
                    case "hotkey": status.HotkeyText = value; break;
                    case "message": status.Message = value; break;
                }
            }

            return status;
        }
        catch
        {
            return JiYuStatus.Unavailable;
        }
    }

    /// <summary>把修饰键与虚拟键格式化成界面显示文本（与宿主状态文件中的 hotkey 一致）。</summary>
    public static string FormatHotkey(uint mods, uint vk)
    {
        if (vk == 0)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        if ((mods & ModControl) != 0) text.Append("Ctrl+");
        if ((mods & ModAlt) != 0) text.Append("Alt+");
        if ((mods & ModShift) != 0) text.Append("Shift+");
        if ((mods & ModWin) != 0) text.Append("Win+");
        text.Append(KeyName(vk));
        return text.ToString();
    }

    private static string KeyName(uint vk)
    {
        if ((vk >= 'A' && vk <= 'Z') || (vk >= '0' && vk <= '9'))
        {
            return ((char)vk).ToString();
        }

        if (vk is >= 0x70 and <= 0x87)
        {
            return $"F{vk - 0x70 + 1}";
        }

        return vk switch
        {
            0x20 => "Space",
            0x0D => "Enter",
            0x1B => "Esc",
            0x09 => "Tab",
            0x08 => "Backspace",
            0x2E => "Delete",
            0x2D => "Insert",
            0x24 => "Home",
            0x23 => "End",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x25 => "Left",
            0x27 => "Right",
            0x26 => "Up",
            0x28 => "Down",
            0xC0 => "`",
            0xBD => "-",
            0xBB => "=",
            0xDB => "[",
            0xDD => "]",
            0xDC => "\\",
            0xBA => ";",
            0xDE => "'",
            0xBC => ",",
            0xBE => ".",
            0xBF => "/",
            _ => $"0x{vk:X2}"
        };
    }

    private static string? ResolveHostExe()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "jiyu", "JiYuHost.exe");
        if (File.Exists(bundled))
        {
            return bundled;
        }

        // 开发期：从输出目录向上找仓库里的原生工程产物。
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "native", "JiYu", "Host", "bin", "Win32", "Release", "JiYuHost.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? ResolveHookDll()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "jiyu", "SteamEya.JiYuHook.dll");
        if (File.Exists(bundled))
        {
            return bundled;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "native", "JiYu", "Hook", "bin", "Win32", "Release", "SteamEya.JiYuHook.dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static nint HostWindow => FindWindowW(null, HostWindowTitle);

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataStruct
    {
        public nuint dwData;
        public uint cbData;
        public nint lpData;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint FindWindowW(string? lpClassName, string lpWindowName);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SendMessageTimeoutW(
        nint hWnd, uint msg, nint wParam, ref CopyDataStruct lParam,
        uint flags, uint timeout, out nint result);
}

/// <summary>宿主状态文件快照。</summary>
internal sealed class JiYuStatus
{
    public static JiYuStatus Unavailable { get; } = new();

    public bool Available { get; set; }
    public bool StudentRunning { get; set; }
    public int Pid { get; set; }
    public string StudentPath { get; set; } = string.Empty;
    public bool Injected { get; set; }
    public bool Controlled { get; set; }
    public bool HookFound { get; set; }
    public bool BypassEnabled { get; set; }
    public bool AllowGbTop { get; set; }
    public bool GbTopmost { get; set; }
    public bool GbFullscreen { get; set; }
    public bool InputLocked { get; set; }
    public bool HotkeyEnabled { get; set; }
    public bool HotkeyRegistered { get; set; }
    public string HotkeyText { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
