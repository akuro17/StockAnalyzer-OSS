using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;
using static StockAnalyzer.Core.Tests.Backtest.ConditionTreeTestKit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>Core helpers the condition-tree editor relies on (MP-2 Phase 1): Measure and HasAnyLeaf.</summary>
public class BacktestConditionTreeEditorSupportTests
{
    private static BacktestConditionEntry Entry(BacktestConditionRole role, TradeSide position)
        => CloseVsSma(ComparisonOperator.GreaterThan, 5, role, position);

    // ---- Measure ----

    [Fact]
    public void Measure_EmptyRoot_IsDepthOneAndOneNode()
    {
        Assert.Equal(new BacktestConditionTreeSize(1, 1), BacktestConditionTreeRule.Measure(BacktestConditionGroup.EmptyAnd));
    }

    [Fact]
    public void Measure_FlatGroup_LeavesAddNodesButNotDepth()
    {
        var root = And(Leaf(Side()), Leaf(Side()), Leaf(Side()));

        Assert.Equal(new BacktestConditionTreeSize(1, 4), BacktestConditionTreeRule.Measure(root));
    }

    [Fact]
    public void Measure_NestedTree_UsesTheDeepestBranchAndCountsEveryNode()
    {
        // (A AND B) OR (C AND (D OR E)): groups root, g1, g2, g3 = 4; leaves 5; depth root=1 -> g2=2 -> g3=3.
        var root = Or(And(Leaf(Side()), Leaf(Side())), And(Leaf(Side()), Or(Leaf(Side()), Leaf(Side()))));

        Assert.Equal(new BacktestConditionTreeSize(3, 9), BacktestConditionTreeRule.Measure(root));
    }

    [Fact]
    public void Measure_AgreesWithTheValidatorLimits_AtExactlyTheBoundAndOneBelow()
    {
        var root = Or(And(Leaf(Side()), Leaf(Side())), And(Leaf(Side()), Or(Leaf(Side()), Leaf(Side()))));
        BacktestConditionTreeSize size = BacktestConditionTreeRule.Measure(root);
        BacktestConditionTree tree = Tree(entryLong: root);

        BacktestConditionValidator.SnapshotTree(tree, maxOffset: null, maxDepth: size.Depth, maxNodes: size.NodeCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => BacktestConditionValidator.SnapshotTree(tree, null, size.Depth - 1, size.NodeCount));
        Assert.Throws<ArgumentOutOfRangeException>(() => BacktestConditionValidator.SnapshotTree(tree, null, size.Depth, size.NodeCount - 1));
    }

    [Fact]
    public void Measure_SharedNodeInstance_CountsEveryOccurrence()
    {
        BacktestConditionLeaf shared = Leaf(Side());
        var root = And(shared, shared);

        Assert.Equal(3, BacktestConditionTreeRule.Measure(root).NodeCount);
    }

    [Fact]
    public void Measure_AbsurdlyDeepTree_BecomesArgumentExceptionNotACrash()
    {
        BacktestConditionGroup deepest = And(Leaf(Side()));
        for (int i = 0; i < 200_000; i++) deepest = And(deepest);

        ArgumentException ex = Assert.Throws<ArgumentException>(() => BacktestConditionTreeRule.Measure(deepest));

        Assert.Contains(BacktestConditionTreeRule.NestingTooDeepMessage, ex.Message);
    }

    [Fact]
    public void Measure_NullRoot_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => BacktestConditionTreeRule.Measure(null!));
    }

    // ---- HasAnyLeaf ----

    [Fact]
    public void HasAnyLeaf_EmptyTree_IsFalse()
    {
        Assert.False(BacktestConditionTree.Empty.HasAnyLeaf);
    }

    [Fact]
    public void HasAnyLeaf_RootsWithoutChildren_AreFalse()
    {
        Assert.False(Tree(entryLong: And(), exitShort: Or()).HasAnyLeaf);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void HasAnyLeaf_AnySingleRootWithALeaf_IsTrue(int rootIndex)
    {
        BacktestConditionTree tree = BacktestConditionTree.Create((section, side) =>
            BacktestConditionTree.CanonicalRoots.IndexOf((section, side)) == rootIndex
                ? And(Or(Leaf(Side())))
                : BacktestConditionGroup.EmptyAnd);

        Assert.True(tree.HasAnyLeaf);
    }
}
