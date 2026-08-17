using System;
using System.Collections.Immutable;

namespace StockAnalyzer.Core.Models.Notes;

/// <summary>
/// An immutable investment journal entry ("Ticker Note"). The Note store (SQLite) is the
/// single source of truth; <see cref="Portfolio.TickerMetadata.Notes"/> is only ever a
/// derived preview cache generated from these records (spec section 4.4).
/// </summary>
public sealed record Note(
    Guid Id,
    string Body,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    /// <summary>The chart date the user was analyzing when writing this Note. Falls back to CreatedAt's date when unset.</summary>
    public DateTime? ChartAnchorDate { get; init; }

    /// <summary>The chart timeframe (Daily/Weekly/Monthly) open when this Note was written.</summary>
    public TimeFrame? ChartTimeframe { get; init; }

    /// <summary>Single, nullable related ticker. Must reference an existing ticker in the master list when set.</summary>
    public string? RelatedTicker { get; init; }

    /// <summary>Hashtags auto-extracted from Body; not independently editable (spec section 3.1).</summary>
    public ImmutableArray<string> Hashtags { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>Up to 10 attachment references (spec section 8).</summary>
    public ImmutableArray<Guid> AttachmentIds { get; init; } = ImmutableArray<Guid>.Empty;

    /// <summary>http/https URLs only (spec section 8).</summary>
    public ImmutableArray<string> LinkUrls { get; init; } = ImmutableArray<string>.Empty;

    public bool IsPinned { get; init; }

    /// <summary>Soft-delete flag; true means the Note is trash-only (spec section 9.3).</summary>
    public bool IsDeleted { get; init; }

    /// <summary>The Note this one quotes, if created via the Quote action. Mutually independent of <see cref="ParentNoteId"/>.</summary>
    public Guid? QuotedNoteId { get; init; }

    /// <summary>The Note this one directly replies to, if created via the Reply action (forms a thread chain).</summary>
    public Guid? ParentNoteId { get; init; }
}
