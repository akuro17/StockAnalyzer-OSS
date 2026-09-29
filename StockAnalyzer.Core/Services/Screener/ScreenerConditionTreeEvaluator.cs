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
        => EvaluateOrAbsent(root, candles, metadata) ?? false;

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

    /// <summary>Null means "this subtree contributes nothing" (a disabled leaf, or a group with no active child).</summary>
    private static bool? EvaluateOrAbsent(IScreenerConditionNode node, IReadOnlyList<CandleData> candles, TickerMetadata metadata)
    {
        switch (node)
        {
            case ScreenerConditionLeaf leaf:
                return leaf.Entry.IsEnabled ? leaf.Entry.IsMet(candles, metadata) : null;

            case ScreenerConditionGroup group:
                bool isAnd = group.Operator == LogicalOperator.And;
                bool? result = null;
                for (int i = 0; i < group.Children.Length; i++)
                {
                    bool? child = EvaluateOrAbsent(group.Children[i], candles, metadata);
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
