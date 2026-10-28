using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 极域助手（内置原版 JiYuTrainer 1.7.6）的启动壳。
/// 这里不做任何注入：启动、注入、界面与各项功能全部由原版 exe 完成，客户端只负责
/// 拉起（静默）、唤出窗口、打开「更多设置」、停止，以及在停止前先让原版卸载注入
/// （等价于原版退出提示里的「分离极域并退出」）。
/// </summary>
internal static partial class JiYuService
{
    public const string BundledVersion = "1.7.6";

    // 原版 exe 的窗口类名与注入后由 Hook DLL 创建的接收窗口标题（见 JiYuTrainer 源码）。
    private const string MainWindowClass = "JiYuTrainerWindow";
    private const string HookWindowTitle = "JiYu Trainer Virus Window";
    private const string TrainerProcessName = "JiYuTrainer";
    private const string StudentProcessName = "StudentMain";

    private const uint WmCopyData = 0x004A;
    private const uint WmHotkey = 0x0312;
    private const uint SmtoAbortIfHung = 0x0002;
    private const int SwShow = 5;
    private const uint ModNoRepeat = 0x4000;

    /// <summary>AllowSetForegroundWindow 的「任意进程」ID。</summary>
    private const uint AsfwAny = 0xFFFFFFFF;

    // 原版助手用全局原子作为热键 ID 注册 WM_HOTKEY；客户端按同一原子转发，即可复用它的伪装全屏。
    private const string FullscreenAtom = "HotKeySwFull";

    /// <summary>WM_HOTKEY 里用于区分本客户端注册的极域热键。</summary>
    public const int BossHotkeyId = 0x5A62;

    public const int FullscreenHotkeyId = 0x5A63;

    private static nint s_hotkeyWindow;

    private static readonly object Gate = new();

    /// <summary>内置助手所在目录（随安装包下发到 &lt;安装目录&gt;\jiyu）。</summary>
    public static string InstallDir => Path.Combine(AppContext.BaseDirectory, "jiyu");

    public static string ExePath => Path.Combine(InstallDir, "JiYuTrainer.exe");

    private static string IniPath => Path.Combine(InstallDir, "JiYuTrainer.ini");

    /// <summary>内置助手是否随安装包就位。</summary>
    public static bool IsAvailable => File.Exists(ExePath);

    /// <summary>启动内置助手（静默注入）；已在运行则直接复用。</summary>
    public static bool Start()
    {
        lock (Gate)
        {
            EnsureIni();

            if (!IsAvailable)
            {
                AppLog.Warn($"未找到内置极域助手：{ExePath}");
                return false;
            }

            if (IsRunning)
            {
                return true;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = ExePath,
                    Arguments = "-hidden",
                    WorkingDirectory = InstallDir,
                    UseShellExecute = false
                });
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"启动内置极域助手失败：{ex.Message}");
                return false;
            }
        }
    }

    /// <summary>唤出原版助手窗口；未运行则先启动。</summary>
    public static void ShowMainWindow()
    {
        var hwnd = FindWindowW(MainWindowClass, null);
        if (hwnd == 0)
        {
            Start();
            return;
        }

        // 跨进程调用：用异步版本避免被原版 UI 线程阻塞。
        ShowWindowAsync(hwnd, SwShow);
        SetForegroundWindow(hwnd);
    }

    /// <summary>打开原版的「更多设置」对话框（热键、广播置顶等都在里面）。</summary>
    public static bool OpenSettings()
    {
        EnsureIni();
        if (!IsAvailable)
        {
            return false;
        }

        try
        {
            // -config 由原版单独处理，不影响正在运行的助手实例。
            Process.Start(new ProcessStartInfo
            {
                FileName = ExePath,
                Arguments = "-config",
                WorkingDirectory = InstallDir,
                UseShellExecute = false
            });
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"打开极域助手设置失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>打开内置助手所在目录，便于查看原版日志或手动替换。</summary>
    public static void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(InstallDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = InstallDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"打开极域助手目录失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 注册「老板键」与「伪装全屏」两个全局热键（主窗口句柄接收 WM_HOTKEY）。
    /// 原版自己的 Ctrl+Alt+F / Ctrl+Alt+D 不受影响；这里注册的热键只是把动作转发给原版或注入侧。
    /// </summary>
    public static void ApplyHotkeys(nint hwnd, AppSettings settings, out bool bossOk, out bool fullscreenOk)
    {
        bossOk = false;
        fullscreenOk = false;
        if (hwnd == 0)
        {
            return;
        }

        UnregisterHotkeys();
        s_hotkeyWindow = hwnd;

        if (settings.JiYuBossKeyEnabled && settings.JiYuBossKeyVk != 0)
        {
            bossOk = RegisterHotKey(
                hwnd, BossHotkeyId, settings.JiYuBossKeyMods | ModNoRepeat, settings.JiYuBossKeyVk);
        }

        if (settings.JiYuFullscreenHotkeyEnabled && settings.JiYuFullscreenHotkeyVk != 0)
        {
            fullscreenOk = RegisterHotKey(
                hwnd, FullscreenHotkeyId,
                settings.JiYuFullscreenHotkeyMods | ModNoRepeat, settings.JiYuFullscreenHotkeyVk);
        }
    }

    public static void UnregisterHotkeys()
    {
        if (s_hotkeyWindow == 0)
        {
            return;
        }

        UnregisterHotKey(s_hotkeyWindow, BossHotkeyId);
        UnregisterHotKey(s_hotkeyWindow, FullscreenHotkeyId);
        s_hotkeyWindow = 0;
    }

    /// <summary>
    /// 把「伪装全屏」热键转发给原版助手：原版用全局原子 HotKeySwFull 注册 WM_HOTKEY，
    /// 这里按同一原子 PostMessage，等价于按下原版的伪全屏热键。
    /// </summary>
    public static bool TriggerFakeFullscreen()
    {
        var target = FindWindowW(MainWindowClass, null);
        var atom = GlobalFindAtomW(FullscreenAtom);
        if (target == 0 || atom == 0)
        {
            return false;
        }

        return PostMessageW(target, WmHotkey, (nint)atom, 0);
    }

    /// <summary>老板键：让注入侧把当前广播窗口拉成全屏并激活（只全屏，不置顶）。</summary>
    public static bool TriggerBossKey()
    {
        // 按热键时茶π基本都是前台进程，把前台权限让给极域进程，
        // 注入侧再 SetForegroundWindow 才能真正激活广播窗口，而不是只让任务栏图标闪一下。
        AllowSetForegroundWindow(AsfwAny);
        return SendHookCommand("hk:boss");
    }

    /// <summary>停止内置助手：先让原版卸载注入（分离极域），再结束本次内置的 JiYuTrainer 进程。</summary>
    /// <param name="detach">是否先分离极域；紧急退出（如保队友）可置 false 以求最快。</param>
    public static void Stop(bool detach = true)
    {
        lock (Gate)
        {
            if (detach)
            {
                Detach();
            }

            foreach (var process in FindOwnedProcesses())
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(1500);
                    }
                }
                catch
                {
                    // 忽略：进程可能已自行退出，或权限不足以结束。
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// 让原版卸载注入：给 Hook DLL 在其宿主（极域学生端）里创建的窗口发 hk:ckend，
    /// 效果等同原版「分离极域退出」——注入停止生效，极域恢复原始控制。
    /// </summary>
    public static bool Detach()
    {
        if (!SendHookCommand("hk:ckend"))
        {
            return false;
        }

        // 原版处理完会销毁该窗口；短暂等待，确认注入确实退出了。
        for (var i = 0; i < 20; i++)
        {
            if (FindWindowW(null, HookWindowTitle) == 0)
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return false;
    }

    /// <summary>给注入 DLL 的接收窗口发一条 WM_COPYDATA 指令。</summary>
    private static bool SendHookCommand(string command)
    {
        var hwnd = FindWindowW(null, HookWindowTitle);
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
            SendMessageTimeoutW(hwnd, WmCopyData, 0, ref data, SmtoAbortIfHung, 1000, out _);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"向极域注入发送指令失败：{ex.Message}");
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>读取一次当前状态（供「小工具」页每两秒轮询）。</summary>
    public static JiYuState Query()
    {
        var processes = FindOwnedProcesses();
        var running = processes.Count > 0 || FindWindowW(MainWindowClass, null) != 0;
        var pid = processes.Count > 0 ? processes[0].Id : 0;
        foreach (var process in processes)
        {
            process.Dispose();
        }

        var injected = FindWindowW(null, HookWindowTitle) != 0;

        var studentPid = 0;
        try
        {
            var students = Process.GetProcessesByName(StudentProcessName);
            if (students.Length > 0)
            {
                studentPid = students[0].Id;
            }

            foreach (var student in students)
            {
                student.Dispose();
            }
        }
        catch
        {
            // 枚举进程失败按「未检测到极域」处理。
        }

        return new JiYuState
        {
            Available = IsAvailable,
            Running = running,
            ProcessId = pid,
            Injected = injected,
            StudentPid = studentPid
        };
    }

    /// <summary>
    /// 写入原版需要的 ini：跳过首次使用的协议弹窗、关闭自动更新与看门狗。
    /// 看门狗必须关掉——否则助手被停止后，注入到极域里的 Hook 会自动把它重新拉起来。
    /// 这里采用「只补/纠固定项」的迁移写入：已存在 ini 时保留用户其它配置，
    /// 只把下面这些由客户端托管的键改写成期望值（否则老用户的 ini 会一直是旧值）。
    /// </summary>
    private static void EnsureIni()
    {
        try
        {
            Directory.CreateDirectory(InstallDir);

            var lines = File.Exists(IniPath)
                ? new List<string>(File.ReadAllLines(IniPath))
                : new List<string>();

            var changed = false;
            changed |= EnsureIniValue(lines, "JTArgeement", "FirstUse", "false");
            changed |= EnsureIniValue(lines, "JTArgeement", "Argeed", "true");
            changed |= EnsureIniValue(lines, "JTSettings", "AlwaysCheckUpdate", "false");
            changed |= EnsureIniValue(lines, "JTSettings", "DoNotShowVirusWindow", "true");
            changed |= EnsureIniValue(lines, "JTSettings", "ForceDisableWatchDog", "true");
            // 置顶策略：广播窗口默认不置顶，只有用户右键「广播窗口置顶」才允许置顶。
            // 原版会把用户点过的「允许置顶」持久化成 true，这里每次启动都压回 FALSE，
            // 避免上一次广播的置顶特权延续到下一次广播。
            changed |= EnsureIniValue(lines, "JTSettings", "AllowGbTop", "false");

            if (changed)
            {
                // 原版用 GetPrivateProfileString 读取，内容全为 ASCII，UTF-8 无 BOM 与 ANSI 等价。
                File.WriteAllLines(IniPath, lines, new UTF8Encoding(false));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"准备极域助手配置失败：{ex.Message}");
        }
    }

    /// <summary>把 section 下的 key 改成期望值；已一致则不修改。返回是否发生改动。</summary>
    private static bool EnsureIniValue(List<string> lines, string section, string key, string value)
    {
        var header = $"[{section}]";
        var headerIndex = lines.FindIndex(line =>
            string.Equals(line.Trim(), header, StringComparison.OrdinalIgnoreCase));

        if (headerIndex < 0)
        {
            if (lines.Count > 0 && lines[^1].Length > 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add(header);
            lines.Add($"{key}={value}");
            return true;
        }

        // 段内查找（遇到下一个段头即结束）。
        var end = lines.Count;
        for (var i = headerIndex + 1; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith('['))
            {
                end = i;
                break;
            }
        }

        for (var i = headerIndex + 1; i < end; i++)
        {
            var trimmed = lines[i].Trim();
            var separator = trimmed.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            if (!string.Equals(trimmed[..separator].Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(trimmed[(separator + 1)..].Trim(), value, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            lines[i] = $"{key}={value}";
            return true;
        }

        lines.Insert(headerIndex + 1, $"{key}={value}");
        return true;
    }

    /// <summary>
    /// 只枚举从本安装目录启动的 JiYuTrainer，避免误杀用户自己另外安装的副本。
    /// </summary>
    private static List<Process> FindOwnedProcesses()
    {
        var owned = new List<Process>();
        Process[] all;
        try
        {
            all = Process.GetProcessesByName(TrainerProcessName);
        }
        catch
        {
            return owned;
        }

        foreach (var process in all)
        {
            var isOwned = false;
            try
            {
                var path = process.MainModule?.FileName;
                isOwned = path is not null &&
                          string.Equals(path, ExePath, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // 取不到路径（权限/位数差异）时保守跳过。
            }

            if (isOwned)
            {
                owned.Add(process);
            }
            else
            {
                process.Dispose();
            }
        }

        return owned;
    }

    private static bool IsRunning
    {
        get
        {
            if (FindWindowW(MainWindowClass, null) != 0)
            {
                return true;
            }

            var processes = FindOwnedProcesses();
            var running = processes.Count > 0;
            foreach (var process in processes)
            {
                process.Dispose();
            }

            return running;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataStruct
    {
        public nuint dwData;
        public uint cbData;
        public nint lpData;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint FindWindowW(string? lpClassName, string? lpWindowName);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindowAsync(nint hWnd, int nCmdShow);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint dwProcessId);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SendMessageTimeoutW(
        nint hWnd, uint msg, nint wParam, ref CopyDataStruct lParam,
        uint flags, uint timeout, out nint result);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint hWnd, int id);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial ushort GlobalFindAtomW(string lpString);
}

/// <summary>「小工具」页展示的极域助手状态快照。</summary>
internal sealed class JiYuState
{
    /// <summary>内置的 JiYuTrainer.exe 是否存在。</summary>
    public bool Available { get; set; }

    /// <summary>内置助手是否在运行。</summary>
    public bool Running { get; set; }

    public int ProcessId { get; set; }

    /// <summary>是否已注入极域学生端（Hook 的接收窗口存在）。</summary>
    public bool Injected { get; set; }

    public int StudentPid { get; set; }

    public bool StudentRunning => StudentPid > 0;
}
