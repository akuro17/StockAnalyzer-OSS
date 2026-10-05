using System;

namespace StockAnalyzer.Avalonia.ViewModels.Backtest;

/// <summary>
/// Sorting direction for the Backtest Trade History table.
/// </summary>
public enum TradeSortOrder
{
    None = 0,
    Ascending = 1,
    Descending = 2
}

/// <summary>
/// Page size choice with localized display string for pagination dropdown.
/// </summary>
public sealed record TradePageSizeOption(int Value, string DisplayText);

/// <summary>
/// Individual token rendered in the pagination button sequence.
/// When <see cref="IsEllipsis"/> is true, this represents an unclickable ellipsis ("...").
/// </summary>
public sealed record TradePaginationToken(int PageNumber, bool IsEllipsis, bool IsActive);
