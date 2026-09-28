using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Wraps a Windows Job Object handle configured with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE.
/// Disposing (or losing, via process termination) the last handle to the job kills every
/// process still assigned to it -- this is the OS-level safety net for orphaned Python
/// child processes when the parent app is killed abruptly (crash, Task Manager "End Task")
/// rather than shut down gracefully through PythonProcessManager.CleanupConnection's own
/// explicit Process.Kill(entireProcessTree: true).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsJobObjectHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public WindowsJobObjectHandle(IntPtr handle) : base(true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle() => CloseHandle(handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}

internal static class WindowsJobObject
{
    /// <summary>
    /// Creates a new Job Object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE and assigns
    /// <paramref name="process"/> to it. No-op (returns null) on any non-Windows platform,
    /// or if the underlying Win32 calls fail for any reason -- this is a best-effort
    /// hardening layer, not a required part of process startup.
    /// </summary>
    public static WindowsJobObjectHandle? TryAssign(Process process)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;

        try
        {
            var jobHandle = new WindowsJobObjectHandle(NativeMethods.CreateJobObjectW(IntPtr.Zero, null));
            if (jobHandle.IsInvalid) return null;

            var info = new NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            };
            var extendedInfo = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = info
            };

            int length = Marshal.SizeOf(typeof(NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr extendedInfoPtr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(extendedInfo, extendedInfoPtr, false);

                if (!NativeMethods.SetInformationJobObject(jobHandle, NativeMethods.JobObjectExtendedLimitInformation, extendedInfoPtr, (uint)length))
                {
                    jobHandle.Dispose();
                    return null;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(extendedInfoPtr);
            }

            if (!NativeMethods.AssignProcessToJobObject(jobHandle, process.Handle))
            {
                jobHandle.Dispose();
                return null;
            }

            return jobHandle;
        }
        catch
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static class NativeMethods
    {
        public const int JobObjectExtendedLimitInformation = 9;
        public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(WindowsJobObjectHandle hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(WindowsJobObjectHandle hJob, IntPtr hProcess);
    }
}
