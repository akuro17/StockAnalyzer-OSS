using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Common;

/// <summary>
/// Publishes a finished byte block as a file: the content goes to a temporary file next to the target and replaces the target in
/// one step, so a failure (disk full, cancellation, crash) leaves either the old file or the complete new one, never a half-written
/// file. This is the binary counterpart of <see cref="AtomicJsonFile"/> (SA_ARCHITECTURE_RULES section 3, "Atomic Archive &amp;
/// Binary Export Packaging") and uses the same replace-or-move step (<see cref="AtomicJsonFile.Commit"/>).
/// </summary>
public static class AtomicFileWriter
{
    private const string TemporarySuffix = AtomicJsonFile.TemporarySuffix;

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="filePath"/> atomically. Creates the directory when it is missing,
    /// replaces an existing file without leaving a <c>.bak</c> copy, and removes the temporary file on every failure.
    /// An empty <paramref name="content"/> writes an empty file.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> is null, empty or white space.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled; the target is unchanged.</exception>
    public static async Task WriteAllBytesAsync(string filePath, ReadOnlyMemory<byte> content, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("File path must not be empty.", nameof(filePath));

        string tempPath = $"{filePath}.{Guid.NewGuid():N}{TemporarySuffix}";
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, AtomicJsonFile.StreamBufferSize, useAsync: true))
            {
                await stream.WriteAsync(content, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            AtomicJsonFile.Commit(tempPath, filePath, backupPath: null, File.Replace);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* Ignore cleanup errors */ }
            }
        }
    }
}
