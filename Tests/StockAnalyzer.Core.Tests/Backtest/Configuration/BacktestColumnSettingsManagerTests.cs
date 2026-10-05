using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Configuration;

public class BacktestColumnSettingsManagerTests
{
    private static string NewTempPath() => Path.Combine(Path.GetTempPath(), $"backtest-cols-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task LoadAsync_WhenFileDoesNotExist_ReturnsNull()
    {
        string path = NewTempPath();
        var manager = new BacktestColumnSettingsManager(path);

        var result = await manager.LoadAsync();
        Assert.Null(result);

        var syncResult = manager.Load();
        Assert.Null(syncResult);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrip_PreservesOrderAndVisibility()
    {
        string path = NewTempPath();
        try
        {
            var manager = new BacktestColumnSettingsManager(path);

            var tradeCols = new List<string> { "ClosedNet", "TradeId", "Quantity" };
            var metricNames = new List<string> { "CAGR", "TotalReturn", "ProfitFactor" };

            await manager.SaveColumnsAsync(tradeCols, metricNames);

            var loaded = await manager.LoadAsync();
            Assert.NotNull(loaded);
            Assert.Equal(1, loaded.SchemaVersion);
            Assert.Equal(tradeCols, loaded.TradeVisibleColumns);
            Assert.Equal(metricNames, loaded.MetricVisibleNames);

            var syncLoaded = manager.Load();
            Assert.NotNull(syncLoaded);
            Assert.Equal(tradeCols, syncLoaded.TradeVisibleColumns);
            Assert.Equal(metricNames, syncLoaded.MetricVisibleNames);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveColumnsAsync_PreservesOtherField_WhenOneIsUpdated()
    {
        string path = NewTempPath();
        try
        {
            var manager = new BacktestColumnSettingsManager(path);

            await manager.SaveColumnsAsync(new[] { "Side", "TradeId" }, null);
            var step1 = manager.Load();
            Assert.NotNull(step1);
            Assert.Equal(new[] { "Side", "TradeId" }, step1.TradeVisibleColumns);
            Assert.Empty(step1.MetricVisibleNames);

            await manager.SaveColumnsAsync(null, new[] { "MaxDrawdown" });
            var step2 = manager.Load();
            Assert.NotNull(step2);
            Assert.Equal(new[] { "Side", "TradeId" }, step2.TradeVisibleColumns);
            Assert.Equal(new[] { "MaxDrawdown" }, step2.MetricVisibleNames);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
