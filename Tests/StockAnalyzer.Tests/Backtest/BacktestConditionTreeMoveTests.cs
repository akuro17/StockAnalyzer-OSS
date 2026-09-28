using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Screener;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>
/// Moving one comparison inside its own fixed root (drag and drop, MP-C): where it lands, what is removed, what is rejected, and that a move that changes nothing
/// or is not allowed leaves the tree and its edit counter untouched.
/// </summary>
public class BacktestConditionTreeMoveTests
{
    private static BacktestConditionTreeViewModel Create() => new(6, 40);

    private static BacktestConditionEntry Entry(decimal value) => new()
    {
        Left = new BacktestConditionSide { IndicatorType = IndicatorType.Price, PriceSource = PriceType.Close },
        Operator = ComparisonOperator.GreaterThan,
        TargetMode = RightHandTargetMode.NumericValue,
        RightNumericValue = value,
    };

    private static BacktestConditionGroupViewModel Root(BacktestConditionTreeViewModel tree, BacktestConditionSection section = BacktestConditionSection.Entry, TradeSide side = TradeSide.Long)
        => tree.Root(section, side);

    private static BacktestConditionLeafViewModel AddLeaf(BacktestConditionTreeViewModel tree, BacktestConditionGroupViewModel group, decimal value)
    {
        tree.SelectedNode = group;
        BacktestConditionGroupViewModel? target = tree.TryAddLeaf(group.Section, group.Side, Entry(value));
        Assert.Same(group, target);
        tree.SelectedNode = null;
        return (BacktestConditionLeafViewModel)group.Children[^1];
    }

    private static BacktestConditionGroupViewModel AddGroup(BacktestConditionTreeViewModel tree, BacktestConditionGroupViewModel parent, string? name = null)
    {
        Assert.True(tree.TryAddGroup(parent, LogicalOperator.Or, name));
        return (BacktestConditionGroupViewModel)parent.Children[^1];
    }

    /// <summary>The children of a group as text: a leaf as its right-hand number, a group as "[name](...)".</summary>
    private static string Shape(BacktestConditionGroupViewModel group) => string.Join(",", group.Children.Select(c => c switch
    {
        BacktestConditionLeafViewModel leaf => leaf.Entry.RightNumericValue.ToString(),
        BacktestConditionGroupViewModel g => $"[{g.Name}]({Shape(g)})",
        _ => "?",
    }));

    // ---- reorder inside one group ----

    [Fact]
    public void AfterALaterSibling_MovesTheLeafBehindIt()
    {
        BacktestConditionTreeViewModel tree = Create();
        BacktestConditionGroupViewModel root = Root(tree);
        BacktestConditionLeafViewModel a = AddLeaf(tree, root, 1), b = AddLeaf(tree, root, 2), c = AddLeaf(tree, root, 3);

        Assert.Equal(ConditionMoveResult.Moved, tree.MoveLeaf(a, c, ConditionDropPlacement.After));

        Assert.Equal("2,3,1", Shape(root));
    }

    [Fact]
    public void BeforeAnEarlierSibling_MovesTheLeafInFrontOfIt()
    {
        BacktestConditionTreeViewModel tree = Create();
        BacktestConditionGroupViewModel root = Root(tree);
        BacktestConditionLeafViewModel a = AddLeaf(tree, root, 1), b = AddLeaf(tree, root, 2), c = AddLeaf(tree, root, 3);

        Assert.Equal(ConditionMoveResult.Moved, tree.MoveLeaf(c, a, ConditionDropPlacement.Before));

        Assert.Equal("3,1,2", Shape(root));
    }

    [Theory]
    [InlineData(0, 1, ConditionDropPlacement.Before)] // just before the next sibling = where it already is
    [InlineData(1, 0, ConditionDropPlacement.After)]  // just after the previous sibling = where it already is
    [InlineData(0, 0, ConditionDropPlacement.Before)] // onto itself
    [InlineData(2, 2, ConditionDropPlacement.After)]  // onto itself
    public void ADropThatLeavesTheLeafWhereItIs_ChangesNothing_AndCountsNoEdit(int leafIndex, int targetIndex, ConditionDropPlacement placement)
    {
        BacktestConditionTreeViewModel tree = Create();
        BacktestConditionGroupViewModel root = Root(tree);
        AddLeaf(tree, root, 1);
        AddLeaf(tree, root, 2);
        AddLeaf(tree, root, 3);
        int revision = tree.Revision;

        Assert.Equal(ConditionMoveResult.Unchanged, tree.MoveLeaf(root.Children[leafIndex], root.Children[targetIndex], placement));

        Assert.Equal("1,2,3", Shape(root));
        Assert.Equal(revision, tree.Revision);
    }

    [Fact]
    public void DroppingOnTheLastPlaceOfItsOwnGroup_ChangesNothing()
    {
        BacktestConditionTreeViewModel tree = Create();
        BacktestConditionGroupViewModel root = Root(tree);
        AddLeaf(tree, root, 1);
        BacktestConditionLeafViewModel last = AddLeaf(tree, root, 2);
        int revision = tree.Revision;

        Assert.Equal(ConditionMoveResult.Unchanged, tree.MoveLeaf(last, root, ConditionDropPlacement.Inside));

        Assert.Equal("1,2", Shape(root));
        Assert.Equal(revision, tree.Revision);
    }

    [Fact]
    public void DroppingAnEarlierLeafOnItsOwnGroup_MovesItToTheEnd()
    {
        BacktestConditionTreeViewModel tree = Create();
        BacktestConditionGroupViewModel root = Root(tree);
        BacktestConditionLeafViewModel first = AddLeaf(tree, root, 1);
        AddLeaf(tree, root, 2);
        AddLeaf(tree, root, 3);

        Assert.Equal(ConditionMoveResult.Moved, tree.MoveLeaf(first, root, ConditionDropPlacement.Inside));

        Assert.Equal("2,3,1", Shape(root));
    }

    // ---- between groups of the same root ----

    [Fact]
    public void IntoANestedGroup_AppendsThere_OpensIt_KeepsTheSelection_AndCountsOneEdit()
    {
        BacktestConditionTreeViewModel tree = Create();
        BacktestConditionGroupViewModel root = Root(tree);
        BacktestConditionLeafViewModel a = AddLeaf(tree, root, 1);
        BacktestConditionGroupViewModel group = AddGroup(tree, root, "MA");
        AddLeaf(tree, group, 2);
        group.IsExpanded = false;
        int revision = tree.Revision;
        int structureChanges = 0;
        tree.StructureChanged += (_, _) => structureChanges++;

        Assert.Equal(ConditionMoveResult.Moved, tree.MoveLeaf(a, group, ConditionDropPlacement.Inside));

        Assert.Equal("[MA](2,1)", Shape(root));
        Assert.Same(group, a.Parent);
        Assert.True(group.IsExpanded);
        Assert.Same(a, tree.SelectedNode);
        Assert.Equal(revision + 1, tree.Revision);
        Assert.Equal(1, structureChanges);
    }

    [Fact]
    public void BeforeALeafOfANestedGroup_InsertsThereAndKeepsTheOrder()
    {
        BacktestConditionTreeViewModel tree = Create();
        BacktestConditionGroupViewModel root = Root(tree);
        BacktestConditionLeafViewModel a = AddLeaf(tree, root, 1);
        BacktestConditionGroupViewModel group = AddGroup(tree, root, "MA");
        AddLeaf(tree, group, 2);
        BacktestConditionLeafViewModel c = AddLeaf(tree, group, 3);

        Assert.Equal(ConditionMoveResult.Moved, tree.MoveLeaf(a, c, ConditionDropPlacement.Before));

        Assert.Equal("[MA](2,1,3)", Shape(root));
    }

    [Fact]
    public void OutOfANestedGroupIntoTheRoot_AppendsToTheRoot()
    {
        BacktestConditionTreeViewModel tree = Create();
        BacktestConditionGroupViewModel root = Root(tree);
        AddLeaf(tree, root, 1);
        BacktestConditionGroupViewModel group = AddGroup(tree, root, "MA");
        BacktestConditionLeafViewModel inner = AddLeaf(tree, group, 2);
        AddLeaf(tree, group, 3);

        Assert.Equal(ConditionMoveResult.Moved, tree.MoveLeaf(inner, root, ConditionDropPlacement.Inside));

        Assert.Equal("1,[MA](3),2", Shape(root));
    }

    [Fact]
    public void TheOnlyLeafOfAGroup_MovesOut_AndTheEmptiedGroupsAreRemovedUpwards_ButNeverTheRoot()
    {
        BacktestConditionTreeViewModel tree = Create();
        BacktestConditionGroupViewModel root = Root(tree);
        BacktestConditionLeafViewModel keep = AddLeaf(tree, root, 1);
        BacktestConditionGroupViewModel outer = AddGroup(tree, root, "outer");
        BacktestConditionGroupViewModel inner = AddGroup(tree, outer, "inner");
        BacktestConditionLeafViewModel only = AddLeaf(tree, inner, 2);

        Assert.Equal(ConditionMoveResult.Moved, tree.MoveLeaf(only, keep, ConditionDropPlacement.After));

        Assert.Equal("1,2", Shape(root));
        Assert.Null(outer.Parent);
        Assert.Null(inner.Parent);
        Assert.Same(root, only.Parent);
    }

    [Fact]
    public void TheOnlyLeafOfTheRoot_CanMoveIntoAGroupOfTheRoot_LeavingTheRootEmptyOfLeaves()
    {
        BacktestConditionTreeViewModel tree = Create();
        BacktestConditionGroupViewModel root = Root(tree);
        BacktestConditionGroupViewModel group = AddGroup(tree, root, "MA");
        BacktestConditionLeafViewModel leaf = AddLeaf(tree, root, 1);

        Assert.Equal(ConditionMoveResult.Moved, tree.MoveLeaf(leaf, group, ConditionDropPlacement.Inside));

        Assert.Equal("[MA](1)", Shape(root));
    }

    [Fact]
    public void AMove_DoesNotChangeTheNodeCountOfTheRoot_WhenNoGroupIsRemoved()
    {
        BacktestConditionTreeViewModel tree = Create();
        BacktestConditionGroupViewModel root = Root(tree);
        BacktestConditionLeafViewModel a = AddLeaf(tree, root, 1);
        BacktestConditionGroupViewModel group = AddGroup(tree, root, "MA");
        AddLeaf(tree, group, 2);
        int before = StockAnalyzer.Core.Services.Backtest.Engine.BacktestConditionTreeRule.Measure(tree.Build().EntryLong).NodeCount;

        tree.MoveLeaf(a, group, ConditionDropPlacement.Inside);

        Assert.Equal(before, StockAnalyzer.Core.Services.Backtest.Engine.BacktestConditionTreeRule.Measure(tree.Build().EntryLong).NodeCount);
    }

    [Fact]
    public void TheBuiltTree_FollowsTheMovedOrder()
    {
        BacktestConditionTreeViewModel tree = Create();
        BacktestConditionGroupViewModel root = Root(tree);
        BacktestConditionLeafViewModel a = AddLeaf(tree, root, 1);
        AddLeaf(tree, root, 2);
        AddLeaf(tree, root, 3);

        tree.MoveLeaf(a, root, ConditionDropPlacement.Inside);

        decimal[] values = tree.Build().EntryLong.Children.Cast<BacktestConditionLeaf>().Select(l => l.Comparison.RightNumericValue).ToArray();
        Assert.Equal(new[] { 2m, 3m, 1m }, values);
    }

    // ---- what is rejected ----

    public static IEnumerable<object[]> OtherRoots() => new[]
    {
        new object[] { BacktestConditionSection.Entry, TradeSide.Short },
        new object[] { BacktestConditionSection.Exit, TradeSide.Long },
        new object[] { BacktestConditionSection.Reverse, TradeSide.Long },
    };

    [Theory]
    [MemberData(nameof(OtherRoots))]
    public void AMoveIntoAnotherRoot_IsRejected_AndChangesNothing(BacktestConditionSection section, TradeSide side)
    {
        BacktestConditionTreeViewModel tree = Create();
        BacktestConditionGroupViewModel source = Root(tree);
        BacktestConditionGroupViewModel other = Root(tree, section, side);
        BacktestConditionLeafViewModel leaf = AddLeaf(tree, source, 1);
        BacktestConditionLeafViewModel otherLeaf = AddLeaf(tree, other, 9);
        int revision = tree.Revision;

        Assert.Equal(ConditionMoveResult.Rejected, tree.MoveLeaf(leaf, other, ConditionDropPlacement.Inside));
        Assert.Equal(ConditionMoveResult.Rejected, tree.MoveLeaf(leaf, otherLeaf, ConditionDropPlacement.Before));
        Assert.Equal(ConditionMoveResult.Rejected, tree.EvaluateMove(leaf, otherLeaf, ConditionDropPlacement.After));

        Assert.Equal("1", Shape(source));
        Assert.Equal("9", Shape(other));
        Assert.Equal(revision, tree.Revision);
    }

    [Fact]
    public void AGroupCannotBeMoved()
    {
        BacktestConditionTreeViewModel tree = Create();
        BacktestConditionGroupViewModel root = Root(tree);
        BacktestConditionGroupViewModel group = AddGroup(tree, root, "MA");
        AddLeaf(tree, group, 1);
        BacktestConditionLeafViewModel other = AddLeaf(tree, root, 2);

        Assert.Equal(ConditionMoveResult.Rejected, tree.MoveLeaf(group, other, ConditionDropPlacement.After));
        Assert.Equal(ConditionMoveResult.Rejected, tree.MoveLeaf(root, other, ConditionDropPlacement.After));
        Assert.Equal("[MA](1),2", Shape(root));
    }

    [Fact]
    public void ARootIsNotASiblingTarget_AndALeafIsNotAnInsideTarget()
    {
        BacktestConditionTreeViewModel tree = Create();
        BacktestConditionGroupViewModel root = Root(tree);
        BacktestConditionLeafViewModel a = AddLeaf(tree, root, 1);
        BacktestConditionLeafViewModel b = AddLeaf(tree, root, 2);

        Assert.Equal(ConditionMoveResult.Rejected, tree.EvaluateMove(a, root, ConditionDropPlacement.Before));
        Assert.Equal(ConditionMoveResult.Rejected, tree.EvaluateMove(a, root, ConditionDropPlacement.After));
        Assert.Equal(ConditionMoveResult.Rejected, tree.EvaluateMove(a, b, ConditionDropPlacement.Inside));
        Assert.Equal(ConditionMoveResult.Rejected, tree.EvaluateMove(a, b, (ConditionDropPlacement)99));
        Assert.Equal(ConditionMoveResult.Rejected, tree.EvaluateMove(null, b, ConditionDropPlacement.After));
        Assert.Equal(ConditionMoveResult.Rejected, tree.EvaluateMove(a, null, ConditionDropPlacement.After));
        Assert.Equal("1,2", Shape(root));
    }

    [Fact]
    public void ADetachedLeaf_OrANodeOfAnotherTree_IsRejected()
    {
        BacktestConditionTreeViewModel tree = Create();
        BacktestConditionTreeViewModel other = Create();
        BacktestConditionGroupViewModel root = Root(tree);
        BacktestConditionLeafViewModel a = AddLeaf(tree, root, 1);
        BacktestConditionLeafViewModel b = AddLeaf(tree, root, 2);
        BacktestConditionLeafViewModel foreign = AddLeaf(other, Root(other), 7);
        tree.Delete(b);

        Assert.Equal(ConditionMoveResult.Rejected, tree.MoveLeaf(b, a, ConditionDropPlacement.After));
        Assert.Equal(ConditionMoveResult.Rejected, tree.MoveLeaf(a, b, ConditionDropPlacement.After));
        Assert.Equal(ConditionMoveResult.Rejected, tree.MoveLeaf(foreign, a, ConditionDropPlacement.After));
        Assert.Equal(ConditionMoveResult.Rejected, tree.MoveLeaf(a, foreign, ConditionDropPlacement.After));
        Assert.Equal("1", Shape(root));
        Assert.Equal("7", Shape(Root(other)));
    }

    [Fact]
    public void EvaluateMove_ChangesNothing()
    {
        BacktestConditionTreeViewModel tree = Create();
        BacktestConditionGroupViewModel root = Root(tree);
        BacktestConditionLeafViewModel a = AddLeaf(tree, root, 1);
        AddLeaf(tree, root, 2);
        int revision = tree.Revision;

        Assert.Equal(ConditionMoveResult.Moved, tree.EvaluateMove(a, root, ConditionDropPlacement.Inside));

        Assert.Equal("1,2", Shape(root));
        Assert.Equal(revision, tree.Revision);
    }
}
