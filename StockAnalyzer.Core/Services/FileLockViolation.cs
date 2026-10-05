using System.IO;

namespace StockAnalyzer.Core.Services;

/// <summary>The one definition of "another owner holds this file": the exclusive handle of a lease or gate file cannot be opened right now.</summary>
internal static class FileLockViolation
{
    // Win32 ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION, and POSIX EAGAIN.
    private const int SharingViolation = 32, LockViolation = 33, WouldBlock = 11, ErrorCodeMask = 0xffff;

    public static bool IsHeldByAnotherOwner(IOException ex) => (ex.HResult & ErrorCodeMask) is SharingViolation or LockViolation or WouldBlock;
}
