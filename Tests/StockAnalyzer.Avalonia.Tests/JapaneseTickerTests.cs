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
    public async Task ParquetDataService_LoadsGSPC_AndAligns()
    {
        var dailyPath = StockAnalyzer.Core.Common.PathDiscovery.ResolveDataPath(null, "Data/Daily");
        var dbManager = new DuckDBConnectionManager();
        var dataService = new ParquetDataService(dbManager, Microsoft.Extensions.Options.Options.Create(new MarketDataSettings { DailyDataPath = dailyPath }));

        var allGspc = await dataService.LoadCandlesAsync("GSPC", TimeFrame.D1, 0);
        var allAapl = await dataService.LoadCandlesAsync("AAPL", TimeFrame.D1, 0);
        var all7203 = await dataService.LoadCandlesAsync("7203-T", TimeFrame.D1, 0);

        System.Diagnostics.Debug.WriteLine($"GSPC total count: {allGspc.Count}, Date range: {allGspc.FirstOrDefault().Timestamp:yyyy-MM-dd} ~ {allGspc.LastOrDefault().Timestamp:yyyy-MM-dd}");
        System.Diagnostics.Debug.WriteLine($"AAPL total count: {allAapl.Count}, Date range: {allAapl.FirstOrDefault().Timestamp:yyyy-MM-dd} ~ {allAapl.LastOrDefault().Timestamp:yyyy-MM-dd}");
        System.Diagnostics.Debug.WriteLine($"7203 total count: {all7203.Count}, Date range: {all7203.FirstOrDefault().Timestamp:yyyy-MM-dd} ~ {all7203.LastOrDefault().Timestamp:yyyy-MM-dd}");

        Assert.True(allGspc.Count > 0, "GSPC count must be > 0");



        var aligner = new ComparisonDataAligner(dataService);
        var fullGspc = await dataService.LoadCandlesAsync("GSPC", TimeFrame.D1, 50);
        var coreCandles = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Select(fullGspc, c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)));
        var alignRes = await aligner.AlignAsync("GSPC", new[] { "GSPC" }, TimeFrame.D1, 50);

        var secMap = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.IReadOnlyList<CoreCandleData?>>(System.StringComparer.OrdinalIgnoreCase);
        if (alignRes.Series.TryGetValue("GSPC", out var arr))
        {
            secMap["GSPC"] = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Select(arr, c => c.HasValue ? new CoreCandleData(c.Value.Timestamp, c.Value.Open, c.Value.High, c.Value.Low, c.Value.Close, c.Value.Volume) : (CoreCandleData?)null));
        }

        var pipeline = new StockAnalyzer.Core.Services.Analysis.AnalysisPipelineService();

        var param = new StockAnalyzer.Core.Models.Parameters.CoreCorrelationParameter { Period = 20, ComparisonSymbol = "GSPC" };

        var settings = new CoreIndicatorSettings
        {
            Id = "corr-1",
            TypeEnum = IndicatorType.Correlation,
            IsEnabled = true,
            ParameterObject = param
        };

        var res = pipeline.CalculateIndicators(coreCandles, new[] { settings }, secMap);
        Assert.True(res.ContainsKey("corr-1"));
        Assert.True(res["corr-1"].IsSuccessful);
        var lastVal = System.Linq.Enumerable.Last(res["corr-1"].MainValues);
        Assert.NotNull(lastVal);
        Assert.Equal(1.0m, lastVal.Value, 4);
        // 1. Test caret fallback for index symbols
        var caretGspc = await dataService.LoadCandlesAsync("^GSPC", TimeFrame.D1, 50);
        Assert.NotEmpty(caretGspc);
        Assert.Equal(50, caretGspc.Count);

        // 2. Test self-correlation with GSPC and ^GSPC
        var alignResCaret = await aligner.AlignAsync("GSPC", new[] { "^GSPC" }, TimeFrame.D1, 50);
        Assert.True(alignResCaret.Series.ContainsKey("^GSPC"));

        var secMapCaret = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.IReadOnlyList<CoreCandleData?>>(System.StringComparer.OrdinalIgnoreCase);
        if (alignResCaret.Series.TryGetValue("^GSPC", out var arrCaret))
        {
            secMapCaret["^GSPC"] = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Select(arrCaret, c => c.HasValue ? new CoreCandleData(c.Value.Timestamp, c.Value.Open, c.Value.High, c.Value.Low, c.Value.Close, c.Value.Volume) : (CoreCandleData?)null));
        }

        var paramCaret = new StockAnalyzer.Core.Models.Parameters.CoreCorrelationParameter { Period = 20, ComparisonSymbol = "^GSPC" };
        var settingsCaret = new CoreIndicatorSettings
        {
            Id = "corr-caret",
            TypeEnum = IndicatorType.Correlation,
            IsEnabled = true,
            ParameterObject = paramCaret
        };

        var resCaret = pipeline.CalculateIndicators(coreCandles, new[] { settingsCaret }, secMapCaret);
        Assert.True(resCaret["corr-caret"].IsSuccessful);
        var lastValCaret = System.Linq.Enumerable.Last(resCaret["corr-caret"].MainValues);
        Assert.NotNull(lastValCaret);
        Assert.Equal(1.0m, lastValCaret.Value, 4);

        // 3. Test cross-ticker correlation: AAPL vs GSPC and 7203-T vs ^GSPC
        var alignAapl = await aligner.AlignAsync("AAPL", new[] { "GSPC" }, TimeFrame.D1, 50);
        Assert.True(alignAapl.Series.ContainsKey("GSPC"));

        var align7203 = await aligner.AlignAsync("7203-T", new[] { "^GSPC" }, TimeFrame.D1, 50);
        Assert.True(align7203.Series.ContainsKey("^GSPC"));

        // 4. Test Weekly aggregated self-correlation: AAPL on W1 with AAPL comparison
        var tfManager = new StockAnalyzer.Avalonia.Services.TimeFrameManager(dataService);
        var weeklyCandles = await tfManager.GetCandlesAsync("AAPL", TimeFrame.W1, 50);
        var coreWeekly = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Select(weeklyCandles, c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)));
        var secWeekly = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.IReadOnlyList<CoreCandleData?>>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["AAPL"] = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Select(coreWeekly, c => (CoreCandleData?)c))
        };
        var paramWeekly = new StockAnalyzer.Core.Models.Parameters.CoreCorrelationParameter { Period = 20, ComparisonSymbol = "AAPL" };
        var settingsWeekly = new CoreIndicatorSettings
        {
            Id = "corr-weekly",
            TypeEnum = IndicatorType.Correlation,
            IsEnabled = true,
            ParameterObject = paramWeekly
        };
        var resWeekly = pipeline.CalculateIndicators(coreWeekly, new[] { settingsWeekly }, secWeekly);
        Assert.True(resWeekly["corr-weekly"].IsSuccessful);
        var lastValWeekly = System.Linq.Enumerable.Last(resWeekly["corr-weekly"].MainValues);
        Assert.NotNull(lastValWeekly);
        Assert.Equal(1.0m, lastValWeekly.Value, 4);
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
