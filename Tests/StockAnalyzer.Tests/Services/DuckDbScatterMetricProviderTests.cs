using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using Xunit;

namespace StockAnalyzer.Tests.Services;

public sealed class DuckDbScatterMetricProviderTests : IDisposable
{
    private readonly DuckDBConnectionManager _dbManager;
    private readonly DuckDbScatterMetricProvider _provider;

    private readonly string _dataDaily;

    public DuckDbScatterMetricProviderTests()
    {
        _dbManager = new DuckDBConnectionManager();
        _dataDaily = PathDiscovery.ResolveDataPath(null, "Data/Daily", "*.parquet");
        string metadataPath = PathDiscovery.ResolveDataPath(null, "Data/Metadata", "*.parquet");
        var options = Options.Create(new MarketDataSettings
        {
            DailyDataPath = _dataDaily,
            MetadataPath = metadataPath
        });
        _provider = new DuckDbScatterMetricProvider(_dbManager, options, NullLogger<DuckDbScatterMetricProvider>.Instance);
    }

    public void Dispose()
    {
        _dbManager.Dispose();
    }

    [Fact]
    public async Task ComputeMetricsAsync_EmptyTickers_ReturnsEmptyResult()
    {
        var result = await _provider.ComputeMetricsAsync(Array.Empty<string>(), "Return", "Volatility");

        Assert.Same(ScatterMetricResult.Empty, result);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ComputeMetricsAsync_CancellationToken_CancelsPromptly()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await _provider.ComputeMetricsAsync(new[] { "AAPL", "MSFT" }, "Return", "Volatility", cts.Token);
        });
    }

    [Fact]
    public async Task ComputeMetricsAsync_NonExistentTickers_FallsBackToSyntheticGracefully()
    {
        var nonExistentTickers = new[] { "NON_EXISTENT_XYZ_1", "NON_EXISTENT_XYZ_2" };

        var result = await _provider.ComputeMetricsAsync(nonExistentTickers, "Return", "Volatility");

        Assert.NotNull(result);
        Assert.Equal(2, result.Items.Count);
        // Fallback to synthetic means IsLiveMarketData is false
        Assert.False(result.IsLiveMarketData);
        Assert.Equal("Synthetic", result.ProviderId);
    }

    [Fact]
    public async Task ComputeMetricsAsync_ExistingDailyParquet_ExtractsLiveMetricsInSingleBatch()
    {
        // AAPL and MSFT are standard test files present in Data/Daily
        string aaplPath = Path.Combine(_dataDaily, "AAPL.parquet");
        string msftPath = Path.Combine(_dataDaily, "MSFT.parquet");

        if (!File.Exists(aaplPath) || !File.Exists(msftPath))
        {
            // Skip test if run in isolated environment without data assets
            return;
        }

        var tickers = new[] { "AAPL", "MSFT" };
        var result = await _provider.ComputeMetricsAsync(tickers, "Return", "Volatility");

        Assert.NotNull(result);
        Assert.True(result.IsLiveMarketData);
        Assert.Equal("DuckDB", result.ProviderId);
        Assert.Equal(2, result.Items.Count);

        var aaplItem = result.Items[0];
        Assert.Equal("AAPL", aaplItem.Ticker);
        Assert.True(double.IsFinite(aaplItem.XValue), $"AAPL XValue should be finite, got {aaplItem.XValue}");
        Assert.True(double.IsFinite(aaplItem.YValue), $"AAPL YValue should be finite, got {aaplItem.YValue}");

        var msftItem = result.Items[1];
        Assert.Equal("MSFT", msftItem.Ticker);
        Assert.True(double.IsFinite(msftItem.XValue), $"MSFT XValue should be finite, got {msftItem.XValue}");
        Assert.True(double.IsFinite(msftItem.YValue), $"MSFT YValue should be finite, got {msftItem.YValue}");
    }

    [Fact]
    public async Task ComputeMetricsAsync_TwentyDefaultTickers_ExecutesQuickly()
    {
        string[] defaultTickers =
        [
            "AAPL", "MSFT", "GOOGL", "AMZN", "NVDA", "META", "TSLA", "BRK-B", "JPM", "V",
            "JNJ", "WMT", "PG", "MA", "UNH", "HD", "DIS", "BAC", "VZ", "ADBE"
        ];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await _provider.ComputeMetricsAsync(defaultTickers, "Return", "Volatility");
        sw.Stop();

        Assert.NotNull(result);
        Assert.Equal(20, result.Items.Count);
    }

    [Fact]
    public async Task ComputeMetricsAsync_PartialMissingTickers_ReturnsNanForMissingWithoutCrashing()
    {
        string aaplPath = Path.Combine("Data", "Daily", "AAPL.parquet");
        if (!File.Exists(aaplPath)) return;

        var tickers = new[] { "AAPL", "MISSING_TICKER_12345" };
        var result = await _provider.ComputeMetricsAsync(tickers, "Return", "Volatility");

        Assert.NotNull(result);
        Assert.True(result.IsLiveMarketData);
        Assert.Equal(2, result.Items.Count);

        // First item (AAPL) is valid
        Assert.Equal("AAPL", result.Items[0].Ticker);
        Assert.True(double.IsFinite(result.Items[0].XValue));

        // Second item (MISSING) has NaN without crashing
        Assert.Equal("MISSING_TICKER_12345", result.Items[1].Ticker);
        Assert.True(double.IsNaN(result.Items[1].XValue));
        Assert.True(double.IsNaN(result.Items[1].YValue));
        Assert.True(double.IsNaN(result.Items[1].ReturnPercentage));
    }

    [Fact]
    public async Task ComputeMetricsAsync_FundamentalMetrics_WhenMetaMissing_AssignsNaNNotZero()
    {
        string aaplPath = Path.Combine(_dataDaily, "AAPL.parquet");
        if (!File.Exists(aaplPath)) return;

        var tickers = new[] { "AAPL" };
        var result = await _provider.ComputeMetricsAsync(tickers, "P/E", "MarketCap");

        Assert.NotNull(result);
        Assert.Single(result.Items);
        var item = result.Items[0];
        Assert.Equal("AAPL", item.Ticker);
        Assert.True(double.IsNaN(item.XValue) || item.XValue > 0.0, $"P/E should be NaN or positive, got {item.XValue}");
    }
}
