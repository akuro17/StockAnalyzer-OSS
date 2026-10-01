using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Models.Notes;

namespace StockAnalyzer.Core.Services.Notes;

/// <summary>
/// Regenerates the derived <c>TickerMetadata.Notes</c> cache (spec section 4.4) from the
/// Note store for a given ticker. The cache holds the latest Note's full hashtag-free body; the
/// "Read more" collapse boundary (Settings &gt; Notes) is applied at display time by the Tickers grid
/// (<see cref="NoteReadMorePreview"/>), so a later threshold change needs no re-save. The Note store is
/// always the source of truth; this direction is one-way only - editing <c>TickerMetadata.Notes</c> directly never feeds back into Notes.
/// </summary>
/// <remarks>
/// Callers must invoke <see cref="RecalculateNotesCacheAsync"/> for the affected ticker(s) after
/// every Note create, edit (Body or RelatedTicker change), soft-delete, and restore (spec section
/// 4.4). A RelatedTicker change requires two calls - one for the old ticker, one for the new one.
/// </remarks>
public sealed class TickerMetadataNotesCacheSynchronizer
{
    private readonly NoteRepository _noteRepository;
    private readonly UserStrategyMetadataRepository _userStrategyMetadataRepository;
    private readonly ILogger<TickerMetadataNotesCacheSynchronizer>? _logger;

    public TickerMetadataNotesCacheSynchronizer(
        NoteRepository noteRepository,
        UserStrategyMetadataRepository userStrategyMetadataRepository,
        ILogger<TickerMetadataNotesCacheSynchronizer>? logger = null)
    {
        _noteRepository = noteRepository;
        _userStrategyMetadataRepository = userStrategyMetadataRepository;
        _logger = logger ?? NullLogger<TickerMetadataNotesCacheSynchronizer>.Instance;
    }

    /// <summary>
    /// Finds the non-deleted Note with the greatest <see cref="Note.CreatedAt"/> (not UpdatedAt -
    /// spec section 4.4) for <paramref name="ticker"/>, writes its hashtag-free, trimmed full body
    /// into TickerMetadata.Notes (null when that text is empty), and clears the field to null when no
    /// such Note exists.
    /// </summary>
    public async Task RecalculateNotesCacheAsync(string ticker, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ticker))
        {
            return;
        }

        var activeNotes = await _noteRepository.GetAllActiveAsync(ct).ConfigureAwait(false);

        Note? latest = null;
        foreach (var note in activeNotes)
        {
            if (!string.Equals(note.RelatedTicker, ticker, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (latest is null || note.CreatedAt > latest.CreatedAt)
            {
                latest = note;
            }
        }

        var preview = latest is null ? null : BuildCacheText(latest.Body);
        try
        {
            await _userStrategyMetadataRepository.SetNotesAsync(ticker, preview, ct).ConfigureAwait(false);
        }
        catch (StrategyLoadFailedException ex)
        {
            // The stored strategy could not be read, so writing would erase it. The Note itself is already saved;
            // only this derived cache stays as it was until the next Note event for the ticker.
            _logger?.LogWarning(ex, "Notes cache of {Ticker} not updated: its stored strategy data could not be read.", ticker);
            return;
        }

        _logger?.LogDebug("Recalculated Notes cache for {Ticker}: hasPreview={HasPreview}.", ticker, preview is not null);
    }

    /// <summary>Hashtag-free trimmed body, or null when nothing remains (same as "no Note").</summary>
    private static string? BuildCacheText(string body)
    {
        var text = HashtagExtractor.RemoveHashtags(body).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// One pass over every active Note: for each ticker that has one, recomputes the cache from its
    /// latest Note (same selection rule as <see cref="RecalculateNotesCacheAsync"/>) and writes it only
    /// when it differs from the stored value (ordinal comparison), so an up-to-date store causes no
    /// write and no StrategyChanged event. Tickers without any active Note are not enumerated here
    /// (the per-event recalculation already clears them). Used to convert caches stored before the
    /// full-body format (which hold a truncated preview). Each ticker is processed on its own: a ticker whose stored
    /// data cannot be read, or whose update throws, is counted in <see cref="NotesCacheBackfillResult.Failed"/> and
    /// left untouched while the remaining tickers are still converted.
    /// </summary>
    public async Task<NotesCacheBackfillResult> RecalculateAllNotesCachesAsync(CancellationToken ct = default)
    {
        var activeNotes = await _noteRepository.GetAllActiveAsync(ct).ConfigureAwait(false);

        var latestByTicker = new Dictionary<string, Note>(StringComparer.OrdinalIgnoreCase);
        foreach (var note in activeNotes)
        {
            if (string.IsNullOrWhiteSpace(note.RelatedTicker))
            {
                continue;
            }

            if (!latestByTicker.TryGetValue(note.RelatedTicker, out var latest) || note.CreatedAt > latest.CreatedAt)
            {
                latestByTicker[note.RelatedTicker] = note;
            }
        }

        var rewritten = 0;
        var failed = 0;
        foreach (var (ticker, latest) in latestByTicker)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var desired = BuildCacheText(latest.Body);
                var existing = await _userStrategyMetadataRepository.GetStrategyAsync(ticker, ct).ConfigureAwait(false);
                if (string.Equals(desired, existing?.Notes, StringComparison.Ordinal))
                {
                    continue;
                }

                // Sets only the Notes field; every other stored strategy field (Long/Short targets, signal flags,
                // Reminder, ...) that UserStrategyMetadataRepository also owns is kept as it is.
                await _userStrategyMetadataRepository.SetNotesAsync(ticker, desired, ct).ConfigureAwait(false);
                rewritten++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                _logger?.LogWarning(ex, "Notes cache of {Ticker} not converted; its stored data is left untouched.", ticker);
            }
        }

        _logger?.LogDebug("Recalculated all Notes caches: {Rewritten} of {Total} tickers rewritten, {Failed} failed.", rewritten, latestByTicker.Count, failed);
        return new NotesCacheBackfillResult(latestByTicker.Count, rewritten, failed);
    }
}
