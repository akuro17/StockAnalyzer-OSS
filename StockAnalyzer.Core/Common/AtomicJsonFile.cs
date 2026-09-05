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
    /// <summary>
    /// Serializes <paramref name="data"/> to a temp file next to <paramref name="filePath"/>,
    /// then atomically replaces (or creates) the target. Exceptions propagate to the caller,
    /// which retains its own try/catch for feature-specific error logging.
    /// </summary>
    public static async Task SaveAsync<T>(string filePath, T data, JsonSerializerOptions? options = null)
    {
        string tempPath = filePath + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, data, options);
            }

            if (File.Exists(filePath))
            {
                File.Replace(tempPath, filePath, filePath + ".bak");
            }
            else
            {
                File.Move(tempPath, filePath);
            }
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
    /// Reads and deserializes <paramref name="filePath"/>. Callers are expected to check
    /// <see cref="File.Exists(string)"/> beforehand, since "file absent" typically means
    /// "apply defaults" rather than an error for these settings files.
    /// </summary>
    public static async Task<T?> LoadAsync<T>(string filePath, JsonSerializerOptions? options = null)
    {
        using var stream = File.OpenRead(filePath);
        return await JsonSerializer.DeserializeAsync<T>(stream, options);
    }
}
