using System;
using System.IO;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Reporting;

public class BacktestReportDefaultsTests
{
    [Fact]
    public void BuiltIn_MatchesSpecifiedDefaults()
    {
        var defaults = BacktestReportDefaults.BuiltIn;

        Assert.Equal(BacktestReportDefaults.CurrentSchemaVersion, defaults.SchemaVersion);
        Assert.Equal(0m, defaults.AnnualRiskFreeRate);
        Assert.Equal(0m, defaults.AnnualMAR);
        Assert.Equal(252, defaults.AnnualPeriodsByFrame[TimeFrame.D1]);
        Assert.Equal(52, defaults.AnnualPeriodsByFrame[TimeFrame.W1]);
        Assert.Equal(12, defaults.AnnualPeriodsByFrame[TimeFrame.MN1]);
    }

    [Fact]
    public async Task AtomicJsonFile_RoundTrip_PreservesEnumKeyedDictionary()
    {
        string tempPath = Path.GetTempFileName();
        try
        {
            var original = new BacktestReportDefaults
            {
                AnnualRiskFreeRate = 0.02m,
                AnnualMAR = 0.01m,
                AnnualPeriodsByFrame = { [TimeFrame.D1] = 252, [TimeFrame.W1] = 52 },
            };
            File.Delete(tempPath); // AtomicJsonFile.SaveAsync creates it; start from "file does not exist".

            await AtomicJsonFile.SaveAsync(tempPath, original);
            BacktestReportDefaults? loaded = await AtomicJsonFile.LoadAsync<BacktestReportDefaults?>(tempPath);

            Assert.NotNull(loaded);
            Assert.Equal(original.AnnualRiskFreeRate, loaded!.AnnualRiskFreeRate);
            Assert.Equal(original.AnnualMAR, loaded.AnnualMAR);
            Assert.Equal(252, loaded.AnnualPeriodsByFrame[TimeFrame.D1]);
            Assert.Equal(52, loaded.AnnualPeriodsByFrame[TimeFrame.W1]);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task BacktestReportSettingsManager_LoadAsync_NoFileYet_ReturnsBuiltIn()
    {
        string settingsPath = Path.Combine(Path.GetTempPath(), $"backtest-report-{System.Guid.NewGuid():N}.json");

        var manager = new BacktestReportSettingsManager(settingsPath);
        BacktestReportDefaults loaded = await manager.LoadAsync();

        Assert.Equal(BacktestReportDefaults.BuiltIn.AnnualRiskFreeRate, loaded.AnnualRiskFreeRate);
        Assert.Equal(BacktestReportDefaults.BuiltIn.AnnualMAR, loaded.AnnualMAR);
    }

    [Fact]
    public async Task BacktestReportSettingsManager_LoadAsync_PreVersioningFile_FallsBackToBuiltIn()
    {
        string settingsPath = Path.Combine(Path.GetTempPath(), $"backtest-report-{System.Guid.NewGuid():N}.json");

        try
        {
            // A pre-SchemaVersion on-disk shape: no "schemaVersion" property at all, so it deserializes
            // to the type's default (0), not CurrentSchemaVersion.
            await File.WriteAllTextAsync(settingsPath,
                """{"annualRiskFreeRate":0.05,"annualMAR":0.03,"annualPeriodsByFrame":{"D1":252,"W1":52}}""");

            var manager = new BacktestReportSettingsManager(settingsPath);
            BacktestReportDefaults loaded = await manager.LoadAsync();

            Assert.Equal(BacktestReportDefaults.BuiltIn.SchemaVersion, loaded.SchemaVersion);
            Assert.Equal(BacktestReportDefaults.BuiltIn.AnnualRiskFreeRate, loaded.AnnualRiskFreeRate);
            Assert.Equal(BacktestReportDefaults.BuiltIn.AnnualMAR, loaded.AnnualMAR);
        }
        finally
        {
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
        }
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\":1,\"annualRiskFreeRate\":-1,\"annualMAR\":0,\"annualPeriodsByFrame\":{}}")]
    [InlineData("{\"schemaVersion\":1,\"annualRiskFreeRate\":0,\"annualMAR\":0,\"annualPeriodsByFrame\":null}")]
    public async Task BacktestReportSettingsManager_LoadAsync_InvalidWholeObject_FallsBackToBuiltIn(string json)
    {
        string settingsPath = Path.Combine(Path.GetTempPath(), $"backtest-report-{System.Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(settingsPath, json);
            var manager = new BacktestReportSettingsManager(settingsPath);

            BacktestReportDefaults loaded = await manager.LoadAsync();

            Assert.Equal(BacktestReportDefaults.BuiltIn.AnnualPeriodsByFrame, loaded.AnnualPeriodsByFrame);
            Assert.Equal(0m, loaded.AnnualRiskFreeRate);
            Assert.Equal(0m, loaded.AnnualMAR);
        }
        finally
        {
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
        }
    }

    [Fact]
    public async Task BacktestReportSettingsManager_SaveAsync_InvalidObject_FailsBeforeCreatingFile()
    {
        string settingsPath = Path.Combine(Path.GetTempPath(), $"backtest-report-{System.Guid.NewGuid():N}.json");
        var manager = new BacktestReportSettingsManager(settingsPath);
        var invalid = new BacktestReportDefaults
        {
            SchemaVersion = BacktestReportDefaults.CurrentSchemaVersion,
            AnnualPeriodsByFrame = null!,
        };

        await Assert.ThrowsAsync<ArgumentException>(() => manager.SaveAsync(invalid));
        Assert.False(File.Exists(settingsPath));
    }
}
