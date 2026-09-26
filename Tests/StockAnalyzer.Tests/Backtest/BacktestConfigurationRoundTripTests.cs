using System;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>
/// Task 17 acceptance tests for BacktestWindowViewModel's Save/Restore round trip (spec §5.5) using an
/// in-memory <see cref="FakeBacktestConfigurationManager"/> - the real <c>BacktestConfigurationManager</c>
/// resolves a fixed path under the user's actual Data/Config folder, which a unit test must never touch.
/// </summary>
public class BacktestConfigurationRoundTripTests
{
    [Fact]
    public async Task Save_Modify_Restore_AllParametersMatch()
    {
        var configManager = new FakeBacktestConfigurationManager();
        var catalog = new FakeScreenerCatalogProvider(new[]
        {
            new ScreenerCatalogItem { CategoryType = ScreenerItemCategoryType.Indicator, GroupName = "MA", ShortName = "SMA", DisplayName = "Simple Moving Average", IndicatorType = IndicatorType.SMA },
        });
        BacktestWindowViewModel vm = BacktestTestFactory.CreateViewModel(configurationManager: configManager, catalogProvider: catalog);

        vm.Symbol = "AAPL";
        vm.ExecutionModel = ExecutionModel.StrictEvidence;
        vm.Frame = TimeFrame.H4;
        vm.EvaluationStartUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        vm.EvaluationEndUtc = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        vm.InitialCapital = 500_000m;
        vm.CommissionFlat = 1.5m;
        vm.CommissionPerUnit = 0.02m;
        vm.SlippageRatio = 0.001m;
        vm.SizingModel = PositionSizingModel.PercentOfEquity;
        vm.SizingParameter = 0.5m;
        vm.InitialMarginRatio = 0.4m;
        vm.MaintenanceMarginRatio = 0.25m;
        vm.LiquidationPenaltyRatio = 0.01m;
        vm.BootstrapSeed = 7;
        vm.BootstrapIterations = 3000;
        vm.AnnualRiskFreeRate = 0.02m;
        vm.AnnualMar = 0.01m;
        vm.AnnualPeriodsByFrame.Clear();
        vm.AnnualPeriodsByFrame.Add(new AnnualPeriodEntry(TimeFrame.H4, 300));
        vm.IndicatorSelection.SelectedCatalogItem = vm.IndicatorSelection.FilteredItems.Single();
        vm.IndicatorSelection.AddSelectedIndicatorCommand.Execute(null);
        Assert.Single(vm.IndicatorSelection.AddedIndicators);

        await vm.SaveConfigurationCommand.ExecuteAsync(null);
        Assert.Equal("Backtest_Config_SaveSuccess", vm.StatusMessage);

        // Modify every saved field to a different value.
        vm.Symbol = "MSFT";
        vm.ExecutionModel = ExecutionModel.Legacy;
        vm.Frame = TimeFrame.D1;
        vm.EvaluationStartUtc = new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        vm.EvaluationEndUtc = new DateTime(2019, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        vm.InitialCapital = 10_000m;
        vm.CommissionFlat = 0m;
        vm.CommissionPerUnit = 0m;
        vm.SlippageRatio = 0m;
        vm.SizingModel = PositionSizingModel.FixedQuantity;
        vm.SizingParameter = 3m;
        vm.InitialMarginRatio = 0.9m;
        vm.MaintenanceMarginRatio = 0.1m;
        vm.LiquidationPenaltyRatio = 0m;
        vm.BootstrapSeed = 1;
        vm.BootstrapIterations = 1000;
        vm.AnnualRiskFreeRate = 0.20m;
        vm.AnnualMar = 0.10m;
        vm.AnnualPeriodsByFrame.Clear();
        vm.AnnualPeriodsByFrame.Add(new AnnualPeriodEntry(TimeFrame.H4, 200));
        vm.IndicatorSelection.RemoveIndicatorCommand.Execute(vm.IndicatorSelection.AddedIndicators.Single());
        Assert.Empty(vm.IndicatorSelection.AddedIndicators);

        await vm.RestoreConfigurationCommand.ExecuteAsync(null);

        Assert.Equal("Backtest_Config_RestoreSuccess", vm.StatusMessage);
        Assert.Equal("AAPL", vm.Symbol);
        Assert.Equal(ExecutionModel.StrictEvidence, vm.ExecutionModel);
        Assert.Equal(TimeFrame.H4, vm.Frame);
        Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), vm.EvaluationStartUtc);
        Assert.Equal(new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc), vm.EvaluationEndUtc);
        Assert.Equal(500_000m, vm.InitialCapital);
        Assert.Equal(1.5m, vm.CommissionFlat);
        Assert.Equal(0.02m, vm.CommissionPerUnit);
        Assert.Equal(0.001m, vm.SlippageRatio);
        Assert.Equal(PositionSizingModel.PercentOfEquity, vm.SizingModel);
        Assert.Equal(0.5m, vm.SizingParameter);
        Assert.Equal(0.4m, vm.InitialMarginRatio);
        Assert.Equal(0.25m, vm.MaintenanceMarginRatio);
        Assert.Equal(0.01m, vm.LiquidationPenaltyRatio);
        Assert.Equal(7, vm.BootstrapSeed);
        Assert.Equal(3000, vm.BootstrapIterations);
        Assert.Equal(0.02m, vm.AnnualRiskFreeRate);
        Assert.Equal(0.01m, vm.AnnualMar);
        Assert.Equal(300, Assert.Single(vm.AnnualPeriodsByFrame).Periods);

        BacktestIndicatorSelectionItem restoredIndicator = Assert.Single(vm.IndicatorSelection.AddedIndicators);
        Assert.Equal(IndicatorType.SMA, restoredIndicator.Type);
    }

    [Fact]
    public async Task SecondSettingsSaveFailure_IsReportedAsPartialSuccess()
    {
        var configManager = new FakeBacktestConfigurationManager();
        var reportManager = new FakeBacktestReportSettingsManager
        {
            SaveFailure = new InvalidOperationException("report save failed"),
        };
        BacktestWindowViewModel vm = BacktestTestFactory.CreateViewModel(
            configurationManager: configManager,
            reportSettingsManager: reportManager);
        vm.Symbol = "TEST";

        await vm.SaveConfigurationCommand.ExecuteAsync(null);

        Assert.Equal(1, configManager.SaveCallCount);
        Assert.NotNull(configManager.Saved);
        Assert.Equal("Backtest_Config_SavePartialError", vm.StatusMessage);
    }

    [Fact]
    public async Task Save_NormalizesUnspecifiedPickerDatesToUtc()
    {
        var configManager = new FakeBacktestConfigurationManager();
        BacktestWindowViewModel vm = BacktestTestFactory.CreateViewModel(configurationManager: configManager);
        vm.EvaluationStartUtc = new DateTime(2020, 2, 3, 15, 45, 0, DateTimeKind.Unspecified);
        vm.EvaluationEndUtc = new DateTime(2020, 2, 4, 18, 30, 0, DateTimeKind.Unspecified);

        await vm.SaveConfigurationCommand.ExecuteAsync(null);

        Assert.NotNull(configManager.Saved);
        Assert.Equal(new DateTime(2020, 2, 3, 0, 0, 0, DateTimeKind.Utc), configManager.Saved!.EvaluationStartUtc);
        Assert.Equal(new DateTime(2020, 2, 4, 0, 0, 0, DateTimeKind.Utc), configManager.Saved.EvaluationEndUtc);
    }

    [Fact]
    public async Task CorruptJson_ErrorShown_OriginalMaintained()
    {
        var configManager = new FakeBacktestConfigurationManager
        {
            LoadOverride = () => Task.FromResult(BacktestConfigurationLoadResult.Error("backtest_configuration.json is corrupt (invalid JSON)."))
        };
        BacktestWindowViewModel vm = BacktestTestFactory.CreateViewModel(configurationManager: configManager);
        vm.Symbol = "ORIGINAL";
        vm.InitialCapital = 12345m;

        await vm.RestoreConfigurationCommand.ExecuteAsync(null);

        Assert.Equal("Backtest_Config_LoadError", vm.StatusMessage);
        Assert.Equal("ORIGINAL", vm.Symbol);
        Assert.Equal(12345m, vm.InitialCapital);
    }

    [Fact]
    public async Task ReportSettingsFallback_IsExplicitlyReported()
    {
        var reportManager = new FakeBacktestReportSettingsManager { UseBuiltInFallbackStatus = true };
        BacktestWindowViewModel vm = BacktestTestFactory.CreateViewModel(reportSettingsManager: reportManager);

        await vm.RestoreConfigurationCommand.ExecuteAsync(null);

        Assert.Equal("Backtest_Config_RestoreReportFallback", vm.StatusMessage);
        Assert.Equal(BacktestReportDefaults.BuiltIn.AnnualRiskFreeRate, vm.AnnualRiskFreeRate);
    }

    [Fact]
    public async Task RestoreIoFailure_IsShownAndLeavesUiUntouched()
    {
        var configManager = new FakeBacktestConfigurationManager
        {
            LoadOverride = () => Task.FromException<BacktestConfigurationLoadResult>(new UnauthorizedAccessException("denied")),
        };
        BacktestWindowViewModel vm = BacktestTestFactory.CreateViewModel(configurationManager: configManager);
        vm.Symbol = "UNCHANGED";

        await vm.RestoreConfigurationCommand.ExecuteAsync(null);

        Assert.Equal("Backtest_Config_LoadError", vm.StatusMessage);
        Assert.Equal("UNCHANGED", vm.Symbol);
    }

    [Fact]
    public async Task UnknownSchemaVersion_ErrorShown()
    {
        var configManager = new FakeBacktestConfigurationManager
        {
            LoadOverride = () => Task.FromResult(BacktestConfigurationLoadResult.Error("Unrecognized SchemaVersion 2 (expected 1)."))
        };
        BacktestWindowViewModel vm = BacktestTestFactory.CreateViewModel(configurationManager: configManager);
        vm.Symbol = "ORIGINAL2";

        await vm.RestoreConfigurationCommand.ExecuteAsync(null);

        Assert.Equal("Backtest_Config_LoadError", vm.StatusMessage);
        Assert.Equal("ORIGINAL2", vm.Symbol);
    }

    /// <summary>
    /// sa_implementation_plan_BacktestP3Hardening.md Task 2: an explicit Reset to Defaults recovery command,
    /// distinct from Restore's own "no file yet" default-application path - this exercises the same
    /// ApplyBuiltInDefaults() reused as a directly user-invokable command, including after a corrupt-file
    /// Restore has already left the UI's edited-away-from-default fields untouched (as in
    /// <see cref="CorruptJson_ErrorShown_OriginalMaintained"/> above).
    /// </summary>
    [Fact]
    public void ResetToDefaults_RevertsEditedFieldsAndClearsIndicators()
    {
        var catalog = new FakeScreenerCatalogProvider(new[]
        {
            new ScreenerCatalogItem { CategoryType = ScreenerItemCategoryType.Indicator, GroupName = "MA", ShortName = "SMA", DisplayName = "Simple Moving Average", IndicatorType = IndicatorType.SMA },
        });
        BacktestWindowViewModel vm = BacktestTestFactory.CreateViewModel(catalogProvider: catalog);

        vm.Symbol = "MSFT";
        vm.Frame = TimeFrame.W1;
        vm.InitialCapital = 10_000m;
        vm.InitialMarginRatio = 0.9m;
        vm.BootstrapSeed = 1;
        vm.IndicatorSelection.SelectedCatalogItem = vm.IndicatorSelection.FilteredItems.Single();
        vm.IndicatorSelection.AddSelectedIndicatorCommand.Execute(null);
        Assert.Single(vm.IndicatorSelection.AddedIndicators);

        Assert.True(vm.ResetToDefaultsCommand.CanExecute(null));
        vm.ResetToDefaultsCommand.Execute(null);

        Assert.Equal("Backtest_Config_ResetToDefaults", vm.StatusMessage);
        Assert.Equal(string.Empty, vm.Symbol);
        Assert.Equal(TimeFrame.D1, vm.Frame);
        Assert.Equal(1_000_000m, vm.InitialCapital);
        Assert.Equal(0.30m, vm.InitialMarginRatio);
        Assert.Equal(42, vm.BootstrapSeed);
        Assert.Empty(vm.IndicatorSelection.AddedIndicators);
    }
}
