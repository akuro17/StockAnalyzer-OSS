using System.Linq;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Avalonia.ViewModels.Screener;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Screener;
using Xunit;

namespace StockAnalyzer.Tests.Screener;

/// <summary>
/// Moving one comparison inside the Filters tree's single fixed root (drag and drop,
/// Y:\Temp\sa_implementation_plan_ScreenerFilterConditionDragMove.md Task 3): where it lands, what is removed, what is
/// rejected, and that a move that changes nothing or is not allowed leaves the tree and its edit counter untouched.
/// Mirrors the coverage of Tests\StockAnalyzer.Tests\Backtest\BacktestConditionTreeMoveTests.cs, minus the
/// cross-root-rejection scenarios that do not apply here (Screener has only one root - decision B of
/// sa_implementation_plan_ScreenerFilterConditionTree.md).
/// </summary>
public class ScreenerConditionTreeMoveTests
{
    private static ScreenerConditionTreeViewModel Create() => new(6, 40, 32);

    private static ScreenerIndicatorEntry Entry(decimal value) => new()
    {
        LeftHand = new ScreenerIndicatorSideConfig
        {
            IndicatorType = IndicatorType.SMA,
            Parameters = new System.Collections.Generic.Dictionary<string, object> { { "Period", 1 } },
            TimeFrame = TimeFrame.D1,
            Offset = 0,
        },
        Operator = ComparisonOperator.GreaterThan,
        TargetMode = RightHandTargetMode.NumericValue,
        RightNumericValue = value,
    };

    private static ScreenerConditionLeafViewModel AddLeaf(ScreenerConditionTreeViewModel tree, ScreenerConditionGroupViewModel group, decimal value)
    {
        tree.SelectedNode = group;
        ScreenerConditionGroupViewModel? target = tree.TryAddLeaf(Entry(value));
        Assert.Same(group, target);
        tree.SelectedNode = null;
        return (ScreenerConditionLeafViewModel)group.Children[^1];
    }

    private static ScreenerConditionGroupViewModel AddGroup(ScreenerConditionTreeViewModel tree, ScreenerConditionGroupViewModel parent, string? name = null)
    {
        Assert.True(tree.TryAddGroup(parent, LogicalOperator.Or, name));
        return (ScreenerConditionGroupViewModel)parent.Children[^1];
    }

    /// <summary>The children of a group as text: a leaf as its right-hand number, a group as "[name](...)".</summary>
    private static string Shape(ScreenerConditionGroupViewModel group) => string.Join(",", group.Children.Select(c => c switch
    {
        ScreenerConditionLeafViewModel leaf => leaf.Entry.RightNumericValue.ToString(),
        ScreenerConditionGroupViewModel g => $"[{g.Name}]({Shape(g)})",
        _ => "?",
    }));

    // ---- reorder inside one group ----

    [Fact]
    public void AfterALaterSibling_MovesTheLeafBehindIt()
    {
        ScreenerConditionTreeViewModel tree = Create();
        ScreenerConditionGroupViewModel root = tree.Root;
        ScreenerConditionLeafViewModel a = AddLeaf(tree, root, 1), b = AddLeaf(tree, root, 2), c = AddLeaf(tree, root, 3);

        Assert.Equal(ConditionMoveResult.Moved, tree.MoveLeaf(a, c, ConditionDropPlacement.After));

        Assert.Equal("2,3,1", Shape(root));
    }

    [Fact]
    public void BeforeAnEarlierSibling_MovesTheLeafInFrontOfIt()
    {
        ScreenerConditionTreeViewModel tree = Create();
        ScreenerConditionGroupViewModel root = tree.Root;
        ScreenerConditionLeafViewModel a = AddLeaf(tree, root, 1), b = AddLeaf(tree, root, 2), c = AddLeaf(tree, root, 3);

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
        ScreenerConditionTreeViewModel tree = Create();
        ScreenerConditionGroupViewModel root = tree.Root;
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
        ScreenerConditionTreeViewModel tree = Create();
        ScreenerConditionGroupViewModel root = tree.Root;
        AddLeaf(tree, root, 1);
        ScreenerConditionLeafViewModel last = AddLeaf(tree, root, 2);
        int revision = tree.Revision;

        Assert.Equal(ConditionMoveResult.Unchanged, tree.MoveLeaf(last, root, ConditionDropPlacement.Inside));

        Assert.Equal("1,2", Shape(root));
        Assert.Equal(revision, tree.Revision);
    }

    [Fact]
    public void DroppingAnEarlierLeafOnItsOwnGroup_MovesItToTheEnd()
    {
        ScreenerConditionTreeViewModel tree = Create();
        ScreenerConditionGroupViewModel root = tree.Root;
        ScreenerConditionLeafViewModel first = AddLeaf(tree, root, 1);
        AddLeaf(tree, root, 2);
        AddLeaf(tree, root, 3);

        Assert.Equal(ConditionMoveResult.Moved, tree.MoveLeaf(first, root, ConditionDropPlacement.Inside));

        Assert.Equal("2,3,1", Shape(root));
    }

    // ---- between groups of the same root ----

    [Fact]
    public void IntoANestedGroup_AppendsThere_OpensIt_KeepsTheSelection_AndCountsOneEdit()
    {
        ScreenerConditionTreeViewModel tree = Create();
        ScreenerConditionGroupViewModel root = tree.Root;
        ScreenerConditionLeafViewModel a = AddLeaf(tree, root, 1);
        ScreenerConditionGroupViewModel group = AddGroup(tree, root, "MA");
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
        ScreenerConditionTreeViewModel tree = Create();
        ScreenerConditionGroupViewModel root = tree.Root;
        ScreenerConditionLeafViewModel a = AddLeaf(tree, root, 1);
        ScreenerConditionGroupViewModel group = AddGroup(tree, root, "MA");
        AddLeaf(tree, group, 2);
        ScreenerConditionLeafViewModel c = AddLeaf(tree, group, 3);

        Assert.Equal(ConditionMoveResult.Moved, tree.MoveLeaf(a, c, ConditionDropPlacement.Before));

        Assert.Equal("[MA](2,1,3)", Shape(root));
    }

    [Fact]
    public void OutOfANestedGroupIntoTheRoot_AppendsToTheRoot()
    {
        ScreenerConditionTreeViewModel tree = Create();
        ScreenerConditionGroupViewModel root = tree.Root;
        AddLeaf(tree, root, 1);
        ScreenerConditionGroupViewModel group = AddGroup(tree, root, "MA");
        ScreenerConditionLeafViewModel inner = AddLeaf(tree, group, 2);
        AddLeaf(tree, group, 3);

        Assert.Equal(ConditionMoveResult.Moved, tree.MoveLeaf(inner, root, ConditionDropPlacement.Inside));

        Assert.Equal("1,[MA](3),2", Shape(root));
    }

    [Fact]
    public void TheOnlyLeafOfAGroup_MovesOut_AndTheEmptiedGroupsAreRemovedUpwards_ButNeverTheRoot()
    {
        ScreenerConditionTreeViewModel tree = Create();
        ScreenerConditionGroupViewModel root = tree.Root;
        ScreenerConditionLeafViewModel keep = AddLeaf(tree, root, 1);
        ScreenerConditionGroupViewModel outer = AddGroup(tree, root, "outer");
        ScreenerConditionGroupViewModel inner = AddGroup(tree, outer, "inner");
        ScreenerConditionLeafViewModel only = AddLeaf(tree, inner, 2);

        Assert.Equal(ConditionMoveResult.Moved, tree.MoveLeaf(only, keep, ConditionDropPlacement.After));

        Assert.Equal("1,2", Shape(root));
        Assert.Null(outer.Parent);
        Assert.Null(inner.Parent);
        Assert.Same(root, only.Parent);
    }

    [Fact]
    public void TheOnlyLeafOfTheRoot_CanMoveIntoAGroupOfTheRoot_LeavingTheRootEmptyOfLeaves()
    {
        ScreenerConditionTreeViewModel tree = Create();
        ScreenerConditionGroupViewModel root = tree.Root;
        ScreenerConditionGroupViewModel group = AddGroup(tree, root, "MA");
        ScreenerConditionLeafViewModel leaf = AddLeaf(tree, root, 1);

        Assert.Equal(ConditionMoveResult.Moved, tree.MoveLeaf(leaf, group, ConditionDropPlacement.Inside));

        Assert.Equal("[MA](1)", Shape(root));
    }

    [Fact]
    public void TheBuiltTree_FollowsTheMovedOrder()
    {
        ScreenerConditionTreeViewModel tree = Create();
        ScreenerConditionGroupViewModel root = tree.Root;
        ScreenerConditionLeafViewModel a = AddLeaf(tree, root, 1);
        AddLeaf(tree, root, 2);
        AddLeaf(tree, root, 3);

        tree.MoveLeaf(a, root, ConditionDropPlacement.Inside);

        decimal[] values = tree.Build().Children.Cast<ScreenerConditionLeaf>().Select(l => l.Entry.RightNumericValue).ToArray();
        Assert.Equal(new[] { 2m, 3m, 1m }, values);
    }

    // ---- what is rejected ----

    [Fact]
    public void AGroupCannotBeMoved()
    {
        ScreenerConditionTreeViewModel tree = Create();
        ScreenerConditionGroupViewModel root = tree.Root;
        ScreenerConditionGroupViewModel group = AddGroup(tree, root, "MA");
        AddLeaf(tree, group, 1);
        ScreenerConditionLeafViewModel other = AddLeaf(tree, root, 2);

        Assert.Equal(ConditionMoveResult.Rejected, tree.MoveLeaf(group, other, ConditionDropPlacement.After));
        Assert.Equal(ConditionMoveResult.Rejected, tree.MoveLeaf(root, other, ConditionDropPlacement.After));
        Assert.Equal("[MA](1),2", Shape(root));
    }

    [Fact]
    public void ARootIsNotASiblingTarget_AndALeafIsNotAnInsideTarget()
    {
        ScreenerConditionTreeViewModel tree = Create();
        ScreenerConditionGroupViewModel root = tree.Root;
        ScreenerConditionLeafViewModel a = AddLeaf(tree, root, 1);
        ScreenerConditionLeafViewModel b = AddLeaf(tree, root, 2);

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
        ScreenerConditionTreeViewModel tree = Create();
        ScreenerConditionTreeViewModel other = Create();
        ScreenerConditionGroupViewModel root = tree.Root;
        ScreenerConditionLeafViewModel a = AddLeaf(tree, root, 1);
        ScreenerConditionLeafViewModel b = AddLeaf(tree, root, 2);
        ScreenerConditionLeafViewModel foreign = AddLeaf(other, other.Root, 7);
        tree.Delete(b);

        Assert.Equal(ConditionMoveResult.Rejected, tree.MoveLeaf(b, a, ConditionDropPlacement.After));
        Assert.Equal(ConditionMoveResult.Rejected, tree.MoveLeaf(a, b, ConditionDropPlacement.After));
        Assert.Equal(ConditionMoveResult.Rejected, tree.MoveLeaf(foreign, a, ConditionDropPlacement.After));
        Assert.Equal(ConditionMoveResult.Rejected, tree.MoveLeaf(a, foreign, ConditionDropPlacement.After));
        Assert.Equal("1", Shape(root));
        Assert.Equal("7", Shape(other.Root));
    }

    [Fact]
    public void EvaluateMove_ChangesNothing()
    {
        ScreenerConditionTreeViewModel tree = Create();
        ScreenerConditionGroupViewModel root = tree.Root;
        ScreenerConditionLeafViewModel a = AddLeaf(tree, root, 1);
        AddLeaf(tree, root, 2);
        int revision = tree.Revision;

        Assert.Equal(ConditionMoveResult.Moved, tree.EvaluateMove(a, root, ConditionDropPlacement.Inside));

        Assert.Equal("1,2", Shape(root));
        Assert.Equal(revision, tree.Revision);
    }

    [Fact]
    public void DragStartDistance_DefaultsToConfiguredValue_AndRejectsBelowOne()
    {
        ScreenerConditionTreeViewModel tree = new(6, 40, 32);
        Assert.Equal(new StockAnalyzer.Avalonia.Common.ScreenerSettings().ConditionDragStartDistance, tree.DragStartDistance);

        ScreenerConditionTreeViewModel custom = new(6, 40, 32, dragStartDistance: 10);
        Assert.Equal(10, custom.DragStartDistance);

        Assert.Throws<System.ArgumentOutOfRangeException>(() => new ScreenerConditionTreeViewModel(6, 40, 32, dragStartDistance: 0));
    }
}
