using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>
/// Settings > Backtest defaults consumed by the Backtest window: no Settings file must leave the window
/// identical to the previous built-in defaults; a Settings file supplies the new defaults for open,
/// Reset to Defaults and no-file Restore; a saved report-settings file still wins over them.
/// </summary>
public class BacktestDefaultSettingsWindowTests
{
    private static string NewTempPath() => Path.Combine(Path.GetTempPath(), $"backtest-window-defaults-{Guid.NewGuid():N}.json");

    private static BacktestDefaultSettings Custom() => new()
    {
        SchemaVersion = BacktestDefaultSettings.CurrentSchemaVersion,
        InitialCapital = 250_000m,
        CommissionFlat = 1.5m,
        CommissionPerUnit = 0.01m,
        SlippageRatio = 0.002m,
        SizingModel = PositionSizingModel.PercentOfEquity,
        SizingParameter = 0.5m,
        InitialMarginRatio = 0.4m,
        MaintenanceMarginRatio = 0.25m,
        LiquidationPenaltyRatio = 0.01m,
        AnnualRiskFreeRate = 0.03m,
        AnnualMAR = 0.02m,
        AnnualPeriodsByFrame = { [TimeFrame.D1] = 260, [TimeFrame.W1] = 50, [TimeFrame.MN1] = 12 },
        BootstrapSeed = 7,
        BootstrapIterations = 5000,
    };

    private static async Task<(BacktestWindowViewModel Vm, string DefaultsPath)> CreateWithSettingsAsync(BacktestDefaultSettings? saved)
    {
        string defaultsPath = NewTempPath();
        var defaultsManager = new BacktestDefaultSettingsManager(defaultsPath);
        if (saved is not null) await defaultsManager.SaveAsync(saved);

        // A real report-settings manager pointed at a missing file reports BuiltInFallback.
        var reportSettings = new BacktestReportSettingsManager(NewTempPath());
        BacktestWindowViewModel vm = BacktestTestFactory.CreateViewModel(
            reportSettingsManager: reportSettings,
            defaultSettingsManager: defaultsManager);
        await vm.InitializeAsync();
        return (vm, defaultsPath);
    }

    [Fact]
    public async Task NoSettingsFile_WindowKeepsPreviousBuiltInDefaults()
    {
        (BacktestWindowViewModel vm, string path) = await CreateWithSettingsAsync(saved: null);
        try
        {
            Assert.Equal(1_000_000m, vm.InitialCapital);
            Assert.Equal(0m, vm.CommissionFlat);
            Assert.Equal(PositionSizingModel.FixedQuantity, vm.SizingModel);
            Assert.Equal(1m, vm.SizingParameter);
            Assert.Equal(0.30m, vm.InitialMarginRatio);
            Assert.Equal(0.20m, vm.MaintenanceMarginRatio);
            Assert.Equal(0.005m, vm.LiquidationPenaltyRatio);
            Assert.Equal(42, vm.BootstrapSeed);
            Assert.Equal(2000, vm.BootstrapIterations);
            Assert.Equal(0m, vm.AnnualRiskFreeRate);
            Assert.Equal(252, vm.AnnualPeriodsByFrame.Single(e => e.Frame == TimeFrame.D1).Periods);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SettingsFile_IsAppliedWhenWindowOpens()
    {
        (BacktestWindowViewModel vm, string path) = await CreateWithSettingsAsync(Custom());
        try
        {
            Assert.Equal(250_000m, vm.InitialCapital);
            Assert.Equal(1.5m, vm.CommissionFlat);
            Assert.Equal(0.01m, vm.CommissionPerUnit);
            Assert.Equal(0.002m, vm.SlippageRatio);
            Assert.Equal(PositionSizingModel.PercentOfEquity, vm.SizingModel);
            Assert.Equal(0.5m, vm.SizingParameter);
            Assert.Equal(0.4m, vm.InitialMarginRatio);
            Assert.Equal(0.25m, vm.MaintenanceMarginRatio);
            Assert.Equal(0.01m, vm.LiquidationPenaltyRatio);
            Assert.Equal(7, vm.BootstrapSeed);
            Assert.Equal(5000, vm.BootstrapIterations);
            Assert.Equal(0.03m, vm.AnnualRiskFreeRate);
            Assert.Equal(0.02m, vm.AnnualMar);
            Assert.Equal(260, vm.AnnualPeriodsByFrame.Single(e => e.Frame == TimeFrame.D1).Periods);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ResetToDefaults_ReturnsToSettingsDefaults_NotFactory()
    {
        (BacktestWindowViewModel vm, string path) = await CreateWithSettingsAsync(Custom());
        try
        {
            vm.InitialCapital = 1m;
            vm.BootstrapSeed = 1;

            vm.ResetToDefaultsCommand.Execute(null);

            Assert.Equal(250_000m, vm.InitialCapital);
            Assert.Equal(7, vm.BootstrapSeed);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SavedReportSettingsFile_WinsOverSettingsDefaults()
    {
        string defaultsPath = NewTempPath();
        string reportPath = NewTempPath();
        try
        {
            var defaultsManager = new BacktestDefaultSettingsManager(defaultsPath);
            await defaultsManager.SaveAsync(Custom());
            var reportManager = new BacktestReportSettingsManager(reportPath);
            await reportManager.SaveAsync(new BacktestReportDefaults
            {
                SchemaVersion = BacktestReportDefaults.CurrentSchemaVersion,
                AnnualRiskFreeRate = 0.09m,
                AnnualMAR = 0.05m,
                AnnualPeriodsByFrame = { [TimeFrame.D1] = 250, [TimeFrame.W1] = 52, [TimeFrame.MN1] = 12 },
            });

            BacktestWindowViewModel vm = BacktestTestFactory.CreateViewModel(
                reportSettingsManager: reportManager,
                defaultSettingsManager: defaultsManager);
            await vm.InitializeAsync();

            Assert.Equal(0.09m, vm.AnnualRiskFreeRate);
            Assert.Equal(250, vm.AnnualPeriodsByFrame.Single(e => e.Frame == TimeFrame.D1).Periods);
            Assert.Equal(250_000m, vm.InitialCapital); // capital/margin/bootstrap still come from Settings > Backtest.
        }
        finally
        {
            if (File.Exists(defaultsPath)) File.Delete(defaultsPath);
            if (File.Exists(reportPath)) File.Delete(reportPath);
        }
    }

    [Fact]
    public async Task SettingsEditedAfterOpen_DoNotChangeAnOpenWindow()
    {
        (BacktestWindowViewModel vm, string path) = await CreateWithSettingsAsync(Custom());
        try
        {
            var laterSettings = new BacktestDefaultSettingsManager(path);
            BacktestDefaultSettings edited = Custom();
            await laterSettings.SaveAsync(new BacktestDefaultSettings
            {
                SchemaVersion = edited.SchemaVersion,
                InitialCapital = 999m,
                SizingModel = edited.SizingModel,
                SizingParameter = edited.SizingParameter,
                InitialMarginRatio = edited.InitialMarginRatio,
                MaintenanceMarginRatio = edited.MaintenanceMarginRatio,
                AnnualPeriodsByFrame = edited.AnnualPeriodsByFrame,
                BootstrapSeed = edited.BootstrapSeed,
                BootstrapIterations = edited.BootstrapIterations,
            });

            vm.ResetToDefaultsCommand.Execute(null);

            Assert.Equal(250_000m, vm.InitialCapital);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
