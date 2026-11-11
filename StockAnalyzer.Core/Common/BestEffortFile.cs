using System;
using System.IO;

namespace StockAnalyzer.Core.Common;

/// <summary>
/// The one definition of "delete a temporary file, and never fail because of it": cleanup code in a final block must not replace the error
/// of the work it cleans up after, and a leftover temporary file is harmless.
/// </summary>
internal static class BestEffortFile
{
    /// <summary>
    /// Deletes <paramref name="path"/>. A missing file is not an error. A file that is in use or refused (<see cref="IOException"/>,
    /// <see cref="UnauthorizedAccessException"/>) is left in place without throwing; any other exception still propagates.
    /// </summary>
    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: whatever is left is removed by a later clean-up or is harmless.
        }
    }
}
