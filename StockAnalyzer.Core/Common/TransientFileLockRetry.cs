using System;
using System.IO;
using System.Threading;
using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Core.Common;

/// <summary>
/// Retries a file-system rename that Windows denies only because another process holds the file for a moment (a scanner or an
/// indexer): Win32 error 5 (access denied) or 32 (sharing violation). Every other failure, and the same codes on another platform,
/// surfaces at once.
/// </summary>
public static class TransientFileLockRetry
{
    private const int Win32Facility = 7;
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;

    public static bool IsTransientLock(Exception error)
    {
        if (!OperatingSystem.IsWindows() || error is not (IOException or UnauthorizedAccessException)) return false;
        int code = error.HResult;
        return ((code >> 16) & 0x1FFF) == Win32Facility && (code & 0xFFFF) is ErrorAccessDenied or ErrorSharingViolation;
    }

    /// <summary>
    /// Runs <paramref name="operation"/>; on a transient lock waits and retries up to the configured attempts, then rethrows the
    /// last error. <paramref name="canRetry"/> is asked before each retry so a changed precondition is never retried blindly.
    /// </summary>
    public static void Run(Action operation, FileReplaceRetrySettings settings, Func<bool>? canRetry = null, Action<TimeSpan>? sleep = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(settings);
        sleep ??= Thread.Sleep;
        for (int attempt = 1; ; attempt++)
        {
            try { operation(); return; }
            catch (Exception error) when (attempt < settings.Attempts && IsTransientLock(error) && (canRetry?.Invoke() ?? true))
            {
                sleep(TimeSpan.FromMilliseconds(settings.DelayMilliseconds));
            }
        }
    }
}
