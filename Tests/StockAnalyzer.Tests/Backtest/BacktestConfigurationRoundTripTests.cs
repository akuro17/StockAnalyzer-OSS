using System;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>
/// Task 17 acceptance tests for BacktestWindowViewModel's Save path (spec §5.5; the Restore command was removed in MP-D) using an
/// in-memory <see cref="FakeBacktestConfigurationManager"/> - the real <c>BacktestConfigurationManager</c>
/// resolves a fixed path under the user's actual Data/Config folder, which a unit test must never touch.
/// </summary>
public class BacktestConfigurationRoundTripTests
{
    [Fact]
    public async Task Save_WritesEveryEditedField()
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

        BacktestConfigurationDto saved = Assert.IsType<BacktestConfigurationDto>(configManager.Saved);
        Assert.Equal("AAPL", saved.Symbol);
        Assert.Equal(ExecutionModel.StrictEvidence, saved.ExecutionModel);
        Assert.Equal(TimeFrame.H4, saved.Frame);
        Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), saved.EvaluationStartUtc);
        Assert.Equal(new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc), saved.EvaluationEndUtc);
        Assert.Equal(500_000m, saved.InitialCapital);
        Assert.Equal(1.5m, saved.CommissionFlat);
        Assert.Equal(0.02m, saved.CommissionPerUnit);
        Assert.Equal(0.001m, saved.SlippageRatio);
        Assert.Equal(PositionSizingModel.PercentOfEquity, saved.SizingModel);
        Assert.Equal(0.5m, saved.SizingParameter);
        Assert.Equal(0.4m, saved.InitialMarginRatio);
        Assert.Equal(0.25m, saved.MaintenanceMarginRatio);
        Assert.Equal(0.01m, saved.LiquidationPenaltyRatio);
        Assert.Equal(7, saved.BootstrapSeed);
        Assert.Equal(3000, saved.BootstrapIterations);
        Assert.Equal(IndicatorType.SMA, Assert.Single(saved.SelectedIndicators).Type);
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

    /// <summary>
    /// sa_implementation_plan_BacktestP3Hardening.md Task 2: an explicit Reset to Defaults recovery command,
    /// exercises the same ApplyBuiltInDefaults() the constructor uses, as a directly user-invokable command.
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
