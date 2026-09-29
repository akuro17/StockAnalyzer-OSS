using System;
using StockAnalyzer.Core.Models.Screener;

namespace StockAnalyzer.Avalonia.ViewModels.Screener;

/// <summary>
/// One comparison of the Filters condition tree editor. The entry is not edited in place: a different comparison is
/// a different leaf. <see cref="DisplayText"/> reuses <see cref="ScreenerIndicatorEntry.DisplayName"/> as-is (no new
/// formatter needed - unlike Backtest's <c>BacktestConditionEntry</c>, <see cref="ScreenerIndicatorEntry"/> already
/// computes its own human-readable comparison text).
/// </summary>
public sealed class ScreenerConditionLeafViewModel : ScreenerConditionNodeViewModel
{
    public ScreenerConditionLeafViewModel(ScreenerIndicatorEntry entry)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
    }

    public ScreenerIndicatorEntry Entry { get; }

    public string DisplayText => Entry.DisplayName;
}
