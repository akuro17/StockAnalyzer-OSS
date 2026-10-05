#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Deletes or moves a test directory tree. A scanner or indexer touching a freshly written file holds it for a moment, so the
/// operation is retried briefly; when the lock does not clear, the exception names the processes that hold it (R2-5 was found
/// this way: the holder was a Python child of another test that had inherited an ONNX file handle).
/// </summary>
internal static class TestDirectory
{
    private const int Attempts = 20, RetryDelayMilliseconds = 100;

    public static void Delete(string path) => Retry("delete", path, () =>
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    });

    public static void Move(string source, string destination) => Retry("move", source, () => Directory.Move(source, destination));

    private static void Retry(string operation, string path, Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { action(); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= Attempts)
                    throw new IOException($"Could not {operation} '{path}' after {Attempts} attempts. Holders: {FileLockProbe.Describe(path)}", ex);
                Thread.Sleep(RetryDelayMilliseconds);
            }
        }
    }
}

/// <summary>Names the processes that hold an open handle on any file below a path (Windows Restart Manager).</summary>
internal static class FileLockProbe
{
    private const int ErrorMoreData = 234, SessionKeyLength = 33, MaxAppNameLength = 256, MaxServiceNameLength = 64, MaxProbedFiles = 64, CommandLineQueryMilliseconds = 10_000;

    public static string Describe(string path)
    {
        if (!OperatingSystem.IsWindows()) return "(not Windows)";
        try
        {
            var files = File.Exists(path) ? [path]
                : Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Take(MaxProbedFiles).ToArray() : [];
            if (files.Length == 0) return "(no files)";
            var key = new StringBuilder(SessionKeyLength);
            if (RmStartSession(out var session, 0, key) != 0) return "(restart manager unavailable)";
            try
            {
                if (RmRegisterResources(session, (uint)files.Length, files, 0, null, 0, null) != 0) return "(register failed)";
                uint needed = 0, count = 0, reasons = 0;
                var result = RmGetList(session, out needed, ref count, null, ref reasons);
                if (result == 0 && needed == 0) return "(none at probe time)";
                if (result != ErrorMoreData) return $"(list failed {result})";
                var info = new RmProcessInfo[needed];
                count = needed;
                if (RmGetList(session, out needed, ref count, info, ref reasons) != 0) return "(list failed)";
                return string.Join(", ", info.Take((int)count).Select(p =>
                    $"{p.AppName}#{p.Process.ProcessId}{(p.Process.ProcessId == Environment.ProcessId ? "(this test host)" : "")} {Detail(p.Process.ProcessId)}"));
            }
            finally { RmEndSession(session); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            return "(probe failed: " + ex.GetType().Name + ")";
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> Details = new();

    /// <summary>Executable, start time, parent and command line of a holder: enough to tell which test or tool started it.</summary>
    private static string Detail(int processId) => Details.GetOrAdd(processId, id =>
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(id);
            var info = new ProcessBasicInformation();
            var parent = NtQueryInformationProcess(process.Handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0
                ? (int)info.InheritedFromUniqueProcessId : -1;
            return $"[started {process.StartTime:HH:mm:ss.fff}, parent #{parent}{(parent == Environment.ProcessId ? " (this test host)" : "")}, cmd: {CommandLine(id)}]";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            return "[process gone or inaccessible]";
        }
    });

    private static string CommandLine(int processId)
    {
        var start = new System.Diagnostics.ProcessStartInfo("powershell",
            $"-NoProfile -Command \"(Get-CimInstance Win32_Process -Filter 'ProcessId={processId}').CommandLine\"")
        { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var query = System.Diagnostics.Process.Start(start);
        if (query is null || !query.WaitForExit(CommandLineQueryMilliseconds)) return "(query timed out)";
        return query.StandardOutput.ReadToEnd().Trim();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, ref ProcessBasicInformation information, int length, out int returned);

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxAppNameLength)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxServiceNameLength)] public string ServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TsSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, StringBuilder key);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint fileCount, string[] files, uint applicationCount,
        RmUniqueProcess[]? applications, uint serviceCount, string[]? services);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RmProcessInfo[]? processes, ref uint reasons);
}
