using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>
/// The right-column condition builder of <see cref="BacktestIndicatorSelectionViewModel"/> - Left/Right Focus-Bind target state,
/// the Entry/Exit/Reverse buttons (each adds a leaf to the condition tree, MP-2 Phase 2), the Output picker, and the persisted-tree round trip.
/// </summary>
public class BacktestConditionBuilderViewModelTests
{
    private static ScreenerCatalogItem SmaItem => new() { CategoryType = ScreenerItemCategoryType.Indicator, GroupName = "MA", ShortName = "SMA", DisplayName = "Simple Moving Average", IndicatorType = IndicatorType.SMA };
    private static ScreenerCatalogItem MacdItem => new() { CategoryType = ScreenerItemCategoryType.Indicator, GroupName = "Momentum", ShortName = "MACD", DisplayName = "MACD", IndicatorType = IndicatorType.MACD };

    /// <summary>Mirrors the real <c>ScreenerCatalogProvider</c>'s "Price" group shape exactly
    /// (Y:\Temp\sa_improvement_plan_BacktestPriceSourceConditionFix.md): one row per <see cref="PriceType"/>,
    /// all sharing <see cref="IndicatorType.Price"/>, distinguished only by <see cref="ScreenerCatalogItem.ShortName"/>.</summary>
    private static ScreenerCatalogItem PriceHighItem => new() { CategoryType = ScreenerItemCategoryType.Indicator, GroupName = "Price", ShortName = "High", DisplayName = "High", IndicatorType = IndicatorType.Price };
    private static ScreenerCatalogItem PriceLowItem => new() { CategoryType = ScreenerItemCategoryType.Indicator, GroupName = "Price", ShortName = "Low", DisplayName = "Low", IndicatorType = IndicatorType.Price };

    private static BacktestIndicatorSelectionViewModel CreateViewModel(
        Func<IndicatorType, IReadOnlyList<string>>? outputSeriesNames = null,
        IIndicatorFactory? factory = null,
        FakeToastNotificationService? toastService = null)
    {
        var catalogProvider = new FakeScreenerCatalogProvider(new[] { SmaItem, MacdItem, PriceHighItem, PriceLowItem }, outputSeriesNames);
        return new BacktestIndicatorSelectionViewModel(catalogProvider, NullLocalizationService.Instance, toastService ?? new FakeToastNotificationService(), factory);
    }

    /// <summary>Every leaf comparison of the editor, in canonical root order and pre-order.</summary>
    private static List<BacktestConditionEntry> Leaves(BacktestIndicatorSelectionViewModel vm)
    {
        var leaves = new List<BacktestConditionEntry>();
        foreach (BacktestConditionGroupViewModel root in vm.ConditionTree.Roots) Collect(root, leaves);
        return leaves;

        static void Collect(BacktestConditionGroupViewModel group, List<BacktestConditionEntry> into)
        {
            foreach (BacktestConditionNodeViewModel child in group.Children)
            {
                if (child is BacktestConditionGroupViewModel nested) Collect(nested, into);
                else into.Add(((BacktestConditionLeafViewModel)child).Entry);
            }
        }
    }

    private static BacktestConditionEntry LeafOf(BacktestConditionGroupViewModel root)
        => ((BacktestConditionLeafViewModel)Assert.Single(root.Children)).Entry;

    private static void Press(BacktestIndicatorSelectionViewModel vm, BacktestConditionSection section)
    {
        switch (section)
        {
            case BacktestConditionSection.Entry: vm.AddEntryConditionCommand.Execute(null); break;
            case BacktestConditionSection.Exit: vm.AddExitConditionCommand.Execute(null); break;
            case BacktestConditionSection.Reverse: vm.AddBothConditionCommand.Execute(null); break;
        }
    }

    [Fact]
    public void CanAddCondition_False_UntilLeftTargetSelected()
    {
        var vm = CreateViewModel();
        Assert.False(vm.AddBothConditionCommand.CanExecute(null));
        Assert.False(vm.AddEntryConditionCommand.CanExecute(null));
        Assert.False(vm.AddExitConditionCommand.CanExecute(null));
    }

    [Fact]
    public void AddCondition_NumericMode_BuildsLeafFromLeftTargetAndNumericValue()
    {
        var vm = CreateViewModel();

        vm.SelectedCatalogItem = vm.FilteredItems.Single(i => i.ShortName == "SMA");
        Assert.True(vm.AddBothConditionCommand.CanExecute(null));

        vm.ConditionOperator = ComparisonOperator.LessThan;
        vm.ConditionRightNumericValue = 42m;
        vm.AddBothConditionCommand.Execute(null);

        // The "Both" (Reverse) button adds to the Reverse section; the untouched TargetPosition (Long) selects the Long root.
        BacktestConditionEntry entry = LeafOf(vm.ConditionTree.Root(BacktestConditionSection.Reverse, TradeSide.Long));
        Assert.Single(Leaves(vm));
        Assert.Equal(IndicatorType.SMA, entry.Left.IndicatorType);
        Assert.Equal(ComparisonOperator.LessThan, entry.Operator);
        Assert.Equal(RightHandTargetMode.NumericValue, entry.TargetMode);
        Assert.Equal(42m, entry.RightNumericValue);
        Assert.Null(entry.Right);
        // Inside a tree the location defines Role/Position/connector: the leaf keeps the model defaults, which the Core validation requires.
        var defaults = new BacktestConditionEntry();
        Assert.Equal(defaults.Role, entry.Role);
        Assert.Equal(defaults.Position, entry.Position);
        Assert.Equal(LogicalOperator.And, entry.LogicalOperator);
    }

    /// <summary>Round 2 (Y:\Temp\sa_improvement_plan_BacktestUiCleanupRound2.md Task 3): adding a
    /// condition must confirm itself via the same toast-notification mechanism Indicator Manager/Library's
    /// "Active" toggle already uses, not a bespoke one - proves the exact "{display} {Msg_Added}" shape
    /// and that ToastService is the one actually wired into the ViewModel (not a no-op stub).</summary>
    [Fact]
    public void AddCondition_ShowsToastNotification_SameShapeAsLibraryActiveFlow()
    {
        var toastService = new FakeToastNotificationService();
        var vm = CreateViewModel(toastService: toastService);
        vm.SelectedCatalogItem = vm.FilteredItems.Single(i => i.ShortName == "SMA");
        vm.ConditionOperator = ComparisonOperator.GreaterThan;
        vm.ConditionRightNumericValue = 10m;

        vm.AddEntryConditionCommand.Execute(null);

        Assert.True(toastService.IsNotificationVisible);
        Assert.Single(Leaves(vm));
        // Independently hardcoded (not derived from the converter under test): FakeScreenerCatalogProvider
        // returns null Parameters and no Output names here, so BacktestConditionEntryDisplayConverter's
        // "SMA (period)"/genuine-OutputName logic never engages - the display collapses to the bare
        // short name "SMA", and NullLocalizationService.GetString echoes its key verbatim ("Msg_Added").
        Assert.Equal("SMA > 10 Msg_Added", toastService.NotificationMessage);
    }

    [Theory]
    [InlineData(TradeSide.Long)]
    [InlineData(TradeSide.Short)]
    public void TargetPosition_SelectsTheSideRoot_RegardlessOfWhichButtonAdds(TradeSide position)
    {
        var vm = CreateViewModel();
        vm.SelectedCatalogItem = vm.FilteredItems.Single(i => i.ShortName == "SMA");
        vm.TargetPosition = position;

        vm.AddBothConditionCommand.Execute(null);

        Assert.Single(vm.ConditionTree.Root(BacktestConditionSection.Reverse, position).Children);
        Assert.Single(Leaves(vm));
    }

    [Fact]
    public void SetTargetPositionCommands_ToggleTargetPositionAndTheDisplayFlags()
    {
        var vm = CreateViewModel();
        Assert.Equal(TradeSide.Long, vm.TargetPosition);
        Assert.True(vm.IsTargetPositionLong);
        Assert.False(vm.IsTargetPositionShort);

        vm.SetTargetPositionShortCommand.Execute(null);
        Assert.Equal(TradeSide.Short, vm.TargetPosition);
        Assert.False(vm.IsTargetPositionLong);
        Assert.True(vm.IsTargetPositionShort);

        vm.SetTargetPositionLongCommand.Execute(null);
        Assert.Equal(TradeSide.Long, vm.TargetPosition);
        Assert.True(vm.IsTargetPositionLong);
        Assert.False(vm.IsTargetPositionShort);
    }

    [Theory]
    [InlineData(BacktestConditionSection.Entry)]
    [InlineData(BacktestConditionSection.Exit)]
    [InlineData(BacktestConditionSection.Reverse)]
    public void EachButton_AddsTheLeafToItsOwnSection(BacktestConditionSection expectedSection)
    {
        var vm = CreateViewModel();
        vm.SelectedCatalogItem = vm.FilteredItems.Single(i => i.ShortName == "SMA");

        Press(vm, expectedSection);

        Assert.Single(vm.ConditionTree.Root(expectedSection, TradeSide.Long).Children);
        Assert.Single(Leaves(vm));
    }

    /// <summary>D8: a selected group is the add target only when its Section AND Side both match the button/TargetPosition; otherwise the (Section, Side) root.</summary>
    [Theory]
    [InlineData(BacktestConditionSection.Entry, TradeSide.Long)]
    [InlineData(BacktestConditionSection.Entry, TradeSide.Short)]
    [InlineData(BacktestConditionSection.Exit, TradeSide.Long)]
    [InlineData(BacktestConditionSection.Exit, TradeSide.Short)]
    [InlineData(BacktestConditionSection.Reverse, TradeSide.Long)]
    [InlineData(BacktestConditionSection.Reverse, TradeSide.Short)]
    public void AddTarget_FollowsTheSelection_PerButtonAndSide(BacktestConditionSection section, TradeSide side)
    {
        BacktestConditionSection otherSection = section == BacktestConditionSection.Entry ? BacktestConditionSection.Exit : BacktestConditionSection.Entry;
        TradeSide otherSide = side == TradeSide.Long ? TradeSide.Short : TradeSide.Long;

        // nothing selected -> the root
        var vm = CreateViewModel();
        vm.SelectedCatalogItem = vm.FilteredItems.Single(i => i.ShortName == "SMA");
        vm.TargetPosition = side;
        Press(vm, section);
        BacktestConditionGroupViewModel root = vm.ConditionTree.Root(section, side);
        Assert.Single(root.Children);

        // matching group selected -> that group
        Assert.True(vm.ConditionTree.TryAddGroup(root, LogicalOperator.Or));
        var matching = (BacktestConditionGroupViewModel)root.Children[1];
        vm.ConditionTree.SelectedNode = matching;
        Press(vm, section);
        Assert.Single(matching.Children);
        Assert.Equal(2, root.Children.Count);

        // mismatching groups selected (other side, other section) -> the root
        foreach (BacktestConditionGroupViewModel mismatching in new[]
                 {
                     vm.ConditionTree.Root(section, otherSide),
                     vm.ConditionTree.Root(otherSection, side),
                 })
        {
            vm.ConditionTree.SelectedNode = mismatching;
            int before = root.Children.Count;
            Press(vm, section);
            Assert.Equal(before + 1, root.Children.Count);
            Assert.Empty(mismatching.Children);
        }
    }

    [Fact]
    public void AddCondition_WhenTheNodeLimitIsReached_AddsNothing_AndReportsTheLimit()
    {
        var toastService = new FakeToastNotificationService();
        var vm = CreateViewModel(toastService: toastService);
        vm.SelectedCatalogItem = vm.FilteredItems.Single(i => i.ShortName == "SMA");
        BacktestConditionGroupViewModel root = vm.ConditionTree.Root(BacktestConditionSection.Entry, TradeSide.Long);

        for (int i = 0; i < vm.ConditionTree.MaxNodes - 1; i++) vm.AddEntryConditionCommand.Execute(null);
        Assert.Equal(vm.ConditionTree.MaxNodes - 1, root.Children.Count);

        vm.AddEntryConditionCommand.Execute(null);

        Assert.Equal(vm.ConditionTree.MaxNodes - 1, root.Children.Count);
        Assert.Contains("Backtest_ConditionTree_LimitReached", toastService.NotificationMessage);
    }

    [Fact]
    public void CanAddCondition_False_WhenIndicatorModeAndRightTargetNotYetSelected()
    {
        var vm = CreateViewModel();
        vm.SelectedCatalogItem = vm.FilteredItems.Single(i => i.ShortName == "SMA");
        vm.SetConditionRightModeIndicatorCommand.Execute(null);

        Assert.False(vm.AddBothConditionCommand.CanExecute(null));
    }

    [Fact]
    public void AddCondition_IndicatorMode_BuildsBothSides()
    {
        var vm = CreateViewModel();

        // Left target: select SMA while Left is the active side (default).
        vm.SelectedCatalogItem = vm.FilteredItems.Single(i => i.ShortName == "SMA");

        // Switch to Indicator mode (focuses Right) and select MACD for the Right target.
        vm.SetConditionRightModeIndicatorCommand.Execute(null);
        vm.SelectedCatalogItem = vm.FilteredItems.Single(i => i.ShortName == "MACD");

        Assert.True(vm.AddEntryConditionCommand.CanExecute(null));
        vm.AddEntryConditionCommand.Execute(null);

        BacktestConditionEntry entry = LeafOf(vm.ConditionTree.Root(BacktestConditionSection.Entry, TradeSide.Long));
        Assert.Equal(IndicatorType.SMA, entry.Left.IndicatorType);
        Assert.Equal(RightHandTargetMode.Indicator, entry.TargetMode);
        Assert.NotNull(entry.Right);
        Assert.Equal(IndicatorType.MACD, entry.Right!.IndicatorType);
    }

    [Fact]
    public void SelectingMultiSeriesIndicator_PopulatesOutputPicker_AndNonMainSelectionFlowsIntoConditionSide()
    {
        var vm = CreateViewModel(type => type == IndicatorType.MACD ? new[] { "Main", "Signal" } : new[] { "Main" });

        vm.SelectedCatalogItem = vm.FilteredItems.Single(i => i.ShortName == "MACD");

        Assert.True(vm.HasMultipleLeftConditionOutputs);
        Assert.Equal(new[] { "Main", "Signal" }, vm.LeftConditionAvailableOutputs);

        vm.LeftConditionSelectedOutput = "Signal";
        vm.AddBothConditionCommand.Execute(null);

        BacktestConditionEntry entry = Assert.Single(Leaves(vm));
        Assert.Equal("Signal", entry.Left.OutputName);
    }

    [Fact]
    public void SelectingSingleSeriesIndicator_DoesNotOfferOutputPicker()
    {
        var vm = CreateViewModel(_ => new[] { "Main" });

        vm.SelectedCatalogItem = vm.FilteredItems.Single(i => i.ShortName == "SMA");

        Assert.False(vm.HasMultipleLeftConditionOutputs);
        Assert.Equal(IndicatorResult.MainSeriesName, vm.LeftConditionSelectedOutput);
    }

    [Fact]
    public void AddCondition_UnsupportedIndicator_Rejected_NotAddedToTheTree()
    {
        var factory = new FakeIndicatorFactory(Array.Empty<IndicatorType>());
        var vm = CreateViewModel(factory: factory);

        vm.SelectedCatalogItem = vm.FilteredItems.Single(i => i.ShortName == "SMA");
        vm.AddBothConditionCommand.Execute(null);

        Assert.Empty(Leaves(vm));
        Assert.Equal("Backtest_UnsupportedIndicatorMessage", vm.UnsupportedWarningMessage);
    }

    [Fact]
    public void TreeToDto_ThenBackToTree_RoundTripsEveryField()
    {
        var vm = CreateViewModel(type => type == IndicatorType.MACD ? new[] { "Main", "Signal" } : new[] { "Main" });

        vm.SelectedCatalogItem = vm.FilteredItems.Single(i => i.ShortName == "MACD");
        vm.LeftConditionSelectedOutput = "Signal";
        vm.LeftConditionOffset = 2;
        vm.LeftConditionFrame = vm.AvailableConditionFrames.Single(f => f.Value == TimeFrame.W1);
        vm.ConditionOperator = ComparisonOperator.GreaterThanOrEqual;
        vm.ConditionRightNumericValue = 7.5m;
        vm.TargetPosition = TradeSide.Short;
        vm.AddExitConditionCommand.Execute(null);

        BacktestConditionTreeDto dto = BacktestConditionTreeDtoMapper.ToDto(vm.ConditionTree.Build());
        BacktestConditionEntryDto leaf = Assert.Single(dto.ExitShort.Children).Comparison!;
        Assert.Equal(IndicatorType.MACD, leaf.Left.IndicatorType);
        Assert.Equal("Signal", leaf.Left.OutputName);
        Assert.Equal(2, leaf.Left.Offset);
        Assert.Equal(TimeFrame.W1, leaf.Left.Frame);
        Assert.Equal(ComparisonOperator.GreaterThanOrEqual, leaf.Operator);
        Assert.Equal(7.5m, leaf.RightNumericValue);

        var other = CreateViewModel();
        other.ConditionTree.Load(BacktestConditionTreeDtoMapper.ToTree(dto));

        BacktestConditionEntry restored = LeafOf(other.ConditionTree.Root(BacktestConditionSection.Exit, TradeSide.Short));
        Assert.Single(Leaves(other));
        Assert.Equal(leaf.Left.IndicatorType, restored.Left.IndicatorType);
        Assert.Equal(leaf.Left.OutputName, restored.Left.OutputName);
        Assert.Equal(leaf.Left.Offset, restored.Left.Offset);
        Assert.Equal(leaf.Left.Frame, restored.Left.Frame);
        Assert.Equal(leaf.Operator, restored.Operator);
        Assert.Equal(leaf.RightNumericValue, restored.RightNumericValue);
    }

    [Fact]
    public void AddCondition_PriceSourceRows_CaptureDistinctPriceSourcePerSide()
    {
        // Bug fix (Y:\Temp\sa_improvement_plan_BacktestPriceSourceConditionFix.md): before this
        // fix, selecting "High" vs "Low" from the catalog's Price group made no difference at all - both
        // silently bound to the same generic default settings (PriceSource always Close). This proves the
        // two rows now correctly capture their own distinct PriceType.
        var vm = CreateViewModel();

        vm.SelectedCatalogItem = vm.FilteredItems.Single(i => i.ShortName == "High");
        vm.SetConditionRightModeIndicatorCommand.Execute(null);
        vm.SelectedCatalogItem = vm.FilteredItems.Single(i => i.ShortName == "Low");
        vm.AddEntryConditionCommand.Execute(null);

        BacktestConditionEntry entry = Assert.Single(Leaves(vm));
        Assert.Equal(IndicatorType.Price, entry.Left.IndicatorType);
        Assert.Equal(PriceType.High, entry.Left.PriceSource);
        Assert.NotNull(entry.Right);
        Assert.Equal(IndicatorType.Price, entry.Right!.IndicatorType);
        Assert.Equal(PriceType.Low, entry.Right.PriceSource);
    }

    [Fact]
    public void TreeToDto_ThenBackToTree_RoundTripsPriceSource()
    {
        var vm = CreateViewModel();
        vm.SelectedCatalogItem = vm.FilteredItems.Single(i => i.ShortName == "High");
        vm.AddEntryConditionCommand.Execute(null);

        BacktestConditionTreeDto dto = BacktestConditionTreeDtoMapper.ToDto(vm.ConditionTree.Build());
        Assert.Equal(PriceType.High, Assert.Single(dto.EntryLong.Children).Comparison!.Left.PriceSource);

        vm.ConditionTree.Load(BacktestConditionTreeDtoMapper.ToTree(dto));
        Assert.Equal(PriceType.High, Assert.Single(Leaves(vm)).Left.PriceSource);
    }

    [Fact]
    public void ExistingTrackFlow_StillWorks_AlongsideNewConditionBuilder()
    {
        // Regression guard: the pre-existing single-target "track this indicator" flow
        // (BacktestIndicatorSelectionTests, spec §5.3/§9.6) must keep working exactly as before,
        // since Task 6 added the condition builder alongside it rather than replacing it.
        var vm = CreateViewModel();

        vm.SelectedCatalogItem = vm.FilteredItems.Single(i => i.ShortName == "SMA");
        vm.AddSelectedIndicatorCommand.Execute(null);

        Assert.Single(vm.AddedIndicators);
        Assert.Empty(Leaves(vm));
    }
}
