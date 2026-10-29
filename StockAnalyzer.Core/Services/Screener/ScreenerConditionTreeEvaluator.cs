using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;

namespace StockAnalyzer.Core.Services.Screener;

/// <summary>
/// Pure evaluator for a Filters <see cref="IScreenerConditionNode"/> tree, for one symbol's candle series.
/// Mirrors <c>BacktestConditionTreeEvaluator</c>'s recursive AND/OR fold in <c>StockAnalyzer.Core.Services.Backtest.Engine</c>
/// (no short-circuit, an empty group is false), extended with one Screener-specific rule that Backtest's tree has no
/// equivalent for: a disabled leaf (<see cref="ScreenerIndicatorEntry.IsEnabled"/> false) is treated as ABSENT from its
/// parent group rather than as a false child, so a disabled Filter is ignored exactly as it was under the previous flat
/// list (<c>ScreenerViewModel.ScanAsync</c> used to build <c>activeEntries</c> by filtering out disabled entries before
/// evaluating at all). A group whose every descendant leaf is currently disabled therefore has no active children and,
/// per <see cref="ScreenerConditionGroup"/>'s "empty group is false" rule, evaluates to false rather than vacuously true.
/// </summary>
public static class ScreenerConditionTreeEvaluator
{
    /// <summary>
    /// Evaluates <paramref name="root"/> for one symbol. Returns false when the tree has no active (enabled) leaf at all
    /// (an empty root, or a root whose every leaf is disabled) - the same outcome the previous flat-list implementation
    /// reached by never scanning when zero entries were enabled.
    /// </summary>
    public static bool Evaluate(IScreenerConditionNode root, IReadOnlyList<CandleData> candles, TickerMetadata metadata)
        => Evaluate(root, candles, metadata, null);

    /// <summary>
    /// <see cref="Evaluate(IScreenerConditionNode, IReadOnlyList{CandleData}, TickerMetadata)"/> with the bars of the comparison symbols the indicators
    /// that compare two tickers read; null keeps them without a value (the Screener tab's rule).
    /// </summary>
    public static bool Evaluate(IScreenerConditionNode root, IReadOnlyList<CandleData> candles, TickerMetadata metadata, IScreenerSecondaryCandles? secondary)
        => EvaluateOrAbsent(root, candles, metadata, secondary) ?? false;

    /// <summary>
    /// <see cref="Evaluate(IScreenerConditionNode, IReadOnlyList{CandleData}, TickerMetadata, IScreenerSecondaryCandles?)"/> for a tree whose sides read the bars of their
    /// own time frames from <paramref name="bars"/> (<see cref="ScreenerIndicatorEntry.IsMetOnBars"/>). The same AND/OR fold: a disabled leaf is absent, no short
    /// circuit, an empty or all-absent group (and so an all-absent root) is false. Both evaluations share the one fold below; a test pins that they give the same
    /// answer for the same tree on D1 bars.
    /// </summary>
    public static bool EvaluateOnBars(IScreenerConditionNode root, IScreenerBarSet bars, TickerMetadata metadata, IScreenerSecondaryCandles? secondary = null)
        => EvaluateOrAbsentOnBars(root, bars, metadata, secondary) ?? false;

    /// <summary>True when <paramref name="node"/>'s subtree contains at least one currently-enabled leaf. Used to reproduce the
    /// previous flat-list guard ("please register at least one condition") that used to gate <c>ScanAsync</c> before any scan ran.</summary>
    public static bool HasActiveLeaf(IScreenerConditionNode node)
    {
        switch (node)
        {
            case ScreenerConditionLeaf leaf:
                return leaf.Entry.IsEnabled;
            case ScreenerConditionGroup group:
                for (int i = 0; i < group.Children.Length; i++)
                {
                    if (HasActiveLeaf(group.Children[i])) return true;
                }
                return false;
            default:
                return false;
        }
    }

    /// <summary>How one enabled leaf is evaluated; a struct so that the fold is specialised per kind (no delegate) and the bars and the metadata are held once, not copied per node.</summary>
    private interface ILeafEvaluator
    {
        bool IsMet(ScreenerIndicatorEntry entry);
    }

    /// <summary>The leaf of the daily evaluation: one candle list.</summary>
    private readonly struct DailyLeaf : ILeafEvaluator
    {
        private readonly IReadOnlyList<CandleData> _candles;
        private readonly TickerMetadata _metadata;
        private readonly IScreenerSecondaryCandles? _secondary;

        public DailyLeaf(IReadOnlyList<CandleData> candles, TickerMetadata metadata, IScreenerSecondaryCandles? secondary)
        {
            _candles = candles;
            _metadata = metadata;
            _secondary = secondary;
        }

        public bool IsMet(ScreenerIndicatorEntry entry) => entry.IsMet(_candles, _metadata, _secondary);
    }

    /// <summary>The leaf of the evaluation in which each side reads its own time frame: one bar set.</summary>
    private readonly struct BarSetLeaf : ILeafEvaluator
    {
        private readonly IScreenerBarSet _bars;
        private readonly TickerMetadata _metadata;
        private readonly IScreenerSecondaryCandles? _secondary;

        public BarSetLeaf(IScreenerBarSet bars, TickerMetadata metadata, IScreenerSecondaryCandles? secondary)
        {
            _bars = bars;
            _metadata = metadata;
            _secondary = secondary;
        }

        public bool IsMet(ScreenerIndicatorEntry entry) => entry.IsMetOnBars(_bars, _metadata, _secondary);
    }

    private static bool? EvaluateOrAbsentOnBars(IScreenerConditionNode node, IScreenerBarSet bars, TickerMetadata metadata, IScreenerSecondaryCandles? secondary)
        => EvaluateOrAbsent(node, new BarSetLeaf(bars, metadata, secondary));

    private static bool? EvaluateOrAbsent(IScreenerConditionNode node, IReadOnlyList<CandleData> candles, TickerMetadata metadata, IScreenerSecondaryCandles? secondary)
        => EvaluateOrAbsent(node, new DailyLeaf(candles, metadata, secondary));

    /// <summary>The one AND/OR fold. Null means "this subtree contributes nothing" (a disabled leaf, or a group with no active child).</summary>
    private static bool? EvaluateOrAbsent<TLeaf>(IScreenerConditionNode node, in TLeaf leafEvaluator) where TLeaf : struct, ILeafEvaluator
    {
        switch (node)
        {
            case ScreenerConditionLeaf leaf:
                return leaf.Entry.IsEnabled ? leafEvaluator.IsMet(leaf.Entry) : null;

            case ScreenerConditionGroup group:
                bool isAnd = group.Operator == LogicalOperator.And;
                bool? result = null;
                for (int i = 0; i < group.Children.Length; i++)
                {
                    bool? child = EvaluateOrAbsent(group.Children[i], in leafEvaluator);
                    if (child is null) continue;
                    result = result is null ? child.Value : (isAnd ? (result.Value & child.Value) : (result.Value | child.Value));
                }
                return result;

            default:
                throw new ArgumentException(
                    $"Unsupported condition node type {(node is null ? "null" : node.GetType().Name)}.", nameof(node));
        }
    }
}
