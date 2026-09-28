using System;

namespace StockAnalyzer.Core.Models.Screener;

/// <summary>
/// A single comparison inside a Filters condition tree. <see cref="ScreenerIndicatorEntry"/> stays the single source
/// of truth for what one comparison is and how it evaluates (<see cref="ScreenerIndicatorEntry.IsMet(System.Collections.Generic.IReadOnlyList{CandleData}, Portfolio.TickerMetadata)"/>
/// is unchanged and reused as-is). Inside a tree the entry's <see cref="ScreenerIndicatorEntry.LogicalOperator"/> carries no meaning
/// (the parent <see cref="ScreenerConditionGroup"/>'s operator is what combines siblings); it is left at whatever value the entry
/// already had when added to the tree and is never read during tree evaluation.
/// </summary>
public sealed class ScreenerConditionLeaf : IScreenerConditionNode
{
    public ScreenerConditionLeaf(ScreenerIndicatorEntry entry)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
    }

    public ScreenerIndicatorEntry Entry { get; }
}
