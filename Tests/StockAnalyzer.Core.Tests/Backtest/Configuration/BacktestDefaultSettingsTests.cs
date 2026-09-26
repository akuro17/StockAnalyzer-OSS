using System;
using System.IO;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Configuration;

public class BacktestDefaultSettingsTests
{
    private static string NewTempPath() => Path.Combine(Path.GetTempPath(), $"backtest-defaults-{Guid.NewGuid():N}.json");

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
        AnnualPeriodsByFrame = { [TimeFrame.D1] = 260, [TimeFrame.W1] = 52, [TimeFrame.MN1] = 12 },
        BootstrapSeed = 7,
        BootstrapIterations = 5000,
    };

    [Fact]
    public void BuiltIn_EqualsPreviousHardCodedWindowDefaults()
    {
        BacktestDefaultSettings builtIn = BacktestDefaultSettings.BuiltIn;

        Assert.Equal(1_000_000m, builtIn.InitialCapital);
        Assert.Equal(0m, builtIn.CommissionFlat);
        Assert.Equal(0m, builtIn.CommissionPerUnit);
        Assert.Equal(0m, builtIn.SlippageRatio);
        Assert.Equal(PositionSizingModel.FixedQuantity, builtIn.SizingModel);
        Assert.Equal(1m, builtIn.SizingParameter);
        Assert.Equal(0.30m, builtIn.InitialMarginRatio);
        Assert.Equal(0.20m, builtIn.MaintenanceMarginRatio);
        Assert.Equal(0.005m, builtIn.LiquidationPenaltyRatio);
        Assert.Equal(42, builtIn.BootstrapSeed);
        Assert.Equal(2000, builtIn.BootstrapIterations);
        Assert.Equal(0m, builtIn.AnnualRiskFreeRate);
        Assert.Equal(0m, builtIn.AnnualMAR);
        Assert.Equal(252, builtIn.AnnualPeriodsByFrame[TimeFrame.D1]);
        Assert.Equal(52, builtIn.AnnualPeriodsByFrame[TimeFrame.W1]);
        Assert.Equal(12, builtIn.AnnualPeriodsByFrame[TimeFrame.MN1]);
    }

    [Fact]
    public void BuiltIn_IsValid()
    {
        BacktestDefaultSettings snapshot = BacktestDefaultSettings.SnapshotValidated(BacktestDefaultSettings.BuiltIn);
        Assert.Equal(BacktestDefaultSettings.CurrentSchemaVersion, snapshot.SchemaVersion);
    }

    [Fact]
    public void ToReportDefaults_CarriesReportFields()
    {
        BacktestReportDefaults report = Custom().ToReportDefaults();

        Assert.Equal(0.03m, report.AnnualRiskFreeRate);
        Assert.Equal(0.02m, report.AnnualMAR);
        Assert.Equal(260, report.AnnualPeriodsByFrame[TimeFrame.D1]);
    }

    [Fact]
    public void SnapshotValidated_MaintenanceNotBelowInitial_Throws()
    {
        BacktestDefaultSettings source = Custom();
        var invalid = new BacktestDefaultSettings
        {
            SchemaVersion = source.SchemaVersion,
            InitialCapital = source.InitialCapital,
            SizingModel = source.SizingModel,
            SizingParameter = source.SizingParameter,
            InitialMarginRatio = 0.2m,
            MaintenanceMarginRatio = 0.2m,
            AnnualPeriodsByFrame = source.AnnualPeriodsByFrame,
            BootstrapSeed = source.BootstrapSeed,
            BootstrapIterations = source.BootstrapIterations,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => BacktestDefaultSettings.SnapshotValidated(invalid));
    }

    [Fact]
    public void SnapshotValidated_RiskFreeRateAtOrBelowMinusOne_Throws()
    {
        BacktestDefaultSettings source = Custom();
        var invalid = new BacktestDefaultSettings
        {
            SchemaVersion = source.SchemaVersion,
            InitialCapital = source.InitialCapital,
            SizingModel = source.SizingModel,
            SizingParameter = source.SizingParameter,
            InitialMarginRatio = source.InitialMarginRatio,
            MaintenanceMarginRatio = source.MaintenanceMarginRatio,
            AnnualRiskFreeRate = -1m,
            AnnualPeriodsByFrame = source.AnnualPeriodsByFrame,
            BootstrapSeed = source.BootstrapSeed,
            BootstrapIterations = source.BootstrapIterations,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => BacktestDefaultSettings.SnapshotValidated(invalid));
    }

    [Fact]
    public void SnapshotValidated_BootstrapIterationsBelowMinimum_Throws()
    {
        BacktestDefaultSettings source = Custom();
        var invalid = new BacktestDefaultSettings
        {
            SchemaVersion = source.SchemaVersion,
            InitialCapital = source.InitialCapital,
            SizingModel = source.SizingModel,
            SizingParameter = source.SizingParameter,
            InitialMarginRatio = source.InitialMarginRatio,
            MaintenanceMarginRatio = source.MaintenanceMarginRatio,
            AnnualPeriodsByFrame = source.AnnualPeriodsByFrame,
            BootstrapSeed = source.BootstrapSeed,
            BootstrapIterations = 10,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => BacktestDefaultSettings.SnapshotValidated(invalid));
    }

    [Fact]
    public void FindErrors_BuiltInAndCustom_AreEmpty()
    {
        Assert.Empty(BacktestDefaultSettings.FindErrors(BacktestDefaultSettings.BuiltIn));
        Assert.Empty(BacktestDefaultSettings.FindErrors(Custom()));
    }

    [Fact]
    public void FindErrors_ReportsEachRejectedItemUnderItsOwnKey()
    {
        BacktestDefaultSettings c = Custom();
        var invalid = new BacktestDefaultSettings
        {
            SchemaVersion = c.SchemaVersion,
            InitialCapital = c.InitialCapital,
            SizingModel = c.SizingModel,
            SizingParameter = c.SizingParameter,
            InitialMarginRatio = 0.2m,
            MaintenanceMarginRatio = 0.2m,
            AnnualRiskFreeRate = -1m,
            AnnualPeriodsByFrame = { [TimeFrame.D1] = 0, [TimeFrame.W1] = 52 },
            BootstrapIterations = 10,
        };

        var errors = BacktestDefaultSettings.FindErrors(invalid);

        Assert.Contains(nameof(BacktestDefaultSettings.MaintenanceMarginRatio), errors.Keys);
        Assert.Contains(nameof(BacktestDefaultSettings.AnnualRiskFreeRate), errors.Keys);
        Assert.Contains(BacktestDefaultSettings.AnnualPeriodsErrorKey, errors.Keys);
        Assert.Contains(nameof(BacktestDefaultSettings.BootstrapIterations), errors.Keys);
        Assert.DoesNotContain(nameof(BacktestDefaultSettings.InitialCapital), errors.Keys);
        Assert.DoesNotContain(nameof(BacktestDefaultSettings.AnnualMAR), errors.Keys);
    }

    [Fact]
    public void FindErrors_AgreesWithSnapshotValidated()
    {
        var invalid = new BacktestDefaultSettings { SchemaVersion = BacktestDefaultSettings.CurrentSchemaVersion };

        Assert.NotEmpty(BacktestDefaultSettings.FindErrors(invalid));
        Assert.ThrowsAny<ArgumentException>(() => BacktestDefaultSettings.SnapshotValidated(invalid));
    }

    [Fact]
    public void ValueEquals_SameValuesTrue_AnyDifferingItemFalse()
    {
        Assert.True(Custom().ValueEquals(Custom()));
        Assert.False(Custom().ValueEquals(BacktestDefaultSettings.BuiltIn));

        BacktestDefaultSettings c = Custom();
        var differentPeriod = new BacktestDefaultSettings
        {
            SchemaVersion = c.SchemaVersion,
            InitialCapital = c.InitialCapital,
            CommissionFlat = c.CommissionFlat,
            CommissionPerUnit = c.CommissionPerUnit,
            SlippageRatio = c.SlippageRatio,
            SizingModel = c.SizingModel,
            SizingParameter = c.SizingParameter,
            InitialMarginRatio = c.InitialMarginRatio,
            MaintenanceMarginRatio = c.MaintenanceMarginRatio,
            LiquidationPenaltyRatio = c.LiquidationPenaltyRatio,
            AnnualRiskFreeRate = c.AnnualRiskFreeRate,
            AnnualMAR = c.AnnualMAR,
            AnnualPeriodsByFrame = { [TimeFrame.D1] = 261, [TimeFrame.W1] = 52, [TimeFrame.MN1] = 12 },
            BootstrapSeed = c.BootstrapSeed,
            BootstrapIterations = c.BootstrapIterations,
        };
        Assert.False(c.ValueEquals(differentPeriod));
    }

    [Fact]
    public async Task Manager_NoFile_ReturnsBuiltInFallback()
    {
        var manager = new BacktestDefaultSettingsManager(NewTempPath());

        BacktestDefaultSettingsLoadResult result = await manager.LoadAsync();

        Assert.Equal(BacktestDefaultSettingsLoadStatus.BuiltInFallback, result.Status);
        Assert.Equal(1_000_000m, result.Settings.InitialCapital);
    }

    [Fact]
    public async Task Manager_SaveThenLoad_RoundTripsEveryField()
    {
        string path = NewTempPath();
        try
        {
            var manager = new BacktestDefaultSettingsManager(path);
            await manager.SaveAsync(Custom());

            BacktestDefaultSettingsLoadResult result = await new BacktestDefaultSettingsManager(path).LoadAsync();

            Assert.Equal(BacktestDefaultSettingsLoadStatus.Loaded, result.Status);
            BacktestDefaultSettings loaded = result.Settings;
            Assert.Equal(250_000m, loaded.InitialCapital);
            Assert.Equal(1.5m, loaded.CommissionFlat);
            Assert.Equal(0.01m, loaded.CommissionPerUnit);
            Assert.Equal(0.002m, loaded.SlippageRatio);
            Assert.Equal(PositionSizingModel.PercentOfEquity, loaded.SizingModel);
            Assert.Equal(0.5m, loaded.SizingParameter);
            Assert.Equal(0.4m, loaded.InitialMarginRatio);
            Assert.Equal(0.25m, loaded.MaintenanceMarginRatio);
            Assert.Equal(0.01m, loaded.LiquidationPenaltyRatio);
            Assert.Equal(0.03m, loaded.AnnualRiskFreeRate);
            Assert.Equal(0.02m, loaded.AnnualMAR);
            Assert.Equal(260, loaded.AnnualPeriodsByFrame[TimeFrame.D1]);
            Assert.Equal(7, loaded.BootstrapSeed);
            Assert.Equal(5000, loaded.BootstrapIterations);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Manager_CorruptJson_FallsBackToBuiltIn()
    {
        string path = NewTempPath();
        try
        {
            await File.WriteAllTextAsync(path, "{ not json");

            BacktestDefaultSettingsLoadResult result = await new BacktestDefaultSettingsManager(path).LoadAsync();

            Assert.Equal(BacktestDefaultSettingsLoadStatus.BuiltInFallback, result.Status);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Manager_PreVersioningFile_FallsBackToBuiltIn()
    {
        string path = NewTempPath();
        try
        {
            await File.WriteAllTextAsync(path, "{\"initialCapital\": 5}");

            BacktestDefaultSettingsLoadResult result = await new BacktestDefaultSettingsManager(path).LoadAsync();

            Assert.Equal(BacktestDefaultSettingsLoadStatus.BuiltInFallback, result.Status);
            Assert.Equal(1_000_000m, result.Settings.InitialCapital);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Manager_SaveInvalid_ThrowsAndLeavesFileUntouched()
    {
        string path = NewTempPath();
        var manager = new BacktestDefaultSettingsManager(path);
        var invalid = new BacktestDefaultSettings { SchemaVersion = BacktestDefaultSettings.CurrentSchemaVersion };

        await Assert.ThrowsAnyAsync<ArgumentException>(() => manager.SaveAsync(invalid));

        Assert.False(File.Exists(path));
    }
}
