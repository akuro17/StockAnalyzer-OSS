using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Models.Notes;

namespace StockAnalyzer.Core.Services.Notes;

/// <summary>
/// Regenerates the derived <c>TickerMetadata.Notes</c> preview cache (spec section 4.4) from the
/// Note store for a given ticker. The Note store is always the source of truth; this direction is
/// one-way only - editing <c>TickerMetadata.Notes</c> directly never feeds back into Notes.
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
    private readonly INotesSettingsManager _notesSettingsManager;
    private readonly ILogger<TickerMetadataNotesCacheSynchronizer>? _logger;

    public TickerMetadataNotesCacheSynchronizer(
        NoteRepository noteRepository,
        UserStrategyMetadataRepository userStrategyMetadataRepository,
        INotesSettingsManager notesSettingsManager,
        ILogger<TickerMetadataNotesCacheSynchronizer>? logger = null)
    {
        _noteRepository = noteRepository;
        _userStrategyMetadataRepository = userStrategyMetadataRepository;
        _notesSettingsManager = notesSettingsManager;
        _logger = logger ?? NullLogger<TickerMetadataNotesCacheSynchronizer>.Instance;
    }

    /// <summary>
    /// Finds the non-deleted Note with the greatest <see cref="Note.CreatedAt"/> (not UpdatedAt -
    /// spec section 4.4) for <paramref name="ticker"/>, writes its hashtag-free "Read more" collapse
    /// preview (the same ReadMoreMaxCharacters/ReadMoreMaxLines boundary the Notes tab itself uses,
    /// via <see cref="NoteReadMorePreview"/>) into TickerMetadata.Notes, and clears the field to null
    /// when no such Note exists.
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

        var preview = latest is null ? null : BuildPreview(latest.Body);
        WriteNotesPreview(ticker, preview);

        _logger?.LogDebug("Recalculated Notes cache for {Ticker}: hasPreview={HasPreview}.", ticker, preview is not null);
    }

    private string BuildPreview(string body)
    {
        var withoutHashtags = HashtagExtractor.RemoveHashtags(body).Trim();
        return NoteReadMorePreview.BuildCollapsedText(
            withoutHashtags,
            _notesSettingsManager.ReadMoreMaxCharacters,
            _notesSettingsManager.ReadMoreMaxLines);
    }

    private void WriteNotesPreview(string ticker, string? preview)
    {
        // Only the Notes field is meant to change here; every other strategy field is read back
        // and passed through unchanged so this synchronizer cannot clobber unrelated user data
        // (Long/Short targets, signal flags, etc.) that UserStrategyMetadataRepository also owns.
        var existing = _userStrategyMetadataRepository.GetStrategy(ticker);
        _userStrategyMetadataRepository.SaveStrategy(
            ticker,
            existing?.Long,
            existing?.ExitLong,
            existing?.StopLossLong,
            existing?.Short,
            existing?.ExitShort,
            existing?.StopLossShort,
            preview,
            existing?.IsLong,
            existing?.IsTPLong,
            existing?.IsSLLong,
            existing?.IsShort,
            existing?.IsTPShort,
            existing?.IsSLShort,
            existing?.Reminder);
    }
}
