using System;

namespace StockAnalyzer.Core.Models.Backtest.Engine;

/// <summary>
/// A single comparison inside a <see cref="BacktestConditionTree"/>. <see cref="BacktestConditionEntry"/> stays the
/// single source of truth for what one comparison is. Inside a tree the entry's Role, Position and LogicalOperator
/// carry no meaning (the node's location defines the section/side and its parent group defines the connector); they
/// must be left at their defaults, which the validator enforces when a tree is snapshotted.
/// </summary>
public sealed class BacktestConditionLeaf : IBacktestConditionNode
{
    public BacktestConditionLeaf(BacktestConditionEntry comparison)
    {
        Comparison = comparison ?? throw new ArgumentNullException(nameof(comparison));
    }

    public BacktestConditionEntry Comparison { get; }
}
