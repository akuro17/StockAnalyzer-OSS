using System;
using System.IO;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Purpose-specific OS file leases of the weekly retraining scheduler (root mutation lease, per-occurrence owner lease).
/// A lease is an exclusive (<see cref="FileShare.None"/>) handle: ownership is the live handle, never the file's existence,
/// a timestamp or a process id. Acquisition is always one immediate attempt and never blocks.
/// </summary>
internal static class SchedulerFileLease
{
    // Win32 ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION, and POSIX EAGAIN; the same set TrainingJobGate treats as "busy".
    private const int SharingViolation = 32, LockViolation = 33, WouldBlock = 11;

    internal static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xffff) is SharingViolation or LockViolation or WouldBlock;

    /// <summary>The lease, or null while another owner holds it. Any other I/O failure propagates.</summary>
    internal static IDisposable? TryAcquire(string path)
    {
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) when (IsSharingViolation(ex)) { return null; }
    }

    /// <summary>True only when a live owner handle exists; an absent or unlocked file proves no owner. Creates nothing.</summary>
    internal static bool IsHeld(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (IOException ex) when (IsSharingViolation(ex)) { return true; }
    }
}
