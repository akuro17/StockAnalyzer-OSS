using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Avalonia.ViewModels.Screener;
using StockAnalyzer.Core.Models.Screener;
using Xunit;

namespace StockAnalyzer.Tests.Screener;

/// <summary>
/// Filters condition-tree editor view-model layer (Y:\Temp\sa_implementation_plan_ScreenerFilterConditionTree.md Task 2/5).
/// Plain view-model tests: no Avalonia control, no UI thread. Mirrors the coverage shape of
/// StockAnalyzer.Tests.Backtest.BacktestConditionTreeViewModelTests, reduced to a single root (no Section/Side).
/// </summary>
public class ScreenerConditionTreeViewModelTests
{
    private const int TestDepth = 4;
    private const int TestNodes = 6;
    private const int TestNameLength = 16;

    private static ScreenerConditionTreeViewModel Create(
        int maxDepth = TestDepth,
        int maxNodes = TestNodes,
        int maxNameLength = TestNameLength,
        Func<ConditionGroupEditRequest, Task<ConditionGroupEditResult?>>? groupEditor = null)
        => new(maxDepth, maxNodes, maxNameLength, groupEditor: groupEditor);

    private static ScreenerIndicatorEntry Entry() => new();

    private static int LeafCount(ScreenerConditionGroupViewModel group)
        => group.Children.Sum(c => c switch { ScreenerConditionGroupViewModel g => LeafCount(g), _ => 1 });

    // ---- structure ----

    [Fact]
    public void NewTree_HasOneEmptyAndRoot()
    {
        var vm = Create();

        Assert.Single(vm.Roots);
        Assert.Same(vm.Root, vm.Roots[0]);
        Assert.True(vm.Root.IsRoot);
        Assert.Empty(vm.Root.Children);
        Assert.Equal(LogicalOperator.And, vm.Root.Operator);
        Assert.Equal(1, vm.Root.Depth);
        Assert.True(vm.Root.IsEmptyRoot);
        Assert.False(ScreenerConditionTreeEvaluatorHasActiveLeaf(vm));
    }

    private static bool ScreenerConditionTreeEvaluatorHasActiveLeaf(ScreenerConditionTreeViewModel vm)
        => StockAnalyzer.Core.Services.Screener.ScreenerConditionTreeEvaluator.HasActiveLeaf(vm.Build());

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, 0)]
    public void Constructor_RejectsBoundsBelowOne(int maxDepth, int maxNodes, int maxNameLength)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(maxDepth, maxNodes, maxNameLength));
    }

    // ---- add leaf ----

    [Fact]
    public void AddLeaf_NothingSelected_GoesToRoot()
    {
        var vm = Create();

        ScreenerConditionGroupViewModel? target = vm.TryAddLeaf(Entry());

        Assert.Same(vm.Root, target);
        Assert.Single(vm.Root.Children);
        Assert.Equal(1, LeafCount(vm.Root));
        Assert.False(vm.Root.IsEmptyRoot);
    }

    [Fact]
    public void AddLeaf_GroupSelected_GoesIntoThatGroup()
    {
        var vm = Create();
        Assert.True(vm.TryAddGroup(vm.Root, LogicalOperator.Or));
        var group = (ScreenerConditionGroupViewModel)vm.Root.Children[0];
        vm.SelectedNode = group;

        ScreenerConditionGroupViewModel? target = vm.TryAddLeaf(Entry());

        Assert.Same(group, target);
        Assert.Single(group.Children);
    }

    [Fact]
    public void AddLeaf_LeafSelected_StillGoesToRoot_NotIntoTheLeaf()
    {
        var vm = Create();
        vm.TryAddLeaf(Entry());
        vm.SelectedNode = vm.Root.Children[0];

        ScreenerConditionGroupViewModel? target = vm.TryAddLeaf(Entry());

        Assert.Same(vm.Root, target);
        Assert.Equal(2, vm.Root.Children.Count);
    }

    [Fact]
    public void AddLeaf_AtNodeLimit_ReturnsNull()
    {
        var vm = Create(maxDepth: TestDepth, maxNodes: 2);
        Assert.NotNull(vm.TryAddLeaf(Entry())); // root(1) + leaf(2) = at limit

        ScreenerConditionGroupViewModel? overLimit = vm.TryAddLeaf(Entry());

        Assert.Null(overLimit);
        Assert.Single(vm.Root.Children);
    }

    // ---- add group ----

    [Fact]
    public void AddGroup_RootAtMaxDepth_IsRejected()
    {
        var vm = Create(maxDepth: 1, maxNodes: TestNodes);
        Assert.False(vm.CanAddGroup(vm.Root));
        Assert.False(vm.TryAddGroup(vm.Root, LogicalOperator.And));
    }

    [Fact]
    public void AddGroup_NameLongerThanLimit_Throws()
    {
        var vm = Create(maxNameLength: 4);
        Assert.Throws<ArgumentException>(() => vm.TryAddGroup(vm.Root, LogicalOperator.And, "TooLong"));
    }

    [Fact]
    public void AddGroup_BlankName_NormalizesToNull()
    {
        var vm = Create();
        Assert.True(vm.TryAddGroup(vm.Root, LogicalOperator.And, "   "));
        Assert.Null(((ScreenerConditionGroupViewModel)vm.Root.Children[0]).Name);
    }

    // ---- edit group ----

    [Fact]
    public void UpdateGroup_Root_RejectsAName()
    {
        var vm = Create();
        Assert.Throws<ArgumentException>(() => vm.UpdateGroup(vm.Root, "Named", LogicalOperator.Or));
    }

    [Fact]
    public void UpdateGroup_NestedGroup_ChangesNameAndOperator()
    {
        var vm = Create();
        vm.TryAddGroup(vm.Root, LogicalOperator.And);
        var group = (ScreenerConditionGroupViewModel)vm.Root.Children[0];

        vm.UpdateGroup(group, "Renamed", LogicalOperator.Or);

        Assert.Equal("Renamed", group.Name);
        Assert.Equal(LogicalOperator.Or, group.Operator);
        Assert.False(group.IsAndOperator);
    }

    // ---- delete ----

    [Fact]
    public void Delete_Root_ClearsChildrenButKeepsRoot()
    {
        var vm = Create();
        vm.TryAddLeaf(Entry());

        vm.Delete(vm.Root);

        Assert.True(vm.Root.IsRoot);
        Assert.Empty(vm.Root.Children);
    }

    [Fact]
    public void Delete_LastLeafInNestedGroup_RemovesTheEmptiedGroupToo()
    {
        var vm = Create();
        vm.TryAddGroup(vm.Root, LogicalOperator.And);
        var group = (ScreenerConditionGroupViewModel)vm.Root.Children[0];
        vm.SelectedNode = group;
        vm.TryAddLeaf(Entry());
        var leaf = group.Children[0];

        vm.Delete(leaf);

        Assert.Empty(vm.Root.Children); // the now-empty nested group was pruned too
    }

    [Fact]
    public void Delete_Leaf_RaisesLeafDeletedWithItsEntry()
    {
        var vm = Create();
        var entry = Entry();
        vm.TryAddLeaf(entry);
        ScreenerIndicatorEntry? raised = null;
        vm.LeafDeleted += (_, e) => raised = e;

        vm.Delete(vm.Root.Children[0]);

        Assert.Same(entry, raised);
    }

    [Fact]
    public void Delete_Group_DoesNotRaiseLeafDeleted()
    {
        var vm = Create();
        vm.TryAddGroup(vm.Root, LogicalOperator.And);
        bool raised = false;
        vm.LeafDeleted += (_, _) => raised = true;

        vm.Delete(vm.Root.Children[0]);

        Assert.False(raised);
    }

    [Fact]
    public void RemoveLeafFor_FindsAndDeletesTheMatchingLeafWherever()
    {
        var vm = Create();
        vm.TryAddGroup(vm.Root, LogicalOperator.And);
        var group = (ScreenerConditionGroupViewModel)vm.Root.Children[0];
        vm.SelectedNode = group;
        var entry = Entry();
        vm.TryAddLeaf(entry);

        vm.RemoveLeafFor(entry);

        Assert.Empty(vm.Root.Children); // group was pruned once its only leaf was removed
    }

    [Fact]
    public void RemoveLeafFor_UnknownEntry_IsANoOp()
    {
        var vm = Create();
        vm.TryAddLeaf(Entry());

        vm.RemoveLeafFor(new ScreenerIndicatorEntry());

        Assert.Single(vm.Root.Children);
    }

    // ---- build / load round-trip ----

    [Fact]
    public void Build_ThenLoad_RoundTripsStructure()
    {
        var vm = Create();
        vm.TryAddGroup(vm.Root, LogicalOperator.Or, "Group1");
        var group = (ScreenerConditionGroupViewModel)vm.Root.Children[0];
        vm.SelectedNode = group;
        vm.TryAddLeaf(Entry());

        ScreenerConditionGroup snapshot = vm.Build();
        var vm2 = Create();
        vm2.Load(snapshot);

        Assert.Single(vm2.Root.Children);
        var loadedGroup = Assert.IsType<ScreenerConditionGroupViewModel>(vm2.Root.Children[0]);
        Assert.Equal("Group1", loadedGroup.Name);
        Assert.Equal(LogicalOperator.Or, loadedGroup.Operator);
        Assert.Single(loadedGroup.Children);
        Assert.IsType<ScreenerConditionLeafViewModel>(loadedGroup.Children[0]);
    }

    // ---- Load bounds enforcement (constraint-check finding, 2026-09-27: Load() used to attach an untrusted tree with
    // no MaxDepth/MaxNodes check at all, unlike TryAddGroup/TryAddLeaf) ----

    [Fact]
    public void Load_TreeExceedingMaxNodes_ThrowsAndLeavesCurrentContentUntouched()
    {
        var vm = Create(maxNodes: 2); // root(1) + 1 leaf = at the limit already
        vm.TryAddLeaf(Entry());

        var oversized = new ScreenerConditionGroup(LogicalOperator.And, ImmutableArray.Create<IScreenerConditionNode>(
            new ScreenerConditionLeaf(new ScreenerIndicatorEntry()), new ScreenerConditionLeaf(new ScreenerIndicatorEntry()))); // root + 2 leaves = 3 > 2

        Assert.Throws<ArgumentException>(() => vm.Load(oversized));
        Assert.Single(vm.Root.Children); // the pre-Load content is untouched
    }

    [Fact]
    public void Load_TreeExceedingMaxDepth_ThrowsAndLeavesCurrentContentUntouched()
    {
        var vm = Create(maxDepth: 1); // the root only; no nested group may exist
        vm.TryAddLeaf(Entry());

        var nested = new ScreenerConditionGroup(LogicalOperator.And, ImmutableArray.Create<IScreenerConditionNode>(new ScreenerConditionLeaf(new ScreenerIndicatorEntry())));
        var oversized = new ScreenerConditionGroup(LogicalOperator.And, ImmutableArray.Create<IScreenerConditionNode>(nested));

        Assert.Throws<ArgumentException>(() => vm.Load(oversized));
        Assert.Single(vm.Root.Children); // the pre-Load content is untouched
    }

    // ---- Add/Edit Group dialog commands (stale-edit rejection) ----

    [Fact]
    public async Task AddGroupCommand_TreeChangedWhileDialogOpen_RejectsAndDoesNotAdd()
    {
        ScreenerConditionTreeViewModel? vm = null;
        var vmRef = Create(groupEditor: async _ =>
        {
            // Simulate the tree changing (another add) while the dialog was "open".
            vm!.TryAddLeaf(Entry());
            await Task.Yield();
            return new ConditionGroupEditResult("New", LogicalOperator.And);
        });
        vm = vmRef;
        string? rejectedReason = null;
        vm.EditRejected += (_, reason) => rejectedReason = reason;

        await vm.AddGroupCommand.ExecuteAsync(vm.Root);

        Assert.NotNull(rejectedReason);
        Assert.Empty(vm.Root.Children.OfType<ScreenerConditionGroupViewModel>()); // the group from the stale dialog result was not added
        Assert.Single(vm.Root.Children); // only the leaf added mid-dialog is present
    }

    [Fact]
    public async Task AddGroupCommand_ConfirmedResult_AddsGroupWithNameAndOperator()
    {
        var vm = Create(groupEditor: _ => Task.FromResult<ConditionGroupEditResult?>(new ConditionGroupEditResult("Approved", LogicalOperator.Or)));

        await vm.AddGroupCommand.ExecuteAsync(vm.Root);

        var added = Assert.IsType<ScreenerConditionGroupViewModel>(Assert.Single(vm.Root.Children));
        Assert.Equal("Approved", added.Name);
        Assert.Equal(LogicalOperator.Or, added.Operator);
    }

    [Fact]
    public async Task AddGroupCommand_CancelledDialog_AddsNothing()
    {
        var vm = Create(groupEditor: _ => Task.FromResult<ConditionGroupEditResult?>(null));

        await vm.AddGroupCommand.ExecuteAsync(vm.Root);

        Assert.Empty(vm.Root.Children);
    }
}
