using System;
using System.Collections.Immutable;

namespace StockAnalyzer.Core.Models.Screener;

/// <summary>
/// An AND/OR group of child nodes in a Filters condition tree. <see cref="LogicalOperator.And"/>: every child true;
/// <see cref="LogicalOperator.Or"/>: at least one child true. A group with no children is false (mirrors
/// <c>BacktestConditionGroup</c>'s documented convention in <c>StockAnalyzer.Core.Models.Backtest.Engine</c>: an empty
/// group never evaluates to true, so an empty root or an emptied nested group cannot silently match every symbol).
/// Groups nest freely; a validated editor (<c>ScreenerConditionTreeViewModel</c>) removes a nested group the moment a
/// delete leaves it with zero children, so an empty non-root group is a transient editing state only, never a
/// persisted or evaluated shape in practice.
/// </summary>
public sealed class ScreenerConditionGroup : IScreenerConditionNode
{
    /// <param name="name">The user-entered display name of a nested group; trimmed, an empty text means no name (null). Display-only:
    /// it never influences evaluation. The single root has no name.</param>
    public ScreenerConditionGroup(LogicalOperator @operator, ImmutableArray<IScreenerConditionNode> children, string? name = null)
    {
        if (!Enum.IsDefined(typeof(LogicalOperator), @operator))
        {
            throw new ArgumentOutOfRangeException(nameof(@operator), @operator, "LogicalOperator is undefined.");
        }
        if (children.IsDefault)
        {
            throw new ArgumentException("Children must not be a default ImmutableArray.", nameof(children));
        }

        for (int i = 0; i < children.Length; i++)
        {
            IScreenerConditionNode child = children[i]
                ?? throw new ArgumentException($"Children[{i}] must not be null.", nameof(children));
            if (child is not ScreenerConditionLeaf and not ScreenerConditionGroup)
            {
                throw new ArgumentException($"Children[{i}] has unsupported node type {child.GetType().Name}.", nameof(children));
            }
        }

        Name = NormalizeName(name);
        Operator = @operator;
        Children = children;
    }

    public LogicalOperator Operator { get; }

    /// <summary>The single normalization of a group name: trimmed, and an empty or blank text means "no name" (null).</summary>
    public static string? NormalizeName(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    /// <summary>The display name of this group, or null when it has none.</summary>
    public string? Name { get; }

    public ImmutableArray<IScreenerConditionNode> Children { get; }

    /// <summary>An And group with no children: the inactive state of the tree root (no Filters registered).</summary>
    public static ScreenerConditionGroup EmptyAnd { get; } = new(LogicalOperator.And, ImmutableArray<IScreenerConditionNode>.Empty);
}
