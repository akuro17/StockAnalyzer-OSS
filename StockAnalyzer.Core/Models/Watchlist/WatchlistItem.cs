using System;

namespace StockAnalyzer.Core.Models.Watchlist;

/// <summary>
/// Represents a single item in a watchlist.
/// This record is immutable and ensures normalization of the ticker.
/// </summary>
public sealed record WatchlistItem
{
    public string Ticker { get; init; }
    public DateTimeOffset AddedAtUtc { get; init; }

    public WatchlistItem(string ticker, DateTimeOffset addedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(ticker))
            throw new ArgumentException("Ticker cannot be null or empty.", nameof(ticker));

        if (addedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("AddedAtUtc must be in UTC.", nameof(addedAtUtc));

        Ticker = ticker.Trim().ToUpperInvariant();
        AddedAtUtc = addedAtUtc;
    }

}
