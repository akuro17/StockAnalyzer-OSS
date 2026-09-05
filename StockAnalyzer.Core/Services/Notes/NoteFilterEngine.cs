using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models.Notes;

namespace StockAnalyzer.Core.Services.Notes;

/// <summary>
/// The resolved filter-bar selection to evaluate against Notes (spec section 5.1). Each ticker-set
/// property already represents the OR-union of its own category; leaving a property <c>null</c>
/// means that dimension is inactive and imposes no constraint. Different dimensions are combined
/// with AND. Resolving the unified scope menu / registered-tag selections into ticker sets requires
/// live data (<c>ITickerStateStore</c>, <c>TickerMetadata</c>) that this engine intentionally does
/// not depend on, keeping <see cref="NoteFilterEngine"/> a pure, dependency-free function group;
/// that resolution is the caller's (NoteTimelineViewModel) responsibility.
/// </summary>
public sealed record NoteFilterCriteria
{
    public static readonly NoteFilterCriteria None = new();

    /// <summary>Tickers belonging to the single selected scope-menu node (Watchlist/Portfolio/
    /// AllTickers/Filter - OR already resolved for multi-ticker nodes). Null = "All Tickers"/no
    /// scope constraint.</summary>
    public ImmutableHashSet<string>? ScopeTickers { get; init; }

    /// <summary>Tickers whose registered TickerMetadata.Tag matches any of the selected tag values (OR already resolved). Null = dimension inactive.</summary>
    public ImmutableHashSet<string>? RegisteredTagTickers { get; init; }

    /// <summary>Tickers accumulated via the "銘柄" search box's multi-select chip UI (OR-matched,
    /// same contract as <see cref="ScopeTickers"/>/<see cref="RegisteredTagTickers"/>). Null = no
    /// Ticker chip selected.</summary>
    public ImmutableHashSet<string>? SelectedTickers { get; init; }

    /// <summary>True when the user explicitly picked "銘柄なし" in the "銘柄" dropdown.</summary>
    public bool NoTickerOnly { get; init; }

    /// <summary>Inclusive lower bound evaluated against Note.CreatedAt (never UpdatedAt/ChartAnchorDate, spec 5.1).</summary>
    public DateTime? PeriodStartInclusive { get; init; }

    /// <summary>Inclusive upper bound evaluated against Note.CreatedAt (never UpdatedAt/ChartAnchorDate, spec 5.1).</summary>
    public DateTime? PeriodEndInclusive { get; init; }

    /// <summary>Case-insensitive substring match against Note.Body.</summary>
    public string? SearchText { get; init; }

    /// <summary>The single hashtag (without leading '#') selected from the unified search
    /// suggestion box. Null = dimension inactive. Note.Hashtags is already normalized by
    /// HashtagExtractor at save time, so this is compared with an ordinal exact match.</summary>
    public string? SelectedHashtag { get; init; }
}

/// <summary>
/// Evaluates the compound Note filter-bar logical expression (spec section 5.1):
/// <c>(Scope) AND (RegisteredTag OR) AND (SelectedTickers OR) AND (period) AND (search) AND (hashtag)</c>.
/// Pure functions only - no I/O, no service dependencies.
/// </summary>
public static class NoteFilterEngine
{
    /// <summary>Evaluates whether a single Note satisfies every active dimension of <paramref name="criteria"/>.</summary>
    public static bool Matches(Note note, NoteFilterCriteria criteria)
    {
        if (!MatchesTickerScope(note, criteria.ScopeTickers))
        {
            return false;
        }

        if (!MatchesTickerScope(note, criteria.RegisteredTagTickers))
        {
            return false;
        }

        if (!MatchesTickerScope(note, criteria.SelectedTickers))
        {
            return false;
        }

        if (criteria.NoTickerOnly && note.RelatedTicker is not null)
        {
            return false;
        }

        if (criteria.PeriodStartInclusive is { } start && note.CreatedAt < start)
        {
            return false;
        }

        if (criteria.PeriodEndInclusive is { } end && note.CreatedAt > end)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(criteria.SearchText) &&
            note.Body.IndexOf(criteria.SearchText, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        if (criteria.SelectedHashtag is not null &&
            !note.Hashtags.Contains(criteria.SelectedHashtag, StringComparer.Ordinal))
        {
            return false;
        }

        return true;
    }

    /// <summary>Applies <see cref="Matches"/> across a collection of Notes.</summary>
    public static IEnumerable<Note> Filter(IEnumerable<Note> notes, NoteFilterCriteria criteria) =>
        notes.Where(note => Matches(note, criteria));

    /// <summary>
    /// A ticker-scoped dimension (Watchlist/Portfolio/RegisteredTag) is inactive when its set is
    /// null. When active, a ticker-less Note (<see cref="Note.RelatedTicker"/> is null) can never
    /// match it - spec section 5.1 explicitly excludes ticker-independent Notes from these filters.
    /// </summary>
    private static bool MatchesTickerScope(Note note, ImmutableHashSet<string>? scopeTickers)
    {
        if (scopeTickers is null)
        {
            return true;
        }

        return note.RelatedTicker is not null &&
               scopeTickers.Contains(note.RelatedTicker, StringComparer.OrdinalIgnoreCase);
    }
}
