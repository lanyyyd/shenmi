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
    private const uint SmtoAbortIfHung = 0x0002;
    private const int SwShow = 5;

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
    /// 停止内置助手：先让原版卸载注入（分离极域），再结束本次内置的 JiYuTrainer 进程。
    /// 对应原版退出提示中的「分离极域并退出」，可随时从「小工具」页触发。
    /// </summary>
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
        var hwnd = FindWindowW(null, HookWindowTitle);
        if (hwnd == 0)
        {
            return false;
        }

        const string command = "hk:ckend";
        var buffer = Marshal.StringToHGlobalUni(command);
        try
        {
            var data = new CopyDataStruct
            {
                cbData = (uint)((command.Length + 1) * sizeof(char)),
                lpData = buffer
            };
            SendMessageTimeoutW(hwnd, WmCopyData, 0, ref data, SmtoAbortIfHung, 1000, out _);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"分离极域失败：{ex.Message}");
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
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
    /// 文件已存在时不再覆盖，避免冲掉用户在「更多设置」里的改动。
    /// </summary>
    private static void EnsureIni()
    {
        try
        {
            Directory.CreateDirectory(InstallDir);

            if (File.Exists(IniPath) &&
                File.ReadAllText(IniPath).Contains("Argeed", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            const string template =
                "[JTArgeement]\r\n" +
                "FirstUse=false\r\n" +
                "Argeed=true\r\n" +
                "\r\n" +
                "[JTSettings]\r\n" +
                "AlwaysCheckUpdate=false\r\n" +
                "DoNotShowVirusWindow=true\r\n" +
                "ForceDisableWatchDog=true\r\n";

            // 原版用 GetPrivateProfileString 读取，内容全为 ASCII，UTF-8 无 BOM 与 ANSI 等价。
            File.WriteAllText(IniPath, template, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"准备极域助手配置失败：{ex.Message}");
        }
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
    private static partial nint SendMessageTimeoutW(
        nint hWnd, uint msg, nint wParam, ref CopyDataStruct lParam,
        uint flags, uint timeout, out nint result);
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
