using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Watchlist;

/// <summary>
/// Represents a user-defined watchlist profile.
/// This record is immutable and contains a collection of tickers.
/// </summary>
public sealed record WatchlistProfile
{
    public Guid Id { get; init; }
    public string Name { get; init; }
    public IndicatorColor Color { get; init; }
    public bool IsPortfolio { get; init; }
    public IReadOnlyList<WatchlistItem> Items { get; init; }

    public WatchlistProfile(Guid id, string name, IndicatorColor color, bool isPortfolio = false, IReadOnlyList<WatchlistItem>? items = null)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Id cannot be empty.", nameof(id));

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name cannot be null or empty.", nameof(name));

        if (name.Length > 64)
            throw new ArgumentOutOfRangeException(nameof(name), "Name must be 64 characters or less.");

        Id = id;
        Name = name.Trim();
        Color = color;
        IsPortfolio = isPortfolio;
        Items = items ?? Array.Empty<WatchlistItem>();
    }
}
