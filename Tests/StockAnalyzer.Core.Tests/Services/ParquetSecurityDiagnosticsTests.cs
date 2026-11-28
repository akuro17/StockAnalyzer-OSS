using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ScreeningConditions;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.TestSupport;
using Xunit;
using static StockAnalyzer.Core.Tests.Services.ParquetDataServiceLoadCharacterizationTests;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class ParquetSecurityDiagnosticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa-parquet-diag-" + Guid.NewGuid().ToString("N"));
    private readonly DuckDBConnectionManager _db = new();

    public ParquetSecurityDiagnosticsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Daily"));
        Directory.CreateDirectory(Path.Combine(_root, "Weekly"));
        Directory.CreateDirectory(Path.Combine(_root, "Monthly"));
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void DiagnoseSeries_ContinuousFuturesWithNegativeClose_ExemptsNonPositivePricePerQ9A()
    {
        var bars = new[]
        {
            new CandleData(new DateTime(2020, 4, 17, 0, 0, 0, DateTimeKind.Utc), 18m, 19m, 17m, 18.2m, 100),
            new CandleData(new DateTime(2020, 4, 20, 0, 0, 0, DateTimeKind.Utc), 17m, 17m, -40m, -37.63m, 200),
        };

        var diag = CandleInvariant.DiagnoseSeries("CL=F", bars);

        Assert.False(diag.HasIssues);
        Assert.Equal(0, diag.InvalidBarCount);
        Assert.Equal(0, diag.NonMonotonicBarCount);
    }

    [Fact]
    public void DiagnoseSeries_ForexPairWithNegativeClose_ReportsNonPositivePriceWarning()
    {
        var bars = new[]
        {
            new CandleData(new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), 141m, 142m, -1m, -0.5m, 0),
        };

        var diag = CandleInvariant.DiagnoseSeries("JPY=X", bars);

        Assert.True(diag.HasIssues);
        Assert.Equal(1, diag.InvalidBarCount);
    }

    [Fact]
    public void DiagnoseSeries_HighBelowLowAndDuplicateTimestamp_CountsBothWithoutModifyingBars()
    {
        var d1 = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var bars = new[]
        {
            new CandleData(d1, 10m, 9m, 11m, 10m, 100),
            new CandleData(d1, 10m, 12m, 9m, 11m, 100),
        };

        var diag = CandleInvariant.DiagnoseSeries("AAPL", bars);

        Assert.True(diag.HasIssues);
        Assert.Equal(1, diag.InvalidBarCount);
        Assert.Equal(1, diag.NonMonotonicBarCount);
        Assert.Equal(d1, diag.FirstInvalidTimestamp);
    }

    [Fact]
    public async Task ParquetDataService_LoadCandlesWithStatusAsync_InvalidOhlcvLogsWarningAndRetainsRows()
    {
        string file = Path.Combine(_root, "Daily", "BAD.parquet");
        TestParquetWriter.Write(_db, file, new[]
        {
            new CandleData(new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), 10m, 8m, 12m, 10m, 100L),
            new CandleData(new DateTime(2024, 1, 3, 0, 0, 0, DateTimeKind.Utc), 10m, 12m, 9m, 11m, 100L)
        }, floatPrices: true);

        var settings = Options.Create(new MarketDataSettings { DailyDataPath = Path.Combine(_root, "Daily") });
        var logger = new RecordingLogger();
        var service = new ParquetDataService(_db, settings, logger);

        var result = await service.LoadCandlesWithStatusAsync("BAD", TimeFrame.D1, 0);

        Assert.Equal(DataReadStatus.Read, result.Status);
        Assert.Equal(2, result.Candles.Count);
        Assert.Equal(1, result.InvalidBarCount);
        Assert.Single(logger.Entries.Where(e => e.Level == LogLevel.Warning));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("NUL")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("../evil")]
    public async Task AddTickerAsync_UnsafeSymbol_ThrowsArgumentExceptionAndDoesNotWrite(string unsafeSymbol)
    {
        var tickerListPath = Path.Combine(_root, "tickers.json");
        await File.WriteAllTextAsync(tickerListPath, "[\"MSFT\"]");
        var settings = Options.Create(new MarketDataSettings
        {
            DailyDataPath = Path.Combine(_root, "Daily"),
            TickerListPath = tickerListPath
        });
        var provider = new ParquetMarketDataProvider(_db, UnavailablePythonService.Instance, settings, NullLogger<ParquetMarketDataProvider>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => provider.AddTickerAsync(unsafeSymbol));

        var registered = await provider.GetRegisteredTickersAsync();
        Assert.Equal(new[] { "MSFT" }, registered);
    }

    [Fact]
    public async Task AddTickerAsync_And_GetRegisteredTickersAsync_PreserveCaretIndexAndFilterUnsafeEntries()
    {
        var tickerListPath = Path.Combine(_root, "tickers.json");
        await File.WriteAllTextAsync(tickerListPath, "[\"MSFT\", \"CON\", \"a*b\", \"^GSPC\", \"CL=F\", \"JPY=X\"]");
        var settings = Options.Create(new MarketDataSettings
        {
            DailyDataPath = Path.Combine(_root, "Daily"),
            TickerListPath = tickerListPath
        });
        var provider = new ParquetMarketDataProvider(_db, UnavailablePythonService.Instance, settings, NullLogger<ParquetMarketDataProvider>.Instance);

        var registered = await provider.GetRegisteredTickersAsync();
        Assert.Equal(new[] { "MSFT", "^GSPC", "CL=F", "JPY=X" }, registered);

        await provider.AddTickerAsync("7203.T");
        registered = await provider.GetRegisteredTickersAsync();
        Assert.Contains("7203-T", registered);
    }

    [Fact]
    public void FormatDuckDbPath_And_SqlConditionTranslator_EscapeQuotesAndBackslashes()
    {
        string raw = "C:\\data\\o'reilly\\*.parquet";
        string formatted = ParquetMarketDataProvider.FormatDuckDbPath(raw);
        Assert.Equal("C:/data/o''reilly/*.parquet", formatted);

        var criteria = new ScreeningCriteria { Conditions = [new RsiOversoldCondition(14, 30m)] };
#pragma warning disable CS0618 // SqlConditionTranslator is intentionally retained for legacy DuckDB SQL path diagnostics.
        string sql = SqlConditionTranslator.BuildBatchQuery(raw, criteria);
#pragma warning restore CS0618
        Assert.Contains($"read_parquet('{formatted}'", sql);
    }
}
