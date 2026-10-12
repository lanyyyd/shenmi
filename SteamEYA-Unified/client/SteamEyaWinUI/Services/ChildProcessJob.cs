using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 用作业对象（Job Object）把本机拉起的子进程（离线网关 / 云游戏引擎）绑定到当前进程的生命周期：
/// 只要本进程结束（正常退出、崩溃、或被任务管理器结束），系统就会一并结束这些子进程，
/// 避免出现 steameya-engine.exe / steameya-gateway.exe 残留（PyInstaller onefile 会派生多个进程）。
/// 分配失败时静默降级——应用退出时仍由 <see cref="EngineLauncher.Stop"/> / <see cref="GatewayLauncher.Stop"/> 显式清理。
/// </summary>
internal static partial class ChildProcessJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private static nint _job;

    /// <summary>把子进程加入作业对象。</summary>
    public static void Assign(Process process)
    {
        try
        {
            var job = EnsureJob();
            if (job != 0)
            {
                AssignProcessToJobObject(job, process.Handle);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"子进程加入作业对象失败（退出时仍会显式结束）：{ex.Message}");
        }
    }

    private static nint EnsureJob()
    {
        if (_job != 0)
        {
            return _job;
        }

        var job = CreateJobObjectW(0, 0);
        if (job == 0)
        {
            return 0;
        }

        var info = new JobObjectExtendedLimitInformation_
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };

        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation_>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size))
            {
                CloseHandle(job);
                return 0;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }

        _job = job;
        return _job;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation_
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateJobObjectW(nint lpJobAttributes, nint lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        nint hJob, int jobObjectInformationClass, nint lpJobObjectInformation, uint cbJobObjectInformationLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);
}
