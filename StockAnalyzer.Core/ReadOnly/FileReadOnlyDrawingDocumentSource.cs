using System.Text.Json;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.ReadOnly;

/// <summary>
/// Reads <c>Data/Drawings/{ticker}.{Daily|Weekly|Monthly}.json</c> the way the chart's repository does (same file name rule, same schema check, same JSON options) but opened with
/// <see cref="FileShare.ReadWrite"/> | <see cref="FileShare.Delete"/> so the application can keep saving. Unlike the repository, a damaged file is never copied to a <c>.corrupted</c> file:
/// it is <see cref="DataUnavailableException"/>. A legacy file (no "version") is reported, never converted. A file larger than the byte limit is not read into memory
/// (<see cref="DataUnavailableException"/>); the limit is a safety bound the host passes (the MCP server passes McpLimits.MaxDrawingFileBytes).
/// </summary>
public sealed class FileReadOnlyDrawingDocumentSource : IReadOnlyDrawingDocumentSource
{
    private const string VersionProperty = "version";

    /// <summary>
    /// The byte limit a source has when its host names none: 1 MiB. Measured on 2026-11-17: the largest saved drawing file of the application's own data folder was about 17 KB (the 291 files of the
    /// development folder are at most 2.6 KB), so the limit is about 60 times the largest file seen and stops only a file that is not a drawing document.
    /// </summary>
    public const int DefaultMaxFileBytes = 1024 * 1024;

    private readonly string _drawingsDirectory;
    private readonly int _maxFileBytes;

    public FileReadOnlyDrawingDocumentSource(string drawingsDirectory, int maxFileBytes = DefaultMaxFileBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFileBytes);
        _drawingsDirectory = drawingsDirectory ?? throw new ArgumentNullException(nameof(drawingsDirectory));
        _maxFileBytes = maxFileBytes;
    }

    /// <summary>The source of the application's own drawing folder (resolved without creating it), with the given byte limit.</summary>
    public static FileReadOnlyDrawingDocumentSource ForApplicationData(int maxFileBytes = DefaultMaxFileBytes)
        => new(PathDiscovery.ResolveDataPathWithoutCreating(null, DrawingFileNames.FolderName), maxFileBytes);

    public async Task<DrawingDocumentReadResult> ReadAsync(string symbol, TimeFrame timeFrame, CancellationToken cancellationToken = default)
    {
        if (!MonitoringTimeframes.TryFrom(timeFrame, out TimeframeType timeframeType))
        {
            throw new ArgumentOutOfRangeException(nameof(timeFrame), timeFrame, "Drawings are stored per daily, weekly and monthly chart only.");
        }

        if (DrawingFileNames.TryCreate(symbol, timeframeType, out string normalizedTicker, out string fileName) != DrawingFileNameRefusal.None)
        {
            return new DrawingDocumentReadResult(DrawingDocumentReadStatus.NotFound);
        }

        string path = Path.Combine(_drawingsDirectory, fileName);
        try
        {
            if (!File.Exists(path))
            {
                return new DrawingDocumentReadResult(DrawingDocumentReadStatus.NotFound);
            }

            string json;
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true))
            {
                // The size is read from the opened file, so the file that is measured is the file that is read.
                if (stream.Length > _maxFileBytes)
                {
                    throw new DataUnavailableException($"The drawing file is larger than the limit of {_maxFileBytes} bytes.");
                }

                using var reader = new StreamReader(stream);
                json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                throw new DataUnavailableException("The drawing file is empty.");
            }

            using (JsonDocument document = JsonDocument.Parse(json))
            {
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(VersionProperty, out JsonElement versionElement))
                {
                    return new DrawingDocumentReadResult(DrawingDocumentReadStatus.LegacyFormat);
                }

                int version = versionElement.GetInt32();
                if (version != DrawingDocumentState.CurrentSchemaVersion)
                {
                    throw new DataUnavailableException($"The drawing file has schema version {version}; this reader supports version {DrawingDocumentState.CurrentSchemaVersion}.");
                }
            }

            DrawingDocumentState state = JsonSerializer.Deserialize<DrawingDocumentState>(json, DrawingDocumentJsonOptions.Create());
            if (state.Key != new DrawingDocumentKey(normalizedTicker, timeframeType))
            {
                throw new DataUnavailableException("The drawing file belongs to another symbol or time frame.");
            }

            return new DrawingDocumentReadResult(DrawingDocumentReadStatus.Read, state);
        }
        catch (FileNotFoundException)
        {
            return new DrawingDocumentReadResult(DrawingDocumentReadStatus.NotFound); // replaced between the check and the open
        }
        catch (DirectoryNotFoundException)
        {
            return new DrawingDocumentReadResult(DrawingDocumentReadStatus.NotFound);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or InvalidOperationException or ArgumentException or FormatException)
        {
            throw new DataUnavailableException("The drawing file cannot be read.", error);
        }
    }
}
