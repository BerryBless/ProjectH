using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ProjectH.QA;

// QA-4 backstop: Unity players launched by the tool are put into one Windows Job Object with
// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE. The job handle lives in a static for the whole tool process and is never closed by
// us: when the tool exits for any reason (normal end, crash, hard kill) Windows closes the handle and kills every player
// still in the job. Only Unity players go in: the game server stops itself gracefully through its own ParentPid
// watchdog, which a job kill would cut short.
// Not Windows: no job (the normal cleanup still closes the players).
internal static class KillOnCloseJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private static readonly Lazy<SafeFileHandle?> s_job = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    // 기능: 프로세스를 kill-on-close Job Object에 넣는다(Windows만).
    // 입력: process - 넣을 프로세스.
    // 출력: 넣었으면 true, 아니면 false와 error에 사유(Windows 아님, job 생성 실패, Win32 오류).
    // False (with why) when the process could not be put in the job; the caller logs it and carries on.
    public static bool TryAssign(System.Diagnostics.Process process, out string? error)
    {
        error = null;
        if (!OperatingSystem.IsWindows())
        {
            error = "not Windows";
            return false;
        }
        SafeFileHandle? job = s_job.Value;
        if (job == null || job.IsInvalid)
        {
            error = "the job object could not be created";
            return false;
        }
        if (AssignProcessToJobObject(job, process.Handle)) return true;
        error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
        return false;
    }

    // 기능: KILL_ON_JOB_CLOSE 제한이 걸린 Job Object를 만든다(Lazy 초기화, 프로세스에 하나).
    // 입력: 없음.
    // 출력: Job 핸들. Windows가 아니거나 생성·설정에 실패하면 null.
    private static SafeFileHandle? Create()
    {
        if (!OperatingSystem.IsWindows()) return null;
        SafeFileHandle job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) return null;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                job.Dispose();
                return null;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return job;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
