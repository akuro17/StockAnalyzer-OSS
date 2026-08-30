using System.Threading.Tasks;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using System.IO;

namespace StockAnalyzer.Avalonia.Tests;

public class JapaneseTickerTests
{
    [Fact]
    public async Task ParquetDataService_LoadsCandles_WithDotOrHyphenFallback()
    {
        var dailyPath = StockAnalyzer.Core.Common.PathDiscovery.ResolveDataPath(null, "Data/Daily");
        Assert.True(Directory.Exists(dailyPath), $"dailyPath '{dailyPath}' must exist");

        var dbManager = new DuckDBConnectionManager();
        var dataService = new ParquetDataService(dbManager, Microsoft.Extensions.Options.Options.Create(new MarketDataSettings { DailyDataPath = dailyPath }));

        var candles3382 = await dataService.LoadCandlesAsync("3382-T", TimeFrame.D1, 10);
        Assert.NotEmpty(candles3382);
        Assert.True(candles3382.Count == 10);

        var provider = new ParquetMarketDataProvider(dbManager, null!, Microsoft.Extensions.Options.Options.Create(new MarketDataSettings { DailyDataPath = dailyPath, MetadataPath = dailyPath }));
        var providerCandles = await provider.GetTickersDataAsync("3382-T", TimeFrame.D1);
        Assert.NotEmpty(providerCandles);

        var tfManager = new StockAnalyzer.Avalonia.Services.TimeFrameManager(dataService);
        var d1Candles = await tfManager.GetCandlesAsync("3382-T", TimeFrame.D1, 50);
        var w1Candles = await tfManager.GetCandlesAsync("3382-T", TimeFrame.W1, 50);
        var mn1Candles = await tfManager.GetCandlesAsync("3382-T", TimeFrame.MN1, 50);

        Assert.NotEmpty(d1Candles);
        Assert.NotEmpty(w1Candles);
        Assert.NotEmpty(mn1Candles);

        var candlesWithDot = await dataService.LoadCandlesAsync("5334.T", TimeFrame.D1, 10);
        var candlesWithHyphen = await dataService.LoadCandlesAsync("5334-T", TimeFrame.D1, 10);

        Assert.NotEmpty(candlesWithDot);
        Assert.NotEmpty(candlesWithHyphen);
        Assert.Equal(candlesWithDot.Count, candlesWithHyphen.Count);
    }

    [Fact]
    public async Task ParquetMarketDataProvider_GetAvailableTickersAsync_DeduplicatesDotAndHyphenTickers()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "StockAnalyzer_DedupTest_" + System.Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "5334.T.parquet"), "");
            File.WriteAllText(Path.Combine(tempDir, "5334-T.parquet"), "");

            using var dbManager = new DuckDBConnectionManager();
            var settings = Microsoft.Extensions.Options.Options.Create(new MarketDataSettings { DailyDataPath = tempDir, TickerListPath = null, MetadataPath = tempDir });
            var provider = new ParquetMarketDataProvider(dbManager, null!, settings, Microsoft.Extensions.Logging.Abstractions.NullLogger<ParquetMarketDataProvider>.Instance);

            var tickers = await provider.GetAvailableTickersAsync();

            Assert.Single(tickers);
            Assert.Equal("5334-T", tickers[0]);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task ParquetMarketDataProvider_AddTickerAsync_NormalizesDotToHyphen()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "StockAnalyzer_AddTickerTest_" + System.Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        var tickerListPath = Path.Combine(tempDir, "tickers.json");
        try
        {
            using var dbManager = new DuckDBConnectionManager();
            var settings = Microsoft.Extensions.Options.Options.Create(new MarketDataSettings
            {
                DailyDataPath = tempDir,
                TickerListPath = tickerListPath,
                MetadataPath = tempDir
            });
            var provider = new ParquetMarketDataProvider(dbManager, null!, settings, Microsoft.Extensions.Logging.Abstractions.NullLogger<ParquetMarketDataProvider>.Instance);

            await provider.AddTickerAsync("5334.T");

            var tickers = await provider.GetAvailableTickersAsync();
            Assert.Contains("5334-T", tickers);
            Assert.DoesNotContain("5334.T", tickers);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
