using System;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models.Screener;

namespace StockAnalyzer.Core.Models.Backtest.Engine;

/// <summary>
/// An AND/OR group of child nodes. <see cref="LogicalOperator.And"/>: every child true; <see cref="LogicalOperator.Or"/>:
/// at least one child true. A group with no children is false. Groups nest freely (the depth/size bounds are enforced by
/// the validator, not here).
/// An empty group only exists as an inactive tree root ("this path does nothing"): the validator rejects a nested empty group, so "AND() is true" never
/// applies to a validated tree. Do NOT simplify an empty group to true: an empty root would then fire an entry on every bar.
/// </summary>
public sealed class BacktestConditionGroup : IBacktestConditionNode
{
    /// <param name="name">The user-entered display name of a nested group ("list name"); trimmed, an empty text means no name. Display-only: it never influences evaluation, the run fingerprint or the reproducibility hash. A root has no name.</param>
    public BacktestConditionGroup(LogicalOperator @operator, ImmutableArray<IBacktestConditionNode> children, string? name = null)
    {
        if (!Enum.IsDefined(typeof(LogicalOperator), @operator))
        {
            throw new ArgumentOutOfRangeException(nameof(@operator), @operator, "LogicalOperator is undefined.");
        }
        if (children.IsDefault)
        {
            throw new ArgumentException("Children must not be a default ImmutableArray.", nameof(children));
        }

        bool hasLeaf = false;
        for (int i = 0; i < children.Length; i++)
        {
            IBacktestConditionNode child = children[i]
                ?? throw new ArgumentException($"Children[{i}] must not be null.", nameof(children));
            hasLeaf |= child switch
            {
                BacktestConditionLeaf => true,
                BacktestConditionGroup group => group.HasLeaf,
                _ => throw new ArgumentException($"Children[{i}] has unsupported node type {child.GetType().Name}.", nameof(children)),
            };
        }

        Name = NormalizeName(name);
        Operator = @operator;
        Children = children;
        HasLeaf = hasLeaf;
    }

    public LogicalOperator Operator { get; }

    /// <summary>The single normalization of a group name: trimmed, and an empty or blank text means "no name" (null).</summary>
    public static string? NormalizeName(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    /// <summary>The display name of this group, or null when it has none.</summary>
    public string? Name { get; }

    public ImmutableArray<IBacktestConditionNode> Children { get; }

    /// <summary>True when this subtree contains at least one <see cref="BacktestConditionLeaf"/>; a subtree without one is inactive.</summary>
    public bool HasLeaf { get; }

    /// <summary>An And group with no children: the inactive state of a tree root.</summary>
    public static BacktestConditionGroup EmptyAnd { get; } = new(LogicalOperator.And, ImmutableArray<IBacktestConditionNode>.Empty);
}
