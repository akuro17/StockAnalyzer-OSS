using System.Linq;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>State the Registered Conditions tree view binds to (MP-2 Phase 3): section roots, selection mirroring, row flags, disabled-reason tooltip, summary and limit warning.</summary>
public class BacktestConditionTreeDisplayStateTests
{
    private static BacktestConditionEntry Entry(decimal value = 0m) => new()
    {
        Left = new BacktestConditionSide { IndicatorType = IndicatorType.Price, PriceSource = PriceType.Close },
        Operator = ComparisonOperator.GreaterThan,
        TargetMode = RightHandTargetMode.NumericValue,
        RightNumericValue = value,
    };

    /// <summary>Returns no resource text, so the view-model's own fallback formats (with the numbers filled in) are what the test sees.</summary>
    private sealed class FallbackFormatLocalization : ILocalizationService
    {
        public string GetString(string key) => string.Empty;
    }

    private static BacktestIndicatorSelectionViewModel CreateSelection()
        => new(new FakeScreenerCatalogProvider(System.Array.Empty<ScreenerCatalogItem>()), new FallbackFormatLocalization());

    [Fact]
    public void SectionRoots_AreTheLongAndShortRootOfEachSection()
    {
        var tree = new BacktestConditionTreeViewModel(4, 6);

        Assert.Equal(new[] { TradeSide.Long, TradeSide.Short }, tree.EntryRoots.Select(r => r.Side));
        Assert.All(tree.EntryRoots, r => Assert.Equal(BacktestConditionSection.Entry, r.Section));
        Assert.All(tree.ExitRoots, r => Assert.Equal(BacktestConditionSection.Exit, r.Section));
        Assert.All(tree.ReverseRoots, r => Assert.Equal(BacktestConditionSection.Reverse, r.Section));
        Assert.Same(tree.Root(BacktestConditionSection.Reverse, TradeSide.Short), tree.ReverseRoots[1]);
    }

    [Fact]
    public void NodeIsSelected_MakesItTheSelectedNode_AndClearsThePreviousOne()
    {
        var tree = new BacktestConditionTreeViewModel(4, 6);
        BacktestConditionGroupViewModel a = tree.Root(BacktestConditionSection.Entry, TradeSide.Long);
        BacktestConditionGroupViewModel b = tree.Root(BacktestConditionSection.Exit, TradeSide.Long);

        a.IsSelected = true;
        Assert.Same(a, tree.SelectedNode);

        b.IsSelected = true;

        Assert.Same(b, tree.SelectedNode);
        Assert.False(a.IsSelected);

        b.IsSelected = false;
        Assert.Null(tree.SelectedNode);
    }

    [Fact]
    public void RemovedNode_DoesNotChangeTheSelection()
    {
        var tree = new BacktestConditionTreeViewModel(4, 6);
        tree.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry());
        var leaf = tree.Root(BacktestConditionSection.Entry, TradeSide.Long).Children[0];
        tree.Delete(leaf);
        tree.SelectedNode = tree.Root(BacktestConditionSection.Exit, TradeSide.Long);

        leaf.IsSelected = true;

        Assert.Same(tree.Root(BacktestConditionSection.Exit, TradeSide.Long), tree.SelectedNode);
        Assert.Null(leaf.Tree);
    }

    [Fact]
    public void RootFlags_DescribeSideOperatorAndEmptiness()
    {
        var tree = new BacktestConditionTreeViewModel(4, 6);
        BacktestConditionGroupViewModel reverseShort = tree.Root(BacktestConditionSection.Reverse, TradeSide.Short);
        BacktestConditionGroupViewModel entryLong = tree.Root(BacktestConditionSection.Entry, TradeSide.Long);

        Assert.True(entryLong.IsLongSide);
        Assert.False(reverseShort.IsLongSide);
        Assert.True(reverseShort.IsReverseRoot);
        Assert.False(entryLong.IsReverseRoot);
        Assert.True(entryLong.IsEmptyRoot);
        Assert.True(entryLong.IsAndOperator);

        var changed = new System.Collections.Generic.List<string?>();
        entryLong.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        tree.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry());
        tree.UpdateGroup(entryLong, null, LogicalOperator.Or);

        Assert.False(entryLong.IsEmptyRoot);
        Assert.False(entryLong.IsAndOperator);
        Assert.Contains(nameof(BacktestConditionGroupViewModel.IsEmptyRoot), changed);
        Assert.Contains(nameof(BacktestConditionGroupViewModel.IsAndOperator), changed);
    }

    [Fact]
    public void AddGroupToolTip_IsNullWhileAddingIsPossible_AndTheLimitTextOnceItIsNot()
    {
        var tree = new BacktestConditionTreeViewModel(4, 2, () => "limit text");
        BacktestConditionGroupViewModel root = tree.Root(BacktestConditionSection.Entry, TradeSide.Long);
        tree.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry());
        Assert.Equal("limit text", root.AddGroupToolTip);

        tree.Delete(root.Children[0]);

        Assert.Null(root.AddGroupToolTip);
    }

    [Fact]
    public void StructureChanged_IsRaisedByAddDeleteLoadAndGroupEdit_NotBySelection()
    {
        var tree = new BacktestConditionTreeViewModel(4, 6);
        int raised = 0;
        tree.StructureChanged += (_, _) => raised++;

        tree.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry());
        Assert.Equal(1, raised);
        tree.SelectedNode = tree.Root(BacktestConditionSection.Entry, TradeSide.Long);
        Assert.Equal(1, raised);
        tree.Delete(tree.Root(BacktestConditionSection.Entry, TradeSide.Long).Children[0]);
        Assert.Equal(2, raised);
        tree.Load(BacktestConditionTree.Empty);
        Assert.Equal(3, raised);
        tree.UpdateGroup(tree.Root(BacktestConditionSection.Entry, TradeSide.Long), null, LogicalOperator.Or);
        Assert.Equal(4, raised);
    }

    [Fact]
    public void Summary_CountsLeavesGroupsAndTheLargestSection()
    {
        BacktestIndicatorSelectionViewModel selection = CreateSelection();
        BacktestConditionTreeViewModel tree = selection.ConditionTree;
        BacktestConditionGroupViewModel root = tree.Root(BacktestConditionSection.Entry, TradeSide.Long);
        tree.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry(1m));
        tree.TryAddGroup(root, LogicalOperator.Or);
        tree.SelectedNode = root.Children[1];
        tree.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry(2m));
        tree.TryAddLeaf(BacktestConditionSection.Exit, TradeSide.Short, Entry(3m));

        // The fallback format is used: leaves 3, one non-root group, largest section (EntryLong) = root + leaf + group + leaf = 4 nodes.
        Assert.Equal($"Leaves 3 / Groups 1 / Largest section 4 of {tree.MaxNodes} nodes", selection.ConditionTreeSummaryText);
        Assert.Null(selection.ConditionTreeLimitWarning);
    }

    [Fact]
    public void LimitWarning_AppearsForATreeOverTheLimit_AndDisablesNothing()
    {
        BacktestIndicatorSelectionViewModel selection = CreateSelection();
        BacktestConditionTreeViewModel tree = selection.ConditionTree;
        var wide = new BacktestConditionGroup(LogicalOperator.And,
            Enumerable.Range(0, tree.MaxNodes + 2).Select(i => (IBacktestConditionNode)new BacktestConditionLeaf(Entry(i))).ToImmutableArrayOfNodes());

        tree.Load(BacktestConditionTree.Create((s, d) => s == BacktestConditionSection.Entry && d == TradeSide.Long ? wide : BacktestConditionGroup.EmptyAnd));

        Assert.NotNull(selection.ConditionTreeLimitWarning);
        Assert.Contains("EntryLong", selection.ConditionTreeLimitWarning);
        Assert.Contains((tree.MaxNodes + 3).ToString(), selection.ConditionTreeLimitWarning);
        tree.Load(BacktestConditionTree.Empty);
        Assert.Null(selection.ConditionTreeLimitWarning);
    }
}

internal static class NodeArrayExtensions
{
    public static System.Collections.Immutable.ImmutableArray<IBacktestConditionNode> ToImmutableArrayOfNodes(this System.Collections.Generic.IEnumerable<IBacktestConditionNode> nodes)
        => System.Collections.Immutable.ImmutableArray.CreateRange(nodes);
}
