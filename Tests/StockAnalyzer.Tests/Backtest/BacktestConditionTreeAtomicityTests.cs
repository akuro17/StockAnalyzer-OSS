using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>
/// A rejected edit, load or dialog result leaves the condition tree exactly as it was, and the run path enforces the same
/// configured name limit as the save path (MP-A of the review plan: validate before mutating, no partially applied state).
/// </summary>
public class BacktestConditionTreeAtomicityTests
{
    private const int NameLimit = 8;

    private static BacktestConditionEntry Entry(decimal value = 0m) => new()
    {
        Left = new BacktestConditionSide { IndicatorType = IndicatorType.Price, PriceSource = PriceType.Close },
        Operator = ComparisonOperator.GreaterThan,
        TargetMode = RightHandTargetMode.NumericValue,
        RightNumericValue = value,
    };

    private static BacktestConditionEntry LegacyEntry() => new()
    {
        Left = new BacktestConditionSide { IndicatorType = IndicatorType.Price, PriceSource = PriceType.Close },
        Operator = ComparisonOperator.GreaterThan,
        TargetMode = RightHandTargetMode.NumericValue,
        RightNumericValue = 1m,
        Role = BacktestConditionRole.EntryOnly,
        Position = TradeSide.Long,
    };

    private static BacktestConditionTreeViewModel Create(Func<ConditionGroupEditRequest, Task<ConditionGroupEditResult?>>? editor = null)
        => new(4, 12, () => "limit", NameLimit, editor, () => "stale");

    private static BacktestConditionGroupViewModel EntryLong(BacktestConditionTreeViewModel tree)
        => tree.Root(BacktestConditionSection.Entry, TradeSide.Long);

    private static BacktestConditionGroupViewModel AddNamedGroupWithLeaf(BacktestConditionTreeViewModel tree, string name)
    {
        BacktestConditionGroupViewModel root = EntryLong(tree);
        Assert.True(tree.TryAddGroup(root, LogicalOperator.Or, name));
        var group = (BacktestConditionGroupViewModel)root.Children[^1];
        tree.SelectedNode = group;
        tree.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry());
        tree.SelectedNode = null;
        return group;
    }

    [Fact]
    public void GroupChildren_AreReadOnlyOutsideTheTreeViewModel()
    {
        BacktestConditionTreeViewModel tree = Create();
        BacktestConditionGroupViewModel root = EntryLong(tree);

        var list = (System.Collections.IList)root.Children;

        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Add(new BacktestConditionLeafViewModel(Entry())));
        Assert.Empty(root.Children);
    }

    // ---- a rejected edit changes nothing ----

    [Fact]
    public void UpdateGroup_WithAnUndefinedOperator_ThrowsAndKeepsNameAndOperator()
    {
        BacktestConditionTreeViewModel tree = Create();
        BacktestConditionGroupViewModel group = AddNamedGroupWithLeaf(tree, "MA");
        int revision = tree.Revision;

        Assert.Throws<ArgumentOutOfRangeException>(() => tree.UpdateGroup(group, "new", (LogicalOperator)99));

        Assert.Equal("MA", group.Name);
        Assert.Equal(LogicalOperator.Or, group.Operator);
        Assert.Equal(revision, tree.Revision);
        _ = tree.Build();
    }

    [Fact]
    public void UpdateGroup_WithATooLongName_ThrowsAndKeepsNameAndOperator()
    {
        BacktestConditionTreeViewModel tree = Create();
        BacktestConditionGroupViewModel group = AddNamedGroupWithLeaf(tree, "MA");

        Assert.Throws<ArgumentException>(() => tree.UpdateGroup(group, new string('x', NameLimit + 1), LogicalOperator.And));

        Assert.Equal("MA", group.Name);
        Assert.Equal(LogicalOperator.Or, group.Operator);
    }

    [Fact]
    public void TryAddGroup_WithAnUndefinedOperator_ThrowsAndAddsNothing()
    {
        BacktestConditionTreeViewModel tree = Create();
        BacktestConditionGroupViewModel root = EntryLong(tree);

        Assert.Throws<ArgumentOutOfRangeException>(() => tree.TryAddGroup(root, (LogicalOperator)99, "x"));

        Assert.Empty(root.Children);
    }

    [Fact]
    public void EditingANodeOfAnotherTree_IsRejected_AndTheOtherTreeIsUnchanged()
    {
        BacktestConditionTreeViewModel mine = Create();
        BacktestConditionTreeViewModel other = Create();
        BacktestConditionGroupViewModel foreignGroup = AddNamedGroupWithLeaf(other, "MA");
        BacktestConditionNodeViewModel foreignLeaf = foreignGroup.Children[0];

        Assert.Throws<ArgumentException>(() => mine.UpdateGroup(foreignGroup, "z", LogicalOperator.And));
        Assert.Throws<ArgumentException>(() => mine.TryAddGroup(foreignGroup, LogicalOperator.And));
        Assert.Throws<ArgumentException>(() => mine.Delete(foreignLeaf));
        Assert.False(mine.CanAddGroup(foreignGroup));
        Assert.False(mine.DeleteNodeCommand.CanExecute(foreignLeaf));
        Assert.False(mine.EditGroupCommand.CanExecute(foreignGroup));

        Assert.Equal("MA", foreignGroup.Name);
        Assert.Single(foreignGroup.Children);
        Assert.Same(foreignGroup, foreignLeaf.Parent);
    }

    [Fact]
    public void DeletingADetachedNode_IsRejected_AndTheTreeIsUnchanged()
    {
        BacktestConditionTreeViewModel tree = Create();
        BacktestConditionGroupViewModel group = AddNamedGroupWithLeaf(tree, "MA");
        BacktestConditionNodeViewModel leaf = group.Children[0];
        tree.Delete(leaf);
        Assert.DoesNotContain(group, EntryLong(tree).Children);
        int revision = tree.Revision;

        Assert.Throws<ArgumentException>(() => tree.Delete(leaf));
        Assert.False(tree.DeleteNodeCommand.CanExecute(leaf));

        Assert.Equal(revision, tree.Revision);
    }

    [Theory]
    [InlineData(1, 12)]
    [InlineData(4, 1)]
    public void NewTree_WhoseLimitsAlreadyForbidAGroup_ShowsTheReasonOnEveryRootFromTheStart(int maxDepth, int maxNodes)
    {
        var tree = new BacktestConditionTreeViewModel(maxDepth, maxNodes, () => "limit");

        Assert.All(tree.Roots, root => Assert.Equal("limit", root.AddGroupToolTip));
        Assert.All(tree.Roots, root => Assert.False(tree.CanAddGroup(root)));
    }

    // ---- load ----

    [Fact]
    public void Load_DetachesTheReplacedNodes_AndKeepsTheRootIdentities()
    {
        BacktestConditionTreeViewModel tree = Create();
        BacktestConditionGroupViewModel oldGroup = AddNamedGroupWithLeaf(tree, "MA");
        BacktestConditionNodeViewModel oldLeaf = oldGroup.Children[0];
        BacktestConditionGroupViewModel[] roots = tree.Roots.ToArray();
        tree.SelectedNode = oldGroup;

        tree.Load(BacktestConditionTreeMigrator.Migrate(new[] { LegacyEntry() }));

        Assert.Null(oldGroup.Parent);
        Assert.Null(oldGroup.Tree);
        Assert.Null(oldLeaf.Tree);
        Assert.Null(tree.SelectedNode);
        Assert.Equal(roots, tree.Roots);
        // A stale reference can no longer edit the tree.
        Assert.Throws<ArgumentException>(() => tree.UpdateGroup(oldGroup, "x", LogicalOperator.And));
        Assert.False(tree.CanAddGroup(oldGroup));
    }

    // ---- dialog results are discarded when the tree changed meanwhile ----

    [Fact]
    public async Task AddGroupResult_IsDiscardedWithAReason_WhenTheTreeChangedWhileTheDialogWasOpen()
    {
        BacktestConditionTreeViewModel? treeRef = null;
        var tree = Create(request =>
        {
            treeRef!.Load(BacktestConditionTree.Empty);
            return Task.FromResult<ConditionGroupEditResult?>(new ConditionGroupEditResult("late", LogicalOperator.And));
        });
        treeRef = tree;
        var reasons = new List<string>();
        tree.EditRejected += (_, reason) => reasons.Add(reason);
        BacktestConditionGroupViewModel root = EntryLong(tree);

        await tree.AddGroupCommand.ExecuteAsync(root);

        Assert.Equal(new[] { "stale" }, reasons);
        Assert.Empty(root.Children);
    }

    [Fact]
    public async Task EditGroupResult_IsDiscardedWithAReason_WhenTheEditedGroupWasDeletedWhileTheDialogWasOpen()
    {
        BacktestConditionTreeViewModel? treeRef = null;
        BacktestConditionGroupViewModel? target = null;
        var tree = Create(request =>
        {
            treeRef!.Delete(target!);
            return Task.FromResult<ConditionGroupEditResult?>(new ConditionGroupEditResult("late", LogicalOperator.And));
        });
        treeRef = tree;
        target = AddNamedGroupWithLeaf(tree, "MA");
        var reasons = new List<string>();
        tree.EditRejected += (_, reason) => reasons.Add(reason);

        await tree.EditGroupCommand.ExecuteAsync(target);

        Assert.Single(reasons);
        Assert.Equal("MA", target.Name);
    }

    [Fact]
    public async Task DialogResult_IsApplied_WhenTheTreeIsUnchanged()
    {
        var tree = Create(request => Task.FromResult<ConditionGroupEditResult?>(new ConditionGroupEditResult("ok", LogicalOperator.Or)));
        var reasons = new List<string>();
        tree.EditRejected += (_, reason) => reasons.Add(reason);

        await tree.AddGroupCommand.ExecuteAsync(EntryLong(tree));

        Assert.Empty(reasons);
        var added = Assert.IsType<BacktestConditionGroupViewModel>(Assert.Single(EntryLong(tree).Children));
        Assert.Equal("ok", added.Name);
    }

    // ---- the window passes the configured name limit to the run ----

    [Fact]
    public async Task Run_WithAGroupNameOverTheConfiguredLimit_IsRejected_AndRunsNothing()
    {
        var engine = new ScriptableBacktestEngine();
        BacktestWindowViewModel vm = BacktestTestFactory.CreateViewModel(engine: engine);
        vm.Symbol = "TEST";
        BacktestConditionTreeViewModel tree = vm.IndicatorSelection.ConditionTree;
        BacktestConditionGroupViewModel group = AddNamedGroupWithLeaf(vm.IndicatorSelection.ConditionTree, "MA");
        // The dialog limits the input, but the property is public: the run must still enforce the limit.
        group.Name = new string('x', tree.MaxNameLength + 1);

        await vm.RunBacktestCommand.ExecuteAsync(null);

        Assert.NotEqual(BacktestRunState.Completed, vm.State);
        Assert.Null(engine.LastStrategy);
    }
}
