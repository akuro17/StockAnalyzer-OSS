using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace StockAnalyzer.Core.Services.Notes;

/// <summary>
/// Persistent record of the format version the <c>TickerMetadata.Notes</c> caches were last converted to
/// (<see cref="NotesCacheFormatMigration"/>). Stored as a small JSON file next to notes.db (Data\Notes\), so the marker
/// lives with the Note store the caches are derived from. A missing, unreadable or malformed file means version 0
/// ("never converted"); the conversion is idempotent, so that is always the safe reading.
/// </summary>
public sealed class NotesCacheFormatMarker
{
    /// <summary>File name of the marker inside the Notes data directory.</summary>
    public const string FileName = "notes_cache_format.json";

    private sealed record MarkerDocument(int Version);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger<NotesCacheFormatMarker> _logger;

    public NotesCacheFormatMarker(NoteDatabaseConnectionManager connectionManager, ILogger<NotesCacheFormatMarker>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(connectionManager);
        var directory = Path.GetDirectoryName(connectionManager.DatabasePath)
            ?? throw new InvalidOperationException("The Notes database path has no directory.");
        _path = Path.Combine(directory, FileName);
        _logger = logger ?? NullLogger<NotesCacheFormatMarker>.Instance;
    }

    /// <summary>The recorded version; 0 when nothing valid is recorded.</summary>
    public int ReadVersion()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return 0;
            }

            var document = JsonSerializer.Deserialize<MarkerDocument>(File.ReadAllText(_path, Encoding.UTF8));
            return document is { Version: > 0 } ? document.Version : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Notes cache format marker '{Path}' is unreadable; treating it as not converted.", _path);
            return 0;
        }
    }

    /// <summary>Records <paramref name="version"/> atomically (temporary file, then replace).</summary>
    public void WriteVersion(int version)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);

        var json = JsonSerializer.Serialize(new MarkerDocument(version), JsonOptions);
        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (File.Exists(_path))
        {
            File.Replace(tempPath, _path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tempPath, _path);
        }
    }
}
