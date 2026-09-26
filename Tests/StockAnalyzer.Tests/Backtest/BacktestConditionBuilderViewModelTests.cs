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
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>
/// Task 6 (Y:\Temp\sa_implementation_plan_BacktestComparisonSignals.md section 4.4, amended by
/// Y:\Temp\sa_implementation_plan_BacktestOutputNameSupport.md): the right-column condition builder
/// added to <see cref="BacktestIndicatorSelectionViewModel"/> - Left/Right Focus-Bind target state,
/// AddCondition/RemoveConditionEntry, the Output picker (Task 6a's engine wiring made this reachable),
/// and the owed ConditionEntries Save/Restore DTO mapping Task 5 deliberately left unwired.
/// </summary>
public class BacktestConditionBuilderViewModelTests
{
    private static ScreenerCatalogItem SmaItem => new() { CategoryType = ScreenerItemCategoryType.Indicator, GroupName = "MA", ShortName = "SMA", DisplayName = "Simple Moving Average", IndicatorType = IndicatorType.SMA };
    private static ScreenerCatalogItem MacdItem => new() { CategoryType = ScreenerItemCategoryType.Indicator, GroupName = "Momentum", ShortName = "MACD", DisplayName = "MACD", IndicatorType = IndicatorType.MACD };

    /// <summary>Mirrors the real <c>ScreenerCatalogProvider</c>'s "Price" group shape exactly (SAで改善,
    /// Y:\Temp\sa_improvement_plan_BacktestPriceSourceConditionFix.md): one row per <see cref="PriceType"/>,
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

    [Fact]
    public void CanAddCondition_False_UntilLeftTargetSelected()
    {
        var vm = CreateViewModel();
        Assert.False(vm.AddBothConditionCommand.CanExecute(null));
        Assert.False(vm.AddEntryConditionCommand.CanExecute(null));
        Assert.False(vm.AddExitConditionCommand.CanExecute(null));
    }

    [Fact]
    public void AddCondition_NumericMode_BuildsEntryFromLeftTargetAndNumericValue()
    {
        var vm = CreateViewModel();

        vm.SelectedCatalogItem = vm.FilteredItems.Single(i => i.ShortName == "SMA");
        Assert.True(vm.AddBothConditionCommand.CanExecute(null));

        vm.ConditionOperator = ComparisonOperator.LessThan;
        vm.ConditionRightNumericValue = 42m;
        vm.AddBothConditionCommand.Execute(null);

        BacktestConditionEntry entry = Assert.Single(vm.ConditionEntries);
        Assert.Equal(IndicatorType.SMA, entry.Left.IndicatorType);
        Assert.Equal(ComparisonOperator.LessThan, entry.Operator);
        Assert.Equal(RightHandTargetMode.NumericValue, entry.TargetMode);
        Assert.Equal(42m, entry.RightNumericValue);
        Assert.Null(entry.Right);
        // The Both button commits Role.Reversal, not Role.Both (SAで実装 2026-09-19 correction, plan
        // section 2.2): Both's own side-agnostic meaning is reserved for pre-existing saved entries only.
        Assert.Equal(BacktestConditionRole.Reversal, entry.Role);
        // AND/OR picker was removed (SAで改善 2026-09-18) - every new entry uses the model's own default.
        Assert.Equal(LogicalOperator.And, entry.LogicalOperator);
        // Position defaults to Long (SAで実装 2026-09-19, plan section 2.1) - an untouched TargetPosition
        // selector must keep producing today's only-ever-possible entry signal (LongEntry).
        Assert.Equal(TradeSide.Long, entry.Position);
    }

    /// <summary>SAで改善 (Round 2, Y:\Temp\sa_improvement_plan_BacktestUiCleanupRound2.md Task 3): adding a
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
        Assert.Single(vm.ConditionEntries);
        // Independently hardcoded (not derived from the converter under test): FakeScreenerCatalogProvider
        // returns null Parameters and no Output names here, so BacktestConditionEntryDisplayConverter's
        // "SMA (period)"/genuine-OutputName logic never engages - the display collapses to the bare
        // short name "SMA", and NullLocalizationService.GetString echoes its key verbatim ("Msg_Added").
        Assert.Equal("SMA > 10 Msg_Added", toastService.NotificationMessage);
    }

    [Theory]
    [InlineData(TradeSide.Long)]
    [InlineData(TradeSide.Short)]
    public void TargetPosition_FlowsIntoNewEntry_RegardlessOfWhichRoleButtonCommits(TradeSide position)
    {
        var vm = CreateViewModel();
        vm.SelectedCatalogItem = vm.FilteredItems.Single(i => i.ShortName == "SMA");
        vm.TargetPosition = position;

        vm.AddBothConditionCommand.Execute(null);

        BacktestConditionEntry entry = Assert.Single(vm.ConditionEntries);
        Assert.Equal(position, entry.Position);
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
    [InlineData(BacktestConditionRole.EntryOnly)]
    [InlineData(BacktestConditionRole.ExitOnly)]
    [InlineData(BacktestConditionRole.Reversal)] // the Both button commits Reversal, not Both - see above
    public void EachRoleButton_CommitsEntryWithItsOwnFixedRole(BacktestConditionRole expectedRole)
    {
        var vm = CreateViewModel();
        vm.SelectedCatalogItem = vm.FilteredItems.Single(i => i.ShortName == "SMA");

        switch (expectedRole)
        {
            case BacktestConditionRole.EntryOnly: vm.AddEntryConditionCommand.Execute(null); break;
            case BacktestConditionRole.ExitOnly: vm.AddExitConditionCommand.Execute(null); break;
            case BacktestConditionRole.Reversal: vm.AddBothConditionCommand.Execute(null); break;
        }

        BacktestConditionEntry entry = Assert.Single(vm.ConditionEntries);
        Assert.Equal(expectedRole, entry.Role);
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

        BacktestConditionEntry entry = Assert.Single(vm.ConditionEntries);
        Assert.Equal(IndicatorType.SMA, entry.Left.IndicatorType);
        Assert.Equal(RightHandTargetMode.Indicator, entry.TargetMode);
        Assert.NotNull(entry.Right);
        Assert.Equal(IndicatorType.MACD, entry.Right!.IndicatorType);
        Assert.Equal(BacktestConditionRole.EntryOnly, entry.Role);
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

        BacktestConditionEntry entry = Assert.Single(vm.ConditionEntries);
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
    public void AddCondition_UnsupportedIndicator_Rejected_NotAddedToConditionEntries()
    {
        var factory = new FakeIndicatorFactory(Array.Empty<IndicatorType>());
        var vm = CreateViewModel(factory: factory);

        vm.SelectedCatalogItem = vm.FilteredItems.Single(i => i.ShortName == "SMA");
        vm.AddBothConditionCommand.Execute(null);

        Assert.Empty(vm.ConditionEntries);
        Assert.Equal("Backtest_UnsupportedIndicatorMessage", vm.UnsupportedWarningMessage);
    }

    [Fact]
    public void RemoveConditionEntry_RemovesFromCollection()
    {
        var vm = CreateViewModel();
        vm.SelectedCatalogItem = vm.FilteredItems.Single(i => i.ShortName == "SMA");
        vm.AddBothConditionCommand.Execute(null);
        BacktestConditionEntry entry = vm.ConditionEntries.Single();

        vm.RemoveConditionEntryCommand.Execute(entry);

        Assert.Empty(vm.ConditionEntries);
    }

    [Fact]
    public void BuildConditionEntryDtos_ThenReplaceConditionEntries_RoundTripsEveryField()
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

        List<BacktestConditionEntryDto> dtos = vm.BuildConditionEntryDtos();
        BacktestConditionEntryDto dto = Assert.Single(dtos);
        Assert.Equal(IndicatorType.MACD, dto.Left.IndicatorType);
        Assert.Equal("Signal", dto.Left.OutputName);
        Assert.Equal(2, dto.Left.Offset);
        Assert.Equal(TimeFrame.W1, dto.Left.Frame);
        Assert.Equal(ComparisonOperator.GreaterThanOrEqual, dto.Operator);
        Assert.Equal(BacktestConditionRole.ExitOnly, dto.Role);
        Assert.Equal(7.5m, dto.RightNumericValue);
        Assert.Equal(TradeSide.Short, dto.Position);

        vm.ReplaceConditionEntries(dtos);

        BacktestConditionEntry restored = Assert.Single(vm.ConditionEntries);
        Assert.Equal(dto.Left.IndicatorType, restored.Left.IndicatorType);
        Assert.Equal(dto.Left.OutputName, restored.Left.OutputName);
        Assert.Equal(dto.Left.Offset, restored.Left.Offset);
        Assert.Equal(dto.Left.Frame, restored.Left.Frame);
        Assert.Equal(dto.Operator, restored.Operator);
        Assert.Equal(dto.Role, restored.Role);
        Assert.Equal(dto.RightNumericValue, restored.RightNumericValue);
        Assert.Equal(dto.Position, restored.Position);
    }

    [Fact]
    public void AddCondition_PriceSourceRows_CaptureDistinctPriceSourcePerSide()
    {
        // SAで改善 bug fix (Y:\Temp\sa_improvement_plan_BacktestPriceSourceConditionFix.md): before this
        // fix, selecting "High" vs "Low" from the catalog's Price group made no difference at all - both
        // silently bound to the same generic default settings (PriceSource always Close). This proves the
        // two rows now correctly capture their own distinct PriceType.
        var vm = CreateViewModel();

        vm.SelectedCatalogItem = vm.FilteredItems.Single(i => i.ShortName == "High");
        vm.SetConditionRightModeIndicatorCommand.Execute(null);
        vm.SelectedCatalogItem = vm.FilteredItems.Single(i => i.ShortName == "Low");
        vm.AddEntryConditionCommand.Execute(null);

        BacktestConditionEntry entry = Assert.Single(vm.ConditionEntries);
        Assert.Equal(IndicatorType.Price, entry.Left.IndicatorType);
        Assert.Equal(PriceType.High, entry.Left.PriceSource);
        Assert.NotNull(entry.Right);
        Assert.Equal(IndicatorType.Price, entry.Right!.IndicatorType);
        Assert.Equal(PriceType.Low, entry.Right.PriceSource);
    }

    [Fact]
    public void BuildConditionEntryDtos_ThenReplaceConditionEntries_RoundTripsPriceSource()
    {
        var vm = CreateViewModel();
        vm.SelectedCatalogItem = vm.FilteredItems.Single(i => i.ShortName == "High");
        vm.AddEntryConditionCommand.Execute(null);

        List<BacktestConditionEntryDto> dtos = vm.BuildConditionEntryDtos();
        Assert.Equal(PriceType.High, Assert.Single(dtos).Left.PriceSource);

        vm.ReplaceConditionEntries(dtos);
        Assert.Equal(PriceType.High, Assert.Single(vm.ConditionEntries).Left.PriceSource);
    }

    /// <summary>SAで改善 (Y:\Temp\sa_improvement_plan_BacktestResultsUiPolish.md Task 4): the Results tab's
    /// Entry/Exit/Both 3-column split reads these three buckets instead of filtering ConditionEntries
    /// itself in the View (which cannot react live to ObservableCollection content changes). Proves they
    /// stay in sync as entries are added, and that Role.Reversal (the Both button's actual emitted role)
    /// lands in the Both bucket alongside Role.Both.</summary>
    [Fact]
    public void ConditionEntryBuckets_StaySyncedWithConditionEntries_AsRowsAreAdded()
    {
        var vm = CreateViewModel();
        vm.SelectedCatalogItem = vm.FilteredItems.Single(i => i.ShortName == "SMA");

        vm.AddEntryConditionCommand.Execute(null);
        vm.AddExitConditionCommand.Execute(null);
        vm.AddBothConditionCommand.Execute(null); // commits Role.Reversal, not Role.Both

        Assert.Equal(3, vm.ConditionEntries.Count);
        BacktestConditionEntry entryBucketEntry = Assert.Single(vm.EntryConditionEntries);
        Assert.Equal(BacktestConditionRole.EntryOnly, entryBucketEntry.Role);
        BacktestConditionEntry exitBucketEntry = Assert.Single(vm.ExitConditionEntries);
        Assert.Equal(BacktestConditionRole.ExitOnly, exitBucketEntry.Role);
        BacktestConditionEntry bothBucketEntry = Assert.Single(vm.BothConditionEntries);
        Assert.Equal(BacktestConditionRole.Reversal, bothBucketEntry.Role);
    }

    [Fact]
    public void ConditionEntryBuckets_StaySyncedWithConditionEntries_AfterRemoveAndRestore()
    {
        var vm = CreateViewModel();
        vm.SelectedCatalogItem = vm.FilteredItems.Single(i => i.ShortName == "SMA");
        vm.AddEntryConditionCommand.Execute(null);
        BacktestConditionEntry added = Assert.Single(vm.ConditionEntries);

        vm.RemoveConditionEntryCommand.Execute(added);
        Assert.Empty(vm.ConditionEntries);
        Assert.Empty(vm.EntryConditionEntries);
        Assert.Empty(vm.ExitConditionEntries);
        Assert.Empty(vm.BothConditionEntries);

        vm.ReplaceConditionEntries(new List<BacktestConditionEntryDto>
        {
            new() { Left = new BacktestConditionSideDto { IndicatorType = IndicatorType.SMA }, Role = BacktestConditionRole.Both },
        });
        Assert.Empty(vm.EntryConditionEntries);
        Assert.Empty(vm.ExitConditionEntries);
        Assert.Equal(BacktestConditionRole.Both, Assert.Single(vm.BothConditionEntries).Role);
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
        Assert.Empty(vm.ConditionEntries);
    }
}
