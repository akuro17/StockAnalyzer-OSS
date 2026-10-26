using System;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;
using static StockAnalyzer.Core.Tests.Backtest.ConditionTreeTestKit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>Condition-tree size bounds and validation (MP-1 Phase 2). See docs/sa_analysis_BacktestConditionTree B9.</summary>
public class BacktestConditionTreeValidatorTests
{
    private static readonly int DefaultDepth = new BacktestSettings().MaxConditionTreeDepth;
    private static readonly int DefaultNodes = new BacktestSettings().MaxConditionTreeNodes;
    private static readonly int DefaultOffset = new BacktestSettings().MaxConditionOffset;

    private static BacktestConditionSide Side(int offset = 0) => new() { IndicatorType = IndicatorType.SMA, Offset = offset };

    private static BacktestConditionEntry Comparison(BacktestConditionSide? side = null) => new() { Left = side ?? Side(), RightNumericValue = 1m };

    private static BacktestConditionLeaf Leaf(BacktestConditionSide? side = null) => new(Comparison(side));

    private static BacktestConditionTree TreeWithEntryLong(BacktestConditionGroup entryLong) => new(
        entryLong, BacktestConditionGroup.EmptyAnd, BacktestConditionGroup.EmptyAnd,
        BacktestConditionGroup.EmptyAnd, BacktestConditionGroup.EmptyAnd, BacktestConditionGroup.EmptyAnd);

    /// <summary>A root group whose deepest group sits at exactly <paramref name="depth"/> (root = 1) and holds one leaf.</summary>
    private static BacktestConditionGroup ChainOfDepth(int depth)
    {
        BacktestConditionGroup current = Group(LogicalOperator.And, Leaf());
        for (int level = depth - 1; level >= 1; level--) current = Group(level % 2 == 0 ? LogicalOperator.And : LogicalOperator.Or, current);
        return current;
    }

    private static BacktestConditionTree Snapshot(BacktestConditionTree tree, int? maxOffset = null, int? maxDepth = null, int? maxNodes = null)
        => BacktestConditionValidator.SnapshotTree(
            tree,
            maxOffset ?? DefaultOffset,
            maxDepth ?? (maxNodes.HasValue ? Math.Min(DefaultDepth, maxNodes.Value) : DefaultDepth),
            maxNodes ?? DefaultNodes);

    [Fact]
    public void PocoDefaults_RespectTheLowerBound()
    {
        // The default values themselves live only in BacktestSettings; the appsettings agreement is pinned in SettingsExternalizationTests.
        Assert.True(DefaultDepth >= BacktestConditionTreeRule.MinBound);
        Assert.True(DefaultNodes >= BacktestConditionTreeRule.MinBound);
    }

    [Fact]
    public void Depth_AtTheBoundPasses_AndOneAboveThrows()
    {
        Snapshot(TreeWithEntryLong(ChainOfDepth(DefaultDepth)));

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Snapshot(TreeWithEntryLong(ChainOfDepth(DefaultDepth + 1))));
        Assert.Contains("Backtest:MaxConditionTreeDepth", ex.Message);
        Assert.Contains("EntryLong", ex.Message);
    }

    [Fact]
    public void Nodes_AtTheBoundPasses_AndOneAboveThrows()
    {
        // Root + (DefaultNodes - 1) leaves = exactly the bound.
        BacktestConditionGroup atBound = new(LogicalOperator.Or, Enumerable.Range(0, DefaultNodes - 1).Select(_ => (IBacktestConditionNode)Leaf()).ToImmutableArray());
        BacktestConditionGroup above = new(LogicalOperator.Or, Enumerable.Range(0, DefaultNodes).Select(_ => (IBacktestConditionNode)Leaf()).ToImmutableArray());

        Snapshot(TreeWithEntryLong(atBound));

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Snapshot(TreeWithEntryLong(above)));
        Assert.Contains("Backtest:MaxConditionTreeNodes", ex.Message);
    }

    [Fact]
    public void NodeCount_IsPerRoot_NotSharedAcrossRoots()
    {
        BacktestConditionGroup Full() => new(LogicalOperator.And, Enumerable.Range(0, DefaultNodes - 1).Select(_ => (IBacktestConditionNode)Leaf()).ToImmutableArray());
        var tree = new BacktestConditionTree(Full(), Full(), Full(), Full(), Full(), Full());

        BacktestConditionTree snapshot = Snapshot(tree);

        Assert.Equal(DefaultNodes - 1, snapshot.ReverseShort.Children.Length);
    }

    [Fact]
    public void Bounds_AreInputs_TheSameTreeChangesOutcomeWithTheLimit()
    {
        BacktestConditionTree tree = TreeWithEntryLong(ChainOfDepth(3));

        Snapshot(tree, maxDepth: 3);
        Assert.Throws<ArgumentOutOfRangeException>(() => Snapshot(tree, maxDepth: 2));
        Snapshot(tree, maxNodes: 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => Snapshot(tree, maxNodes: 3));
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(-1, 5)]
    [InlineData(5, 0)]
    [InlineData(5, -1)]
    public void Limits_BelowTheMinimumBound_AreRejected(int depth, int nodes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Snapshot(BacktestConditionTree.Empty, maxDepth: depth, maxNodes: nodes));
    }

    [Theory]
    [InlineData(BacktestConditionTreeRule.MaxDepthBound + 1, 10, 500)]
    [InlineData(10, BacktestConditionTreeRule.MaxNodesBound + 1, 500)]
    [InlineData(10, 50, BacktestConditionTreeRule.MaxOffsetBound + 1)]
    public void Limits_AboveTheMaximumBound_AreRejected(int depth, int nodes, int offset)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Snapshot(BacktestConditionTree.Empty, maxOffset: offset, maxDepth: depth, maxNodes: nodes));
    }

    [Fact]
    public void BacktestSettings_Validate_EnforcesHardBounds()
    {
        var settingsValid = new BacktestSettings();
        settingsValid.Validate(); // Should pass with defaults

        var depthExceeded = new BacktestSettings { MaxConditionTreeDepth = BacktestConditionTreeRule.MaxDepthBound + 1 };
        Assert.Throws<InvalidOperationException>(() => depthExceeded.Validate());

        var nodesExceeded = new BacktestSettings { MaxConditionTreeNodes = BacktestConditionTreeRule.MaxNodesBound + 1 };
        Assert.Throws<InvalidOperationException>(() => nodesExceeded.Validate());

        var offsetExceeded = new BacktestSettings { MaxConditionOffset = BacktestConditionTreeRule.MaxOffsetBound + 1 };
        Assert.Throws<InvalidOperationException>(() => offsetExceeded.Validate());
    }

    [Fact]
    public void BacktestSettings_Validate_CrossValidation_ThrowsWhenNodesLessThanDepth()
    {
        var invalid = new BacktestSettings
        {
            MaxConditionTreeDepth = 10,
            MaxConditionTreeNodes = 8
        };
        var ex = Assert.Throws<InvalidOperationException>(() => invalid.Validate());
        Assert.Contains("must be >= MaxConditionTreeDepth", ex.Message);
    }

    [Fact]
    public void SnapshotTree_DepthExceedingNodes_ThrowsArgumentOutOfRangeException()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            BacktestConditionValidator.SnapshotTree(BacktestConditionTree.Empty, maxOffset: null, maxDepth: 10, maxNodes: 8));
        Assert.Equal("maxDepth", ex.ParamName);
        Assert.Contains("must not exceed maxNodes", ex.Message);
    }

    [Fact]
    public void EmptyRoots_AreValid_ButAnEmptyNestedGroupIsRejected()
    {
        BacktestConditionTree empty = Snapshot(BacktestConditionTree.Empty);
        Assert.False(empty.EntryLong.HasLeaf);

        BacktestConditionTree nestedEmpty = TreeWithEntryLong(Group(LogicalOperator.And, Leaf(), Group(LogicalOperator.Or)));
        var ex = Assert.Throws<ArgumentException>(() => Snapshot(nestedEmpty));
        Assert.Contains("nested condition group must contain at least one condition", ex.Message);
    }

    [Theory]
    [InlineData("Role")]
    [InlineData("Position")]
    [InlineData("LogicalOperator")]
    public void LeafCarryingRoleOrPositionOrConnector_IsRejected(string field)
    {
        var entry = field switch
        {
            "Role" => new BacktestConditionEntry { Left = Side(), Role = BacktestConditionRole.Reversal },
            "Position" => new BacktestConditionEntry { Left = Side(), Position = TradeSide.Short },
            _ => new BacktestConditionEntry { Left = Side(), LogicalOperator = LogicalOperator.Or },
        };
        BacktestConditionTree tree = TreeWithEntryLong(Group(LogicalOperator.And, new BacktestConditionLeaf(entry)));

        var ex = Assert.Throws<ArgumentException>(() => Snapshot(tree));
        Assert.Contains("must not carry Role/Position/LogicalOperator", ex.Message);
    }

    [Fact]
    public void LeafChecks_ReuseTheListValidation_OffsetEnumAndIndicatorMode()
    {
        BacktestConditionTree Tree(BacktestConditionEntry entry) => TreeWithEntryLong(Group(LogicalOperator.And, new BacktestConditionLeaf(entry)));

        Assert.Throws<ArgumentOutOfRangeException>(() => Snapshot(Tree(Comparison(Side(offset: -1)))));
        Assert.Throws<ArgumentOutOfRangeException>(() => Snapshot(Tree(Comparison(Side(offset: DefaultOffset + 1)))));
        Snapshot(Tree(Comparison(Side(offset: DefaultOffset))));
        Assert.Throws<ArgumentException>(() => Snapshot(Tree(new BacktestConditionEntry { Left = Side(), TargetMode = RightHandTargetMode.Indicator, Right = null })));
        Assert.Throws<ArgumentException>(() => Snapshot(Tree(new BacktestConditionEntry { Left = Side(), Operator = ComparisonOperator.Contains })));
        Assert.Throws<ArgumentException>(() => Snapshot(TreeWithEntryLong(new BacktestConditionGroup((LogicalOperator)0, ImmutableArray.Create<IBacktestConditionNode>(new BacktestConditionLeaf(new BacktestConditionEntry { Left = null! }))))));
    }

    [Fact]
    public void ErrorMessage_NamesTheLeafPreOrderOrdinalAcrossTheWholeTree()
    {
        BacktestConditionEntry bad = Comparison(Side(offset: -1));
        var tree = new BacktestConditionTree(
            Group(LogicalOperator.And, Leaf()),
            Group(LogicalOperator.And, Leaf(), Group(LogicalOperator.Or, new BacktestConditionLeaf(bad))),
            BacktestConditionGroup.EmptyAnd, BacktestConditionGroup.EmptyAnd, BacktestConditionGroup.EmptyAnd, BacktestConditionGroup.EmptyAnd);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Snapshot(tree));

        Assert.Contains("ConditionEntries[2]", ex.Message);
    }

    [Fact]
    public void Snapshot_IsAnIndependentDeepCopyWithTheSameStructure()
    {
        BacktestConditionSide left = Side();
        BacktestConditionTree source = TreeWithEntryLong(Group(LogicalOperator.Or, Leaf(left), Group(LogicalOperator.And, Leaf())));

        BacktestConditionTree copy = Snapshot(source);

        Assert.NotSame(source.EntryLong, copy.EntryLong);
        Assert.Equal(LogicalOperator.Or, copy.EntryLong.Operator);
        var copiedLeaf = Assert.IsType<BacktestConditionLeaf>(copy.EntryLong.Children[0]);
        var sourceLeaf = Assert.IsType<BacktestConditionLeaf>(source.EntryLong.Children[0]);
        Assert.NotSame(sourceLeaf.Comparison, copiedLeaf.Comparison);
        Assert.NotSame(left, copiedLeaf.Comparison.Left);
        Assert.Equal(left.IndicatorType, copiedLeaf.Comparison.Left.IndicatorType);
        var nested = Assert.IsType<BacktestConditionGroup>(copy.EntryLong.Children[1]);
        Assert.Equal(LogicalOperator.And, nested.Operator);
        Assert.Single(nested.Children);
        Assert.Throws<ArgumentNullException>(() => BacktestConditionValidator.SnapshotTree(null!, null, 8, 64));
    }

    [Fact]
    public void ActiveSides_AreInCanonicalRootOrderAndPreOrder_IncludingRightOfIndicatorMode()
    {
        BacktestConditionSide entryLongL = Side(), entryLongR = Side(), nested = Side(), entryShort = Side(), exitShort = Side(), reverseLong = Side();
        var indicatorMode = new BacktestConditionEntry { Left = entryLongL, TargetMode = RightHandTargetMode.Indicator, Right = entryLongR };
        var tree = new BacktestConditionTree(
            Group(LogicalOperator.And, new BacktestConditionLeaf(indicatorMode), Group(LogicalOperator.Or, Leaf(nested))),
            Group(LogicalOperator.And, Leaf(entryShort)),
            BacktestConditionGroup.EmptyAnd,
            Group(LogicalOperator.And, Leaf(exitShort)),
            Group(LogicalOperator.And, Leaf(reverseLong)),
            BacktestConditionGroup.EmptyAnd);

        var sides = BacktestConditionValidator.ActiveSides(tree);

        Assert.Equal(new[] { entryLongL, entryLongR, nested, entryShort, exitShort, reverseLong }, sides);
        Assert.Empty(BacktestConditionValidator.ActiveSides(BacktestConditionTree.Empty));
    }

    [Fact]
    public void ExistingListSnapshot_IsUnchanged()
    {
        var entries = new[] { new BacktestConditionEntry { Left = Side(), Role = BacktestConditionRole.Reversal, Position = TradeSide.Short, LogicalOperator = LogicalOperator.Or } };

        ImmutableArray<BacktestConditionEntry> snapshot = BacktestConditionValidator.Snapshot(entries);

        Assert.Equal(BacktestConditionRole.Reversal, snapshot[0].Role);
        Assert.Equal(TradeSide.Short, snapshot[0].Position);
        Assert.Equal(LogicalOperator.Or, snapshot[0].LogicalOperator);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void Settings_BelowTheMinimumBound_AreRejectedByValidate(int depth, int nodes)
    {
        var settings = new BacktestSettings { MaxConditionTreeDepth = depth, MaxConditionTreeNodes = nodes };

        Assert.Throws<InvalidOperationException>(settings.Validate);
        new BacktestSettings { MaxConditionTreeDepth = 1, MaxConditionTreeNodes = 1 }.Validate();
    }

    [Fact]
    public void MultiBranchTree_TotalNodesAcrossSiblingBranches_AtTheBoundPasses_AndOneAboveThrows()
    {
        // Tree structure:
        // Root (1)
        // - ChildGroup 1 (1 node) + 10 leaves = 11 nodes
        // - ChildGroup 2 (1 node) + 10 leaves = 11 nodes
        // - ChildGroup 3 (1 node) + (DefaultNodes - 1 - 11 - 11 - 1) leaves
        // Total = 1 + 11 + 11 + 1 + (DefaultNodes - 24) = DefaultNodes
        int remainingLeaves = DefaultNodes - 1 - 11 - 11 - 1;
        Assert.True(remainingLeaves > 0);

        var group1 = new BacktestConditionGroup(LogicalOperator.And, Enumerable.Range(0, 10).Select(_ => (IBacktestConditionNode)Leaf()).ToImmutableArray());
        var group2 = new BacktestConditionGroup(LogicalOperator.Or, Enumerable.Range(0, 10).Select(_ => (IBacktestConditionNode)Leaf()).ToImmutableArray());
        var group3AtBound = new BacktestConditionGroup(LogicalOperator.And, Enumerable.Range(0, remainingLeaves).Select(_ => (IBacktestConditionNode)Leaf()).ToImmutableArray());
        var group3AboveBound = new BacktestConditionGroup(LogicalOperator.And, Enumerable.Range(0, remainingLeaves + 1).Select(_ => (IBacktestConditionNode)Leaf()).ToImmutableArray());

        var rootAtBound = new BacktestConditionGroup(LogicalOperator.And, ImmutableArray.Create<IBacktestConditionNode>(group1, group2, group3AtBound));
        var rootAboveBound = new BacktestConditionGroup(LogicalOperator.And, ImmutableArray.Create<IBacktestConditionNode>(group1, group2, group3AboveBound));

        Snapshot(TreeWithEntryLong(rootAtBound));

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Snapshot(TreeWithEntryLong(rootAboveBound)));
        Assert.Contains("Backtest:MaxConditionTreeNodes", ex.Message);
    }

    [Fact]
    public void SnapshotTree_ValidatesLaterRoots_ThrowsNamingTheOffendingRoot()
    {
        // EntryLong, ExitLong, EntryShort, ExitShort, ReverseLong are empty
        // ReverseShort exceeds depth
        var tree = new BacktestConditionTree(
            BacktestConditionGroup.EmptyAnd,
            BacktestConditionGroup.EmptyAnd,
            BacktestConditionGroup.EmptyAnd,
            BacktestConditionGroup.EmptyAnd,
            BacktestConditionGroup.EmptyAnd,
            ChainOfDepth(DefaultDepth + 1));

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Snapshot(tree));
        Assert.Contains("ReverseShort", ex.Message);
    }
}
