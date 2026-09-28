using System;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>Condition-tree editor view-model layer (MP-2 Phase 1). Plain view-model tests: no Avalonia control, no UI thread.</summary>
public class BacktestConditionTreeViewModelTests
{
    private const int TestDepth = 4;
    private const int TestNodes = 6;

    private static BacktestConditionTreeViewModel Create(int maxDepth = TestDepth, int maxNodes = TestNodes) => new(maxDepth, maxNodes);

    private static BacktestConditionEntry Entry(decimal value = 0m) => new()
    {
        Left = new BacktestConditionSide { IndicatorType = IndicatorType.Price, PriceSource = PriceType.Close },
        Operator = ComparisonOperator.GreaterThan,
        TargetMode = RightHandTargetMode.NumericValue,
        RightNumericValue = value,
    };

    private static int LeafCount(BacktestConditionGroupViewModel group)
        => group.Children.Sum(c => c switch { BacktestConditionGroupViewModel g => LeafCount(g), _ => 1 });

    // ---- structure ----

    [Fact]
    public void NewTree_HasSixFixedRootsInCanonicalOrder_AllEmptyAnd()
    {
        BacktestConditionTreeViewModel vm = Create();

        Assert.Equal(BacktestConditionTree.CanonicalRoots.Select(r => (r.Section, r.Side)), vm.Roots.Select(r => (r.Section, r.Side)));
        Assert.All(vm.Roots, r =>
        {
            Assert.True(r.IsRoot);
            Assert.Empty(r.Children);
            Assert.Equal(LogicalOperator.And, r.Operator);
            Assert.Equal(1, r.Depth);
        });
        Assert.False(vm.Build().HasAnyLeaf);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void Constructor_RejectsBoundsBelowTheCoreMinimum(int maxDepth, int maxNodes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(maxDepth, maxNodes));
    }

    // ---- add target (D8) ----

    [Theory]
    [InlineData(BacktestConditionSection.Entry, TradeSide.Long)]
    [InlineData(BacktestConditionSection.Exit, TradeSide.Short)]
    [InlineData(BacktestConditionSection.Reverse, TradeSide.Long)]
    public void AddLeaf_NothingSelected_GoesToTheSectionSideRoot(BacktestConditionSection section, TradeSide side)
    {
        BacktestConditionTreeViewModel vm = Create();

        BacktestConditionGroupViewModel? target = vm.TryAddLeaf(section, side, Entry());

        Assert.Same(vm.Root(section, side), target);
        Assert.Equal(1, vm.Root(section, side).Children.Count);
        Assert.Equal(1, vm.Roots.Sum(LeafCount));
    }

    [Fact]
    public void AddLeaf_MatchingGroupSelected_GoesIntoThatGroup()
    {
        BacktestConditionTreeViewModel vm = Create();
        BacktestConditionGroupViewModel root = vm.Root(BacktestConditionSection.Entry, TradeSide.Long);
        Assert.True(vm.TryAddGroup(root, LogicalOperator.Or));
        var group = (BacktestConditionGroupViewModel)root.Children[0];
        vm.SelectedNode = group;

        BacktestConditionGroupViewModel? target = vm.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry());

        Assert.Same(group, target);
        Assert.Single(group.Children);
    }

    [Fact]
    public void AddLeaf_GroupOfAnotherSideOrSectionSelected_GoesToTheRequestedRoot()
    {
        BacktestConditionTreeViewModel vm = Create();
        vm.SelectedNode = vm.Root(BacktestConditionSection.Entry, TradeSide.Short);

        Assert.Same(vm.Root(BacktestConditionSection.Entry, TradeSide.Long), vm.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry()));
        Assert.Same(vm.Root(BacktestConditionSection.Exit, TradeSide.Short), vm.TryAddLeaf(BacktestConditionSection.Exit, TradeSide.Short, Entry()));
    }

    [Fact]
    public void AddLeaf_LeafSelected_GoesToTheRequestedRoot()
    {
        BacktestConditionTreeViewModel vm = Create();
        vm.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry());
        vm.SelectedNode = vm.Root(BacktestConditionSection.Entry, TradeSide.Long).Children[0];

        Assert.Same(vm.Root(BacktestConditionSection.Entry, TradeSide.Long), vm.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry(1m)));
    }

    // ---- bounds ----

    [Fact]
    public void NodeBound_LeafAddSucceedsAtExactlyTheLimitAndFailsOneBeyond()
    {
        BacktestConditionTreeViewModel vm = Create(maxNodes: TestNodes);
        BacktestConditionGroupViewModel root = vm.Root(BacktestConditionSection.Entry, TradeSide.Long);

        // The root itself is a node: TestNodes - 1 leaves fill the root exactly.
        for (int i = 0; i < TestNodes - 1; i++) Assert.NotNull(vm.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry(i)));
        Assert.Equal(TestNodes, BacktestConditionTreeRule.Measure(vm.Build().EntryLong).NodeCount);

        Assert.False(vm.CanAddLeaf(root));
        Assert.Null(vm.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry(99m)));
        Assert.Equal(TestNodes - 1, root.Children.Count);
    }

    [Fact]
    public void NodeBound_IsPerRoot()
    {
        BacktestConditionTreeViewModel vm = Create(maxNodes: 2);
        Assert.NotNull(vm.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry()));
        Assert.Null(vm.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry()));

        Assert.NotNull(vm.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Short, Entry()));
    }

    [Fact]
    public void DepthBound_GroupAddSucceedsAtExactlyTheLimitAndFailsOneBeyond()
    {
        BacktestConditionTreeViewModel vm = Create(maxDepth: TestDepth, maxNodes: 64);
        BacktestConditionGroupViewModel current = vm.Root(BacktestConditionSection.Exit, TradeSide.Long);

        for (int depth = 1; depth < TestDepth; depth++)
        {
            Assert.True(vm.CanAddGroup(current));
            Assert.True(vm.TryAddGroup(current, LogicalOperator.And));
            current = (BacktestConditionGroupViewModel)current.Children[0];
            Assert.Equal(depth + 1, current.Depth);
        }

        Assert.Equal(TestDepth, BacktestConditionTreeRule.Measure(vm.Build().ExitLong).Depth);
        Assert.False(vm.CanAddGroup(current));
        Assert.False(vm.TryAddGroup(current, LogicalOperator.Or));
        Assert.True(vm.CanAddLeaf(current));
    }

    [Fact]
    public void GroupCommands_AreDisabledWhenABoundIsReached_AndReenabledAfterADelete()
    {
        BacktestConditionTreeViewModel vm = Create(maxNodes: 2);
        BacktestConditionGroupViewModel root = vm.Root(BacktestConditionSection.Entry, TradeSide.Long);
        Assert.True(vm.AddGroupCommand.CanExecute(root));

        vm.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry());
        Assert.False(vm.AddGroupCommand.CanExecute(root));

        vm.DeleteNodeCommand.Execute(root.Children[0]);
        Assert.True(vm.AddGroupCommand.CanExecute(root));
    }

    // ---- operator / delete ----

    [Fact]
    public void UpdateGroup_ChangesOperatorOfAnyGroupIncludingRoots()
    {
        BacktestConditionTreeViewModel vm = Create();
        BacktestConditionGroupViewModel root = vm.Root(BacktestConditionSection.Entry, TradeSide.Long);

        vm.UpdateGroup(root, null, LogicalOperator.Or);
        Assert.Equal(LogicalOperator.Or, root.Operator);
        vm.UpdateGroup(root, null, LogicalOperator.And);
        Assert.Equal(LogicalOperator.And, root.Operator);
        vm.UpdateGroup(root, null, LogicalOperator.Or);
        Assert.Equal(LogicalOperator.Or, vm.Build().EntryLong.Operator);
    }

    [Fact]
    public void Delete_Root_OnlyClearsItsChildren()
    {
        BacktestConditionTreeViewModel vm = Create();
        BacktestConditionGroupViewModel root = vm.Root(BacktestConditionSection.Reverse, TradeSide.Short);
        vm.TryAddLeaf(BacktestConditionSection.Reverse, TradeSide.Short, Entry());
        vm.TryAddGroup(root, LogicalOperator.Or);

        Assert.True(vm.DeleteNodeCommand.CanExecute(root));
        vm.DeleteNodeCommand.Execute(root);

        Assert.Empty(root.Children);
        Assert.Same(root, vm.Root(BacktestConditionSection.Reverse, TradeSide.Short));
        Assert.Equal(6, vm.Roots.Count);
        Assert.False(vm.DeleteNodeCommand.CanExecute(root));
    }

    [Fact]
    public void Delete_Subtree_RemovesAllOfIt()
    {
        BacktestConditionTreeViewModel vm = Create();
        BacktestConditionGroupViewModel root = vm.Root(BacktestConditionSection.Entry, TradeSide.Long);
        vm.TryAddGroup(root, LogicalOperator.Or);
        var group = (BacktestConditionGroupViewModel)root.Children[0];
        vm.SelectedNode = group;
        vm.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry(1m));
        vm.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry(2m));
        vm.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Short, Entry(3m));

        vm.Delete(group);

        Assert.Empty(root.Children);
        Assert.Null(group.Parent);
        Assert.Null(vm.SelectedNode);
        Assert.Equal(1, vm.Roots.Sum(LeafCount));
    }

    [Fact]
    public void Delete_LastLeafOfANestedGroup_RemovesTheEmptiedGroupsUpwardsButNeverTheRoot()
    {
        BacktestConditionTreeViewModel vm = Create();
        BacktestConditionGroupViewModel root = vm.Root(BacktestConditionSection.Entry, TradeSide.Long);
        vm.TryAddGroup(root, LogicalOperator.And);
        var outer = (BacktestConditionGroupViewModel)root.Children[0];
        vm.TryAddGroup(outer, LogicalOperator.Or);
        var inner = (BacktestConditionGroupViewModel)outer.Children[0];
        vm.SelectedNode = inner;
        vm.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry());
        vm.SelectedNode = null;

        vm.Delete(inner.Children[0]);

        Assert.Empty(root.Children);
        Assert.Null(outer.Parent);
        Assert.Null(inner.Parent);
        Assert.Equal(6, vm.Roots.Count);
    }

    [Fact]
    public void Delete_OneOfSeveralLeaves_KeepsTheGroup()
    {
        BacktestConditionTreeViewModel vm = Create();
        BacktestConditionGroupViewModel root = vm.Root(BacktestConditionSection.Entry, TradeSide.Long);
        vm.TryAddGroup(root, LogicalOperator.And);
        var group = (BacktestConditionGroupViewModel)root.Children[0];
        vm.SelectedNode = group;
        vm.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry(1m));
        vm.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry(2m));

        vm.Delete(group.Children[0]);

        Assert.Same(group, root.Children.Single());
        Assert.Single(group.Children);
        Assert.Same(group, vm.SelectedNode);
    }

    [Fact]
    public void Selection_MirrorsIsSelected_OnSingleNode()
    {
        BacktestConditionTreeViewModel vm = Create();
        BacktestConditionGroupViewModel a = vm.Root(BacktestConditionSection.Entry, TradeSide.Long);
        BacktestConditionGroupViewModel b = vm.Root(BacktestConditionSection.Exit, TradeSide.Long);

        vm.SelectedNode = a;
        vm.SelectedNode = b;

        Assert.False(a.IsSelected);
        Assert.True(b.IsSelected);
    }

    // ---- Build / Load ----

    [Fact]
    public void Build_ProducesTheDomainTreeThatWasEdited_AndValidatesThroughCore()
    {
        BacktestConditionTreeViewModel vm = Create(maxDepth: 8, maxNodes: 64);
        BacktestConditionGroupViewModel root = vm.Root(BacktestConditionSection.Entry, TradeSide.Long);
        vm.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry(1m));
        vm.TryAddGroup(root, LogicalOperator.Or);
        vm.SelectedNode = root.Children[1];
        vm.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry(2m));
        vm.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry(3m));

        BacktestConditionTree tree = vm.Build();
        BacktestConditionTree snapshot = BacktestConditionValidator.SnapshotTree(tree, maxOffset: null, maxDepth: 8, maxNodes: 64);

        Assert.True(tree.HasAnyLeaf);
        Assert.Equal(2, tree.EntryLong.Children.Length);
        var nested = Assert.IsType<BacktestConditionGroup>(tree.EntryLong.Children[1]);
        Assert.Equal(LogicalOperator.Or, nested.Operator);
        Assert.Equal(2, nested.Children.Length);
        Assert.Equal(new BacktestConditionTreeSize(2, 5), BacktestConditionTreeRule.Measure(snapshot.EntryLong));
    }

    [Fact]
    public void Build_EmptyNestedGroup_IsNotHidden_CoreValidationRejectsIt()
    {
        BacktestConditionTreeViewModel vm = Create();
        vm.TryAddGroup(vm.Root(BacktestConditionSection.Entry, TradeSide.Long), LogicalOperator.And);

        BacktestConditionTree tree = vm.Build();

        Assert.Single(tree.EntryLong.Children);
        Assert.Throws<ArgumentException>(() => BacktestConditionValidator.SnapshotTree(tree, null, TestDepth, TestNodes));
    }

    [Fact]
    public void Load_ThenBuild_RoundTripsTheSameStructure()
    {
        BacktestConditionEntry a = Entry(1m), b = Entry(2m), c = Entry(3m);
        var source = BacktestConditionTree.Create((section, side) => (section, side) switch
        {
            (BacktestConditionSection.Entry, TradeSide.Long) => new BacktestConditionGroup(LogicalOperator.Or, new IBacktestConditionNode[]
            {
                new BacktestConditionLeaf(a),
                new BacktestConditionGroup(LogicalOperator.And, new IBacktestConditionNode[] { new BacktestConditionLeaf(b), new BacktestConditionLeaf(c) }.ToImmutableArray()),
            }.ToImmutableArray()),
            (BacktestConditionSection.Reverse, TradeSide.Short) => new BacktestConditionGroup(LogicalOperator.And, new IBacktestConditionNode[] { new BacktestConditionLeaf(a) }.ToImmutableArray()),
            _ => BacktestConditionGroup.EmptyAnd,
        });
        BacktestConditionTreeViewModel vm = Create(maxDepth: 8, maxNodes: 64);

        vm.Load(source);
        BacktestConditionTree rebuilt = vm.Build();

        AssertSameShape(source, rebuilt);
        Assert.Same(a, ((BacktestConditionLeaf)rebuilt.EntryLong.Children[0]).Comparison);
        Assert.Equal(new BacktestConditionTreeSize(2, 5), BacktestConditionTreeRule.Measure(rebuilt.EntryLong));
    }

    [Fact]
    public void Load_ReplacesPreviousContentAndSelection_KeepsRootInstances()
    {
        BacktestConditionTreeViewModel vm = Create();
        BacktestConditionGroupViewModel root = vm.Root(BacktestConditionSection.Entry, TradeSide.Long);
        vm.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry());
        vm.SelectedNode = root;

        vm.Load(BacktestConditionTree.Empty);

        Assert.Same(root, vm.Root(BacktestConditionSection.Entry, TradeSide.Long));
        Assert.Empty(root.Children);
        Assert.Null(vm.SelectedNode);
        Assert.False(vm.Build().HasAnyLeaf);
    }

    [Fact]
    public void Load_TreeOverTheBound_LoadsAsIs_AndOnlyBlocksFurtherAdds()
    {
        var wide = new BacktestConditionGroup(LogicalOperator.And,
            Enumerable.Range(0, TestNodes + 3).Select(i => (IBacktestConditionNode)new BacktestConditionLeaf(Entry(i))).ToArray().ToImmutableArray());
        BacktestConditionTreeViewModel vm = Create();

        vm.Load(BacktestConditionTree.Create((s, d) => s == BacktestConditionSection.Entry && d == TradeSide.Long ? wide : BacktestConditionGroup.EmptyAnd));

        Assert.Equal(TestNodes + 3, vm.Root(BacktestConditionSection.Entry, TradeSide.Long).Children.Count);
        Assert.Null(vm.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry()));
        vm.Delete(vm.Root(BacktestConditionSection.Entry, TradeSide.Long).Children[0]);
        Assert.Equal(TestNodes + 2, vm.Root(BacktestConditionSection.Entry, TradeSide.Long).Children.Count);
    }

    [Fact]
    public void LeafViewModel_DisplaysTheSharedFormatterText()
    {
        BacktestConditionEntry entry = Entry(5m);

        Assert.Equal(StockAnalyzer.Avalonia.Converters.BacktestConditionFormatter.Format(entry), new BacktestConditionLeafViewModel(entry).DisplayText);
    }

    private static void AssertSameShape(BacktestConditionTree expected, BacktestConditionTree actual)
    {
        foreach ((BacktestConditionSection section, TradeSide side) in BacktestConditionTree.CanonicalRoots)
        {
            AssertSameShape(expected.Root(section, side), actual.Root(section, side));
        }
    }

    private static void AssertSameShape(IBacktestConditionNode expected, IBacktestConditionNode actual)
    {
        switch (expected)
        {
            case BacktestConditionGroup e:
                var g = Assert.IsType<BacktestConditionGroup>(actual);
                Assert.Equal(e.Operator, g.Operator);
                Assert.Equal(e.Children.Length, g.Children.Length);
                for (int i = 0; i < e.Children.Length; i++) AssertSameShape(e.Children[i], g.Children[i]);
                break;
            case BacktestConditionLeaf l:
                Assert.Same(l.Comparison, Assert.IsType<BacktestConditionLeaf>(actual).Comparison);
                break;
            default:
                throw new InvalidOperationException();
        }
    }
}
