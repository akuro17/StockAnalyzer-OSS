using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Screener;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>Named groups, the shared Add/Edit Group dialog and the hover expressions of the condition tree editor (MP-4).</summary>
public class BacktestConditionGroupNameViewModelTests
{
    private const int NameLimit = 8;

    private static BacktestConditionEntry Entry(int value) => new()
    {
        Left = new BacktestConditionSide { IndicatorType = IndicatorType.SMA },
        Operator = ComparisonOperator.GreaterThan,
        TargetMode = RightHandTargetMode.NumericValue,
        RightNumericValue = value,
    };

    private static BacktestConditionTreeViewModel Create(Func<ConditionGroupEditRequest, Task<ConditionGroupEditResult?>>? editor = null)
        => new(maxDepth: 5, maxNodes: 20, limitReason: null, maxNameLength: NameLimit, groupEditor: editor);

    private static BacktestConditionGroupViewModel EntryLong(BacktestConditionTreeViewModel tree) => tree.Root(BacktestConditionSection.Entry, TradeSide.Long);

    /// <summary>Root(AND): [MA](OR: A, B), C  - the structure of the owner's example.</summary>
    private static (BacktestConditionTreeViewModel Tree, BacktestConditionGroupViewModel List) Example(string? name = "MA")
    {
        BacktestConditionTreeViewModel tree = Create();
        BacktestConditionGroupViewModel root = EntryLong(tree);
        Assert.True(tree.TryAddGroup(root, LogicalOperator.Or, name));
        var list = (BacktestConditionGroupViewModel)root.Children[0];
        tree.SelectedNode = list;
        tree.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry(1)); // A
        tree.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry(2)); // B
        tree.SelectedNode = null;
        tree.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry(3)); // C, in the root
        return (tree, list);
    }

    // ---- names ----

    [Fact]
    public void TryAddGroup_StoresTheTrimmedName_AndBuildLoadCarryIt()
    {
        BacktestConditionTreeViewModel tree = Create();
        tree.TryAddGroup(EntryLong(tree), LogicalOperator.Or, "  MA  ");

        var group = (BacktestConditionGroupViewModel)EntryLong(tree).Children[0];
        Assert.Equal("MA", group.Name);
        Assert.True(group.HasName);
        BacktestConditionTree built = tree.Build();
        Assert.Equal("MA", ((BacktestConditionGroup)built.EntryLong.Children[0]).Name);

        BacktestConditionTreeViewModel other = Create();
        other.Load(built);
        Assert.Equal("MA", ((BacktestConditionGroupViewModel)EntryLong(other).Children[0]).Name);
    }

    [Fact]
    public void BlankName_MeansUnnamed()
    {
        BacktestConditionTreeViewModel tree = Create();
        tree.TryAddGroup(EntryLong(tree), LogicalOperator.And, "   ");

        var group = (BacktestConditionGroupViewModel)EntryLong(tree).Children[0];
        Assert.Null(group.Name);
        Assert.False(group.HasName);
    }

    [Fact]
    public void NameLimit_ExactlyAtTheLimitPasses_OneBeyondIsRejected_NothingIsTruncated()
    {
        BacktestConditionTreeViewModel tree = Create();
        Assert.True(tree.TryAddGroup(EntryLong(tree), LogicalOperator.And, new string('x', NameLimit)));

        Assert.Throws<ArgumentException>(() => tree.TryAddGroup(EntryLong(tree), LogicalOperator.And, new string('x', NameLimit + 1)));
        Assert.Single(EntryLong(tree).Children);
        var group = (BacktestConditionGroupViewModel)EntryLong(tree).Children[0];
        Assert.Throws<ArgumentException>(() => tree.UpdateGroup(group, new string('y', NameLimit + 1), LogicalOperator.And));
        Assert.Equal(new string('x', NameLimit), group.Name);
    }

    [Fact]
    public void UpdateGroup_ChangesNameAndOperator_AndARootRefusesAName()
    {
        (BacktestConditionTreeViewModel tree, BacktestConditionGroupViewModel list) = Example();

        tree.UpdateGroup(list, "Renamed", LogicalOperator.And);

        Assert.Equal("Renamed", list.Name);
        Assert.Equal(LogicalOperator.And, list.Operator);
        Assert.Throws<ArgumentException>(() => tree.UpdateGroup(EntryLong(tree), "no", LogicalOperator.And));
    }

    // ---- Add / Edit Group commands with the dialog ----

    [Fact]
    public async Task AddGroupCommand_OpensTheDialogForANewGroup_AndAddsTheConfirmedOne()
    {
        ConditionGroupEditRequest? seen = null;
        BacktestConditionTreeViewModel tree = Create(request =>
        {
            seen = request;
            return Task.FromResult<ConditionGroupEditResult?>(new ConditionGroupEditResult(" Trend ", LogicalOperator.Or));
        });

        await tree.AddGroupCommand.ExecuteAsync(EntryLong(tree));

        Assert.Equal(new ConditionGroupEditRequest(IsNew: true, Name: null, LogicalOperator.And, AllowName: true, MaxNameLength: NameLimit), seen);
        var group = (BacktestConditionGroupViewModel)Assert.Single(EntryLong(tree).Children);
        Assert.Equal("Trend", group.Name);
        Assert.Equal(LogicalOperator.Or, group.Operator);
    }

    [Fact]
    public async Task AddGroupCommand_Cancelled_AddsNothing()
    {
        BacktestConditionTreeViewModel tree = Create(_ => Task.FromResult<ConditionGroupEditResult?>(null));

        await tree.AddGroupCommand.ExecuteAsync(EntryLong(tree));

        Assert.Empty(EntryLong(tree).Children);
    }

    [Fact]
    public async Task AddGroupCommand_WithoutADialog_DoesNothing()
    {
        BacktestConditionTreeViewModel tree = Create(editor: null);

        await tree.AddGroupCommand.ExecuteAsync(EntryLong(tree));

        Assert.Empty(EntryLong(tree).Children);
    }

    [Fact]
    public async Task EditGroupCommand_OpensTheSameDialogWithTheCurrentValues_AndAppliesTheResult()
    {
        ConditionGroupEditRequest? seen = null;
        (BacktestConditionTreeViewModel example, _) = Example();
        BacktestConditionTreeViewModel tree = Create(request =>
        {
            seen = request;
            return Task.FromResult<ConditionGroupEditResult?>(new ConditionGroupEditResult("Other", LogicalOperator.And));
        });
        tree.Load(example.Build());
        var list = (BacktestConditionGroupViewModel)EntryLong(tree).Children[0];

        await tree.EditGroupCommand.ExecuteAsync(list);

        Assert.Equal(new ConditionGroupEditRequest(IsNew: false, "MA", LogicalOperator.Or, AllowName: true, MaxNameLength: NameLimit), seen);
        Assert.Equal("Other", list.Name);
        Assert.Equal(LogicalOperator.And, list.Operator);
    }

    [Fact]
    public async Task EditGroupCommand_OnARoot_DoesNotAllowANameAndOnlyChangesTheOperator()
    {
        ConditionGroupEditRequest? seen = null;
        BacktestConditionTreeViewModel tree = Create(request =>
        {
            seen = request;
            return Task.FromResult<ConditionGroupEditResult?>(new ConditionGroupEditResult("ignored", LogicalOperator.Or));
        });

        await tree.EditGroupCommand.ExecuteAsync(EntryLong(tree));

        Assert.False(seen!.AllowName);
        Assert.Null(EntryLong(tree).Name);
        Assert.Equal(LogicalOperator.Or, EntryLong(tree).Operator);
    }

    // ---- hover expressions ----

    [Fact]
    public void Hover_OnASignal_ShowsOnlyItsList()
    {
        (BacktestConditionTreeViewModel _, BacktestConditionGroupViewModel list) = Example();

        BacktestConditionNodeViewModel a = list.Children[0];
        Assert.Null(a.HoverUpperText);
        Assert.Equal("SMA > 1 OR SMA > 2", a.HoverLowerText);
        Assert.Equal("SMA > 1 OR SMA > 2", list.Children[1].HoverLowerText);
    }

    [Fact]
    public void Hover_OnAList_ShowsTheLevelAboveByName_AndTheLevelBelow()
    {
        (BacktestConditionTreeViewModel tree, BacktestConditionGroupViewModel list) = Example();

        Assert.Equal("[MA] AND SMA > 3", list.HoverUpperText);
        Assert.Equal("SMA > 1 OR SMA > 2", list.HoverLowerText);
        // The root has nothing above; below it, the list appears by name and is not expanded.
        Assert.Null(EntryLong(tree).HoverUpperText);
        Assert.Equal("[MA] AND SMA > 3", EntryLong(tree).HoverLowerText);
        // A signal in the root belongs to the root's list.
        Assert.Equal("[MA] AND SMA > 3", EntryLong(tree).Children[1].HoverLowerText);
    }

    [Fact]
    public void Hover_AnUnnamedGroup_IsShownByItsOperator_AndAnEmptyGroupHasNothingBelow()
    {
        (BacktestConditionTreeViewModel tree, BacktestConditionGroupViewModel list) = Example(name: null);
        tree.TryAddGroup(EntryLong(tree), LogicalOperator.And);
        var empty = (BacktestConditionGroupViewModel)EntryLong(tree).Children[^1];

        Assert.Equal("[OR] AND SMA > 3 AND [AND]", list.HoverUpperText);
        Assert.Null(empty.HoverLowerText);
        Assert.Equal("[OR] AND SMA > 3 AND [AND]", empty.HoverUpperText);
    }

    [Fact]
    public void Hover_FollowsEditsImmediately()
    {
        (BacktestConditionTreeViewModel tree, BacktestConditionGroupViewModel list) = Example();

        tree.UpdateGroup(list, "Trend", LogicalOperator.And);
        Assert.Equal("[Trend] AND SMA > 3", list.HoverUpperText);
        Assert.Equal("SMA > 1 AND SMA > 2", list.HoverLowerText);
        Assert.Equal("SMA > 1 AND SMA > 2", list.Children[0].HoverLowerText);

        tree.Delete(list.Children[1]);
        Assert.Equal("SMA > 1", list.HoverLowerText);
    }

    [Fact]
    public void Hover_AnEmptyList_HasNoHoverText_SoNoEmptyTooltipIsOpened_UntilItGetsASignal()
    {
        BacktestConditionTreeViewModel tree = Create();
        BacktestConditionGroupViewModel root = EntryLong(tree);
        var changed = new List<string?>();
        root.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Null(root.HoverUpperText);
        Assert.Null(root.HoverLowerText);
        Assert.False(root.HasHoverText);

        Assert.NotNull(tree.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry(1)));
        Assert.True(root.HasHoverText);
        Assert.Contains(nameof(BacktestConditionNodeViewModel.HasHoverText), changed);
        Assert.True(root.Children[0].HasHoverText);

        tree.Delete(root.Children[0]);
        Assert.False(root.HasHoverText);
    }

    [Fact]
    public void Hover_AnEmptySubGroup_StillShowsTheListAboveIt()
    {
        (BacktestConditionTreeViewModel tree, BacktestConditionGroupViewModel _) = Example(name: null);
        tree.TryAddGroup(EntryLong(tree), LogicalOperator.And);
        var empty = (BacktestConditionGroupViewModel)EntryLong(tree).Children[^1];

        Assert.Null(empty.HoverLowerText);
        Assert.True(empty.HasHoverText);
    }

    // ---- dialog view-model ----

    [Fact]
    public void Dialog_StartsFromTheRequest_AndBuildsTheResult()
    {
        var vm = new ConditionGroupEditDialogViewModel(new ConditionGroupEditRequest(IsNew: false, "MA", LogicalOperator.Or, AllowName: true, NameLimit));

        Assert.False(vm.IsNew);
        Assert.Equal("MA", vm.Name);
        Assert.False(vm.IsAndOperator);
        Assert.True(vm.IsOrOperator);
        Assert.Equal(NameLimit, vm.MaxNameLength);

        vm.IsOrOperator = false;
        vm.Name = "Changed";

        Assert.True(vm.IsAndOperator);
        Assert.Equal(new ConditionGroupEditResult("Changed", LogicalOperator.And), vm.BuildResult());
    }

    [Fact]
    public void Dialog_ForARoot_ReturnsNoName()
    {
        var vm = new ConditionGroupEditDialogViewModel(new ConditionGroupEditRequest(IsNew: false, null, LogicalOperator.And, AllowName: false, NameLimit));
        vm.Name = "typed anyway";

        Assert.False(vm.AllowName);
        Assert.Null(vm.BuildResult().Name);
    }

    [Fact]
    public void Dialog_RadioButtonsNotifyEachOther()
    {
        var vm = new ConditionGroupEditDialogViewModel(new ConditionGroupEditRequest(IsNew: true, null, LogicalOperator.And, AllowName: true, NameLimit));
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.IsAndOperator = false;

        Assert.Contains(nameof(ConditionGroupEditDialogViewModel.IsOrOperator), changed);
    }

    // ---- selection view-model wires the dialog service ----

    [Fact]
    public async Task SelectionViewModel_OpensTheDialogThroughTheDialogService()
    {
        var dialogs = new FakeDialogService
        {
            ConditionGroupResult = new ConditionGroupEditResult("Trend", LogicalOperator.Or),
        };
        var selection = new BacktestIndicatorSelectionViewModel(
            new FakeScreenerCatalogProvider(Array.Empty<ScreenerCatalogItem>()),
            StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance,
            toastService: null,
            indicatorFactory: null,
            settings: null,
            dialogService: dialogs);

        await selection.ConditionTree.AddGroupCommand.ExecuteAsync(selection.ConditionTree.Root(BacktestConditionSection.Exit, TradeSide.Short));

        Assert.True(dialogs.LastConditionGroupDialog!.IsNew);
        Assert.Equal(selection.ConditionTree.MaxNameLength, dialogs.LastConditionGroupDialog.MaxNameLength);
        var group = (BacktestConditionGroupViewModel)Assert.Single(selection.ConditionTree.Root(BacktestConditionSection.Exit, TradeSide.Short).Children);
        Assert.Equal("Trend", group.Name);
    }

    [Fact]
    public async Task SelectionViewModel_WithoutADialogService_EditsNothing()
    {
        var selection = new BacktestIndicatorSelectionViewModel(
            new FakeScreenerCatalogProvider(Array.Empty<ScreenerCatalogItem>()),
            StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance);

        await selection.ConditionTree.AddGroupCommand.ExecuteAsync(selection.ConditionTree.Root(BacktestConditionSection.Entry, TradeSide.Long));

        Assert.Empty(selection.ConditionTree.Root(BacktestConditionSection.Entry, TradeSide.Long).Children);
    }
}
