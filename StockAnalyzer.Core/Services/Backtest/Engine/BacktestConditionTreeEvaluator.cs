using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Screener;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// Pure per-bar evaluator for a <see cref="IBacktestConditionNode"/> tree. Leaves are evaluated by
/// <see cref="BacktestConditionEvaluator.EvaluateComparison"/> (reads only <c>barIndex - Offset</c>). Precondition: every leaf Offset is >= 0, which
/// <see cref="BacktestConditionValidator.SnapshotTree"/> enforces; that validated form is what makes a lookahead impossible, and a direct call with a
/// negative Offset on an unvalidated tree is outside this contract.
/// Evaluation contract: a comparison whose value is unavailable (null series value, or an index before the first bar) is false for EVERY operator,
/// including <c>!=</c> (identical to the legacy evaluator; changing it would change results); comparisons are exact decimal (no tolerance, no NaN);
/// an empty group is false (see <see cref="BacktestConditionGroup"/>); a leaf whose side is not in the side map throws <see cref="KeyNotFoundException"/>
/// (a strategy registers every side it evaluates, so a validated tree cannot reach that).
/// Every child of every group is evaluated in stored order even when the group's result is already decided (no
/// short-circuit), so a misconfigured leaf always surfaces exactly as it does in the legacy list evaluation. The measurement taken when this
/// evaluator was introduced did not meet the pre-registered adoption rule for short-circuiting, and no execution-stack guard is placed here
/// (the validator and the strategy's side collection already reject a pathologically deep tree before any evaluation).
/// The hot path allocates nothing: recursion over <c>ImmutableArray</c> with index loops, no LINQ, closures or enumerators.
/// </summary>
public static class BacktestConditionTreeEvaluator
{
    public static bool Evaluate(
        IBacktestConditionNode node,
        IReadOnlyDictionary<BacktestConditionSide, string> sideToIndicatorKey,
        IndicatorSeriesSet indicators,
        int barIndex)
    {
        switch (node)
        {
            case BacktestConditionLeaf leaf:
                return BacktestConditionEvaluator.EvaluateComparison(leaf.Comparison, sideToIndicatorKey, indicators, barIndex);

            case BacktestConditionGroup group:
                if (group.Children.Length == 0) return false;

                bool isAnd = group.Operator == LogicalOperator.And;
                bool result = isAnd;
                for (int i = 0; i < group.Children.Length; i++)
                {
                    bool child = Evaluate(group.Children[i], sideToIndicatorKey, indicators, barIndex);
                    result = isAnd ? (result & child) : (result | child);
                }
                return result;

            default:
                throw new ArgumentException(
                    $"Unsupported condition node type {(node is null ? "null" : node.GetType().Name)}.", nameof(node));
        }
    }
}
