using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using Xunit;

using Xunit.Abstractions;

namespace StockAnalyzer.Tests.Services;

public sealed class HeatmapReturnProviderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DuckDBConnectionManager _dbManager;
    private readonly ITestOutputHelper _output;

    public HeatmapReturnProviderTests(ITestOutputHelper output)
    {
        _output = output;
        _tempDir = Path.Combine(Path.GetTempPath(), "HeatmapReturnProviderTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbManager = new DuckDBConnectionManager();
    }

    public void Dispose()
    {
        _dbManager.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    private void CreateParquetFile(string symbol, IReadOnlyList<(DateTime Date, double Close)> bars)
    {
        var filePath = Path.Combine(_tempDir, $"{symbol}.parquet").Replace("\\", "/");
        using var conn = _dbManager.GetConnection();
        using var cmd = conn.CreateCommand();

        var valueRows = new List<string>(bars.Count);
        for (int i = 0; i < bars.Count; i++)
        {
            var b = bars[i];
            string dateStr = b.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string closeStr = b.Close.ToString("F4", CultureInfo.InvariantCulture);
            valueRows.Add($"('{dateStr}'::DATE, {closeStr}::DOUBLE, {closeStr}::DOUBLE, {closeStr}::DOUBLE, {closeStr}::DOUBLE, 1000.0::DOUBLE)");
        }

        string valuesClause = string.Join(", ", valueRows);
        string tempTable = $"tmp_{Guid.NewGuid():N}";

        cmd.CommandText = $@"
            CREATE TEMPORARY TABLE {tempTable} (date DATE, open DOUBLE, high DOUBLE, low DOUBLE, close DOUBLE, volume DOUBLE);
            INSERT INTO {tempTable} VALUES {valuesClause};
            COPY {tempTable} TO '{filePath}' (FORMAT PARQUET);
            DROP TABLE {tempTable};";

        cmd.ExecuteNonQuery();
    }

    private DuckDbHeatmapReturnProvider CreateDuckDbProvider()
    {
        var settings = Options.Create(new MarketDataSettings
        {
            DailyDataPath = _tempDir
        });
        return new DuckDbHeatmapReturnProvider(_dbManager, settings, NullLogger<DuckDbHeatmapReturnProvider>.Instance);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(5, 5)]
    [InlineData(9999, 9999)]
    [InlineData(10000, 9999)]
    [InlineData(int.MinValue, 0)]
    [InlineData(int.MaxValue, 9999)]
    public void HeatmapOffsetLimits_Clamp_RespectsBounds(int input, int expected)
    {
        Assert.Equal(expected, HeatmapOffsetLimits.Clamp(input));
    }

    [Fact]
    public async Task SyntheticProvider_OffsetGreaterThan0_ReturnsNaNItems()
    {
        var tickers = new[] { "AAPL", "MSFT" };
        var synthetic = SyntheticHeatmapReturnProvider.Instance;

        var result = await synthetic.ComputeAsync(tickers, 1);

        Assert.Equal(2, result.Items.Count);
        Assert.Null(result.TargetDate);
        Assert.True(double.IsNaN(result.Items[0].ReturnPercentage));
        Assert.True(double.IsNaN(result.Items[1].ReturnPercentage));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10000)]
    public async Task SyntheticProvider_InvalidOffset_ThrowsArgumentOutOfRangeException(int offset)
    {
        var synthetic = SyntheticHeatmapReturnProvider.Instance;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            synthetic.ComputeAsync(new[] { "AAPL" }, offset));
    }

    [Fact]
    public async Task SyntheticProvider_PreCancelledToken_ThrowsOperationCanceledException()
    {
        var synthetic = SyntheticHeatmapReturnProvider.Instance;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            synthetic.ComputeAsync(new[] { "AAPL" }, 0, cts.Token));
    }

    [Fact]
    public async Task DuckDbProvider_Offset0And1_ComputesDailyReturnAndTargetDate()
    {
        // 5 bars each for A and B
        // Dates: 2026-10-12, 13, 14, 15, 16
        var dates = new[]
        {
            new DateTime(2026, 10, 12),
            new DateTime(2026, 10, 13),
            new DateTime(2026, 10, 14),
            new DateTime(2026, 10, 15),
            new DateTime(2026, 10, 16)
        };

        CreateParquetFile("AAA", new[]
        {
            (dates[0], 100.0),
            (dates[1], 105.0),
            (dates[2], 102.0),
            (dates[3], 110.0),
            (dates[4], 121.0)
        });

        CreateParquetFile("BBB", new[]
        {
            (dates[0], 200.0),
            (dates[1], 190.0),
            (dates[2], 195.0),
            (dates[3], 200.0),
            (dates[4], 190.0)
        });

        var provider = CreateDuckDbProvider();

        // Offset 0: TargetDate = 2026-10-16
        // AAA: (121 - 110) / 110 = 11 / 110 = 0.10
        // BBB: (190 - 200) / 200 = -10 / 200 = -0.05
        var res0 = await provider.ComputeAsync(new[] { "AAA", "BBB" }, 0);

        Assert.Equal(DateOnly.FromDateTime(dates[4]), res0.TargetDate);
        Assert.True(res0.IsLiveMarketData);
        Assert.Equal(2, res0.Items.Count);
        Assert.Equal(0.10, res0.Items[0].ReturnPercentage, precision: 6);
        Assert.Equal(-0.05, res0.Items[1].ReturnPercentage, precision: 6);

        // Offset 1: TargetDate = 2026-10-15
        // AAA: (110 - 102) / 102 = 8 / 102
        // BBB: (200 - 195) / 195 = 5 / 195
        var res1 = await provider.ComputeAsync(new[] { "AAA", "BBB" }, 1);

        Assert.Equal(DateOnly.FromDateTime(dates[3]), res1.TargetDate);
        Assert.Equal(8.0 / 102.0, res1.Items[0].ReturnPercentage, precision: 6);
        Assert.Equal(5.0 / 195.0, res1.Items[1].ReturnPercentage, precision: 6);
    }

    [Fact]
    public async Task DuckDbProvider_StaleTickerMissingLatestDate_YieldsNaNAtOffset0AndFiniteAtOffset1()
    {
        // AAA has dates D1..D5, BBB only has D1..D4 (stale at D5)
        var dates = new[]
        {
            new DateTime(2026, 10, 12),
            new DateTime(2026, 10, 13),
            new DateTime(2026, 10, 14),
            new DateTime(2026, 10, 15),
            new DateTime(2026, 10, 16)
        };

        CreateParquetFile("AAA", new[]
        {
            (dates[0], 100.0),
            (dates[1], 105.0),
            (dates[2], 102.0),
            (dates[3], 110.0),
            (dates[4], 121.0)
        });

        CreateParquetFile("BBB", new[]
        {
            (dates[0], 200.0),
            (dates[1], 190.0),
            (dates[2], 195.0),
            (dates[3], 200.0)
        });

        var provider = CreateDuckDbProvider();

        // Offset 0: Union target date is D5. BBB has no bar on D5 -> NaN
        var res0 = await provider.ComputeAsync(new[] { "AAA", "BBB" }, 0);
        Assert.Equal(DateOnly.FromDateTime(dates[4]), res0.TargetDate);
        Assert.Equal(0.10, res0.Items[0].ReturnPercentage, precision: 6);
        Assert.True(double.IsNaN(res0.Items[1].ReturnPercentage));

        // Offset 1: Union target date is D4. Both AAA and BBB have a bar on D4 -> both finite
        var res1 = await provider.ComputeAsync(new[] { "AAA", "BBB" }, 1);
        Assert.Equal(DateOnly.FromDateTime(dates[3]), res1.TargetDate);
        Assert.Equal(8.0 / 102.0, res1.Items[0].ReturnPercentage, precision: 6);
        Assert.Equal(5.0 / 195.0, res1.Items[1].ReturnPercentage, precision: 6);
    }

    [Fact]
    public async Task DuckDbProvider_OffsetEqualOrExceedingHistory_YieldsNullTargetDateAndNaN()
    {
        var dates = new[] { new DateTime(2026, 10, 15), new DateTime(2026, 10, 16) };
        CreateParquetFile("AAA", new[] { (dates[0], 100.0), (dates[1], 105.0) });

        var provider = CreateDuckDbProvider();

        // 2 distinct dates: offset 2 is beyond history
        var res = await provider.ComputeAsync(new[] { "AAA" }, 2);
        Assert.Null(res.TargetDate);
        Assert.Equal(1, res.Items.Count);
        Assert.True(double.IsNaN(res.Items[0].ReturnPercentage));
    }

    [Fact]
    public async Task DuckDbProvider_OldestBarHasNoPreviousClose_YieldsNaN()
    {
        var dates = new[] { new DateTime(2026, 10, 15), new DateTime(2026, 10, 16) };
        CreateParquetFile("AAA", new[] { (dates[0], 100.0), (dates[1], 105.0) });

        var provider = CreateDuckDbProvider();

        // Offset 1 targets the oldest bar (2026-10-15); no prior bar exists so prev_close is NULL
        var res = await provider.ComputeAsync(new[] { "AAA" }, 1);
        Assert.Equal(DateOnly.FromDateTime(dates[0]), res.TargetDate);
        Assert.True(double.IsNaN(res.Items[0].ReturnPercentage));
    }

    [Fact]
    public async Task DuckDbProvider_PrevCloseZero_YieldsNaN()
    {
        var dates = new[] { new DateTime(2026, 10, 15), new DateTime(2026, 10, 16) };
        CreateParquetFile("ZERO", new[] { (dates[0], 0.0), (dates[1], 10.0) });

        var provider = CreateDuckDbProvider();
        var res = await provider.ComputeAsync(new[] { "ZERO" }, 0);

        Assert.Equal(DateOnly.FromDateTime(dates[1]), res.TargetDate);
        Assert.True(double.IsNaN(res.Items[0].ReturnPercentage));
    }

    [Fact]
    public async Task DuckDbProvider_UnknownAndDuplicateTickers_HandledCorrectly()
    {
        var date = new DateTime(2026, 10, 16);
        var prevDate = new DateTime(2026, 10, 15);
        CreateParquetFile("KNOWN", new[] { (prevDate, 100.0), (date, 110.0) });

        var provider = CreateDuckDbProvider();
        var res = await provider.ComputeAsync(new[] { "KNOWN", "UNKNOWN", "KNOWN" }, 0);

        Assert.Equal(3, res.Items.Count);
        Assert.Equal("KNOWN", res.Items[0].Ticker);
        Assert.Equal(0.10, res.Items[0].ReturnPercentage, precision: 6);

        Assert.Equal("UNKNOWN", res.Items[1].Ticker);
        Assert.True(double.IsNaN(res.Items[1].ReturnPercentage));

        Assert.Equal("KNOWN", res.Items[2].Ticker);
        Assert.Equal(0.10, res.Items[2].ReturnPercentage, precision: 6);
    }

    [Fact]
    public async Task DuckDbProvider_NoFilesForTickers_FallsBackToSynthetic()
    {
        var provider = CreateDuckDbProvider();
        var res = await provider.ComputeAsync(new[] { "NONEXISTENT1", "NONEXISTENT2" }, 0);

        Assert.Equal("Synthetic", res.ProviderId);
        Assert.Equal(2, res.Items.Count);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("..\\evil")]
    [InlineData("sub/ticker")]
    [InlineData("sub\\ticker")]
    [InlineData("ticker:name")]
    [InlineData("   ")]
    public async Task DuckDbProvider_PathTraversalTicker_IsSafelySkipped(string malformedSymbol)
    {
        var provider = CreateDuckDbProvider();
        var res = await provider.ComputeAsync(new[] { malformedSymbol }, 0);
        Assert.NotNull(res);
        Assert.Equal("Synthetic", res.ProviderId);
    }

    [Fact]
    public async Task DuckDbProvider_PreCancelledToken_ThrowsOperationCanceledException()
    {
        var provider = CreateDuckDbProvider();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            provider.ComputeAsync(new[] { "AAA" }, 0, cts.Token));
    }

    [Fact]
    public async Task PerformanceMeasurement_LocalDailyData_MeasuresHeatmap()
    {
        string? localDaily = StockAnalyzer.Core.Common.PathDiscovery.ResolveDataPath(null, "Data/Daily", "*.parquet");
        if (string.IsNullOrEmpty(localDaily) || !Directory.Exists(localDaily)) return;

        var files = Directory.GetFiles(localDaily, "*.parquet");
        if (files.Length == 0) return;

        var tickers = files.Select(Path.GetFileNameWithoutExtension).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToArray();
        if (tickers.Length == 0) return;

        var settings = Options.Create(new MarketDataSettings
        {
            DailyDataPath = localDaily,
            MetadataPath = StockAnalyzer.Core.Common.PathDiscovery.ResolveDataPath(null, "Data/Metadata", "*.parquet")
        });

        var heatmapProvider = new DuckDbHeatmapReturnProvider(_dbManager, settings, NullLogger<DuckDbHeatmapReturnProvider>.Instance);

        // Warmup
        await heatmapProvider.ComputeAsync(tickers, 0);

        var heatmapTimes = new List<double>(5);

        for (int i = 0; i < 5; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var hmRes = await heatmapProvider.ComputeAsync(tickers, 0);
            sw.Stop();
            heatmapTimes.Add(sw.Elapsed.TotalMilliseconds);
            Assert.True(hmRes.IsLiveMarketData);
        }

        heatmapTimes.Sort();

        _output.WriteLine($"[Heatmap performance ({tickers.Length} tickers)]");
        _output.WriteLine($"Heatmap ComputeAsync(offset 0) 5 runs (ms): {string.Join(", ", heatmapTimes.Select(t => t.ToString("F2")))} -> Median = {heatmapTimes[2]:F2} ms");
    }
}
