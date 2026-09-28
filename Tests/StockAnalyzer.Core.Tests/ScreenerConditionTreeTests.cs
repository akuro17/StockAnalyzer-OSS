using System;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models.Screener;
using Xunit;

namespace StockAnalyzer.Core.Tests;

/// <summary>
/// Objective verification of the Filters condition tree domain model (Y:\Temp\sa_implementation_plan_ScreenerFilterConditionTree.md
/// Task 1), proving the safe-extension/no-regression requirement the user's shared-function-reuse policy demands: these types are
/// new and additive, so this file only needs to prove the new construction/validation contract, not any existing behavior.
/// </summary>
public class ScreenerConditionTreeTests
{
    [Fact]
    public void Group_RejectsUndefinedOperator()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ScreenerConditionGroup((LogicalOperator)99, ImmutableArray<IScreenerConditionNode>.Empty));
    }

    [Fact]
    public void Group_RejectsDefaultImmutableArray()
    {
        Assert.Throws<ArgumentException>(() =>
            new ScreenerConditionGroup(LogicalOperator.And, default));
    }

    [Fact]
    public void Group_RejectsNullChild()
    {
        var children = ImmutableArray.Create<IScreenerConditionNode>((IScreenerConditionNode)null!);
        Assert.Throws<ArgumentException>(() => new ScreenerConditionGroup(LogicalOperator.And, children));
    }

    [Fact]
    public void Group_NameNormalization_BlankBecomesNull()
    {
        var group = new ScreenerConditionGroup(LogicalOperator.And, ImmutableArray<IScreenerConditionNode>.Empty, name: "   ");
        Assert.Null(group.Name);
    }

    [Fact]
    public void Group_NameNormalization_TrimsWhitespace()
    {
        var group = new ScreenerConditionGroup(LogicalOperator.And, ImmutableArray<IScreenerConditionNode>.Empty, name: "  My Group  ");
        Assert.Equal("My Group", group.Name);
    }

    [Fact]
    public void EmptyAnd_HasAndOperatorAndNoChildren()
    {
        Assert.Equal(LogicalOperator.And, ScreenerConditionGroup.EmptyAnd.Operator);
        Assert.Empty(ScreenerConditionGroup.EmptyAnd.Children);
    }

    [Fact]
    public void Leaf_RejectsNullEntry()
    {
        Assert.Throws<ArgumentNullException>(() => new ScreenerConditionLeaf(null!));
    }

    [Fact]
    public void Group_NestsGroupsAndLeavesTogether()
    {
        var entry = new ScreenerIndicatorEntry();
        var leaf = new ScreenerConditionLeaf(entry);
        var nested = new ScreenerConditionGroup(LogicalOperator.Or, ImmutableArray.Create<IScreenerConditionNode>(leaf));
        var root = new ScreenerConditionGroup(LogicalOperator.And, ImmutableArray.Create<IScreenerConditionNode>(nested));

        Assert.Single(root.Children);
        Assert.IsType<ScreenerConditionGroup>(root.Children[0]);
        var child = (ScreenerConditionGroup)root.Children[0];
        Assert.Single(child.Children);
        Assert.Same(leaf, child.Children[0]);
        Assert.Same(entry, ((ScreenerConditionLeaf)child.Children[0]).Entry);
    }
}
