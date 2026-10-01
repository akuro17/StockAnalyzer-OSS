using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace StockAnalyzer.Core.Services.Notes;

/// <summary>
/// Converts the <c>TickerMetadata.Notes</c> caches to the current format exactly once: format 1 stored a truncated
/// preview, format 2 (<see cref="CurrentFormatVersion"/>) stores the latest Note's full hashtag-free body and applies
/// the "Read more" limits at display time. The conversion (<see cref="TickerMetadataNotesCacheSynchronizer.RecalculateAllNotesCachesAsync"/>)
/// runs only while <see cref="NotesCacheFormatMarker"/> records an older version, and the marker is advanced only when
/// no ticker failed, so an interrupted or partly failed pass is retried on the next start.
/// </summary>
public sealed class NotesCacheFormatMigration
{
    /// <summary>The Notes-cache format this build writes (1 = truncated preview, 2 = full body).</summary>
    public const int CurrentFormatVersion = 2;

    private readonly TickerMetadataNotesCacheSynchronizer _synchronizer;
    private readonly NotesCacheFormatMarker _marker;
    private readonly ILogger<NotesCacheFormatMigration> _logger;

    public NotesCacheFormatMigration(
        TickerMetadataNotesCacheSynchronizer synchronizer,
        NotesCacheFormatMarker marker,
        ILogger<NotesCacheFormatMigration>? logger = null)
    {
        _synchronizer = synchronizer ?? throw new ArgumentNullException(nameof(synchronizer));
        _marker = marker ?? throw new ArgumentNullException(nameof(marker));
        _logger = logger ?? NullLogger<NotesCacheFormatMigration>.Instance;
    }

    /// <returns>The pass result, or null when the caches are already in the current format and nothing was done.</returns>
    public async Task<NotesCacheBackfillResult?> RunIfNeededAsync(CancellationToken ct = default)
    {
        var recorded = _marker.ReadVersion();
        if (recorded >= CurrentFormatVersion)
        {
            _logger.LogDebug("Notes caches already in format {Version}; conversion skipped.", recorded);
            return null;
        }

        var result = await _synchronizer.RecalculateAllNotesCachesAsync(ct).ConfigureAwait(false);
        if (result.Failed == 0)
        {
            _marker.WriteVersion(CurrentFormatVersion);
        }
        else
        {
            _logger.LogWarning("Notes cache conversion left {Failed} of {Total} ticker(s) unconverted; it will be retried on the next start.", result.Failed, result.Total);
        }

        return result;
    }
}
