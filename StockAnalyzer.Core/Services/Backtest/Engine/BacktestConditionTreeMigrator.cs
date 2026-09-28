using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Screener;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// Converts a legacy flat <see cref="BacktestConditionEntry"/> list into an equivalent <see cref="BacktestConditionTree"/>.
/// The role/side partition is NOT re-derived here: <see cref="BacktestConditionExecutionPaths.Create"/> stays its single owner,
/// and each of its five paths is folded left to right exactly as <see cref="BacktestConditionEvaluator.Evaluate"/> folds it
/// (entry k's LogicalOperator joins the running result with entry k+1, no precedence; consecutive equal operators share one group), so every path evaluates to the same value.
/// The legacy Exit path is side-agnostic (closes whichever side is held), so it is copied into both ExitLong and ExitShort.
/// An entry that belongs to several paths (Both: entry + exit; Reversal: entry + reverse) appears in each of them.
/// The input is validated by the caller and is never modified; leaf comparisons are fresh entries with Role/Position/LogicalOperator
/// reset to their defaults, while the (immutable-by-convention) Left/Right sides are shared with the input.
/// </summary>
public static class BacktestConditionTreeMigrator
{
    public static BacktestConditionTree Migrate(IReadOnlyList<BacktestConditionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        BacktestConditionExecutionPaths paths = BacktestConditionExecutionPaths.Create(entries);
        return new BacktestConditionTree(
            FoldPath(paths.LongEntry),
            FoldPath(paths.ShortEntry),
            FoldPath(paths.Exit),
            FoldPath(paths.Exit),
            FoldPath(paths.ReverseLong),
            FoldPath(paths.ReverseShort));
    }

    private static BacktestConditionGroup FoldPath(ImmutableArray<BacktestConditionEntry> path)
    {
        if (path.IsEmpty) return BacktestConditionGroup.EmptyAnd;

        // Left fold: (((e0 op0 e1) op1 e2) ...). A run of the SAME operator is one group (And/Or are associative over booleans, and every child is
        // evaluated anyway), so the usual all-And legacy list becomes one flat group instead of a needlessly deep chain; a change of operator nests
        // the running result as the first child of the next group, which is exactly the legacy left-to-right, no-precedence reading.
        var running = new List<IBacktestConditionNode> { ToLeaf(path[0]) };
        LogicalOperator runningOperator = LogicalOperator.And;
        for (int i = 1; i < path.Length; i++)
        {
            LogicalOperator connector = path[i - 1].LogicalOperator;
            if (running.Count == 1)
            {
                runningOperator = connector;
            }
            else if (connector != runningOperator)
            {
                running = new List<IBacktestConditionNode> { new BacktestConditionGroup(runningOperator, running.ToImmutableArray()) };
                runningOperator = connector;
            }
            running.Add(ToLeaf(path[i]));
        }

        return running.Count == 1
            ? new BacktestConditionGroup(LogicalOperator.And, running.ToImmutableArray())
            : new BacktestConditionGroup(runningOperator, running.ToImmutableArray());
    }

    private static BacktestConditionLeaf ToLeaf(BacktestConditionEntry entry) => new(new BacktestConditionEntry
    {
        Left = entry.Left,
        Operator = entry.Operator,
        TargetMode = entry.TargetMode,
        RightNumericValue = entry.RightNumericValue,
        Right = entry.Right,
    });
}
