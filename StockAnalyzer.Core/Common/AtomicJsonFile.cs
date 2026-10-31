using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Common;

/// <summary>
/// Shared atomic-write/read mechanics for the small per-feature JSON settings files under
/// Data/Config (theme, fonts, notes, python, ...). Each caller keeps its own persistence
/// data shape, defaults, and error handling; this only removes the identical
/// tmp-write-then-replace/move boilerplate that was duplicated across every settings manager.
/// </summary>
public static class AtomicJsonFile
{
    internal const string BackupSuffix = ".bak";
    internal const string TemporarySuffix = ".tmp";

    /// <summary>Buffer size of the temporary-file stream; <see cref="AtomicFileWriter"/> uses the same value.</summary>
    internal const int StreamBufferSize = 4096;

    /// <summary>
    /// Publishes the finished temp file as <paramref name="filePath"/>. Normally <paramref name="replace"/> (<see cref="File.Replace(string, string, string?)"/>)
    /// swaps it in atomically; when that is not supported or fails with an I/O error (drive roots such as <c>Y:\</c> or a volume
    /// boundary) the temp file is moved over the target instead, so a save is not lost. The fallback keeps no <c>.bak</c> copy.
    /// </summary>
    internal static void Commit(string tempPath, string filePath, string? backupPath, Action<string, string, string?> replace)
    {
        if (!File.Exists(filePath))
        {
            File.Move(tempPath, filePath);
            return;
        }

        try
        {
            replace(tempPath, filePath, backupPath);
        }
        catch (Exception error) when (error is IOException or PlatformNotSupportedException)
        {
            File.Move(tempPath, filePath, overwrite: true);
        }
    }

    /// <summary>
    /// Serializes <paramref name="data"/> to a temp file next to <paramref name="filePath"/>,
    /// then atomically replaces (or creates) the target. Exceptions propagate to the caller,
    /// which retains its own try/catch for feature-specific error logging.
    /// </summary>
    public static async Task SaveAsync<T>(string filePath, T data, JsonSerializerOptions? options = null, bool preserveBackup = false)
    {
        string tempPath = filePath + TemporarySuffix;
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, StreamBufferSize, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, data, options);
            }

            Commit(tempPath, filePath, preserveBackup ? null : filePath + BackupSuffix, File.Replace);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* Ignore cleanup errors */ }
            }
        }
    }

    /// <summary>
    /// Synchronous counterpart of <see cref="SaveAsync{T}"/> with the same temp-write-then-replace/move semantics
    /// (including the <c>.bak</c> backup unless <paramref name="preserveBackup"/> is true). For callers that cannot await;
    /// <see cref="SaveAsync{T}"/> is unchanged. Exceptions propagate to the caller.
    /// </summary>
    public static void Save<T>(string filePath, T data, JsonSerializerOptions? options = null, bool preserveBackup = false)
    {
        string tempPath = filePath + TemporarySuffix;
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, data, options);
            }

            Commit(tempPath, filePath, preserveBackup ? null : filePath + BackupSuffix, File.Replace);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* Ignore cleanup errors */ }
            }
        }
    }

    /// <summary>
    /// Opens <paramref name="filePath"/> for reading. Same defaults as <see cref="File.OpenRead(string)"/> except the sharing mode:
    /// a reader that does not allow <see cref="FileShare.Delete"/> makes a concurrent <see cref="File.Replace(string, string, string?)"/>
    /// (and the <see cref="File.Move(string, string, bool)"/> fallback of <see cref="Commit"/>) fail on Windows, which would lose a save.
    /// </summary>
    internal static FileStream OpenForRead(string filePath)
        => new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>
    /// Reads and deserializes <paramref name="filePath"/>. Callers are expected to check
    /// <see cref="File.Exists(string)"/> beforehand, since "file absent" typically means
    /// "apply defaults" rather than an error for these settings files.
    /// </summary>
    public static async Task<T?> LoadAsync<T>(string filePath, JsonSerializerOptions? options = null)
    {
        using var stream = OpenForRead(filePath);
        return await JsonSerializer.DeserializeAsync<T>(stream, options);
    }
}
