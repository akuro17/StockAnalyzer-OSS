using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ScreeningConditions;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Vendor-suffixed symbols (continuous futures <c>CL=F</c>, <c>GC=F</c>, <c>HG=F</c> and currency pairs <c>JPY=X</c>, <c>EURUSD=X</c>): the symbol is the file stem, so a stem containing '=' must travel through every
/// DuckDB read shape the app uses without being mistaken for a Hive partition (a mis-detection can be silent: an extra column named after the key).
/// Every probe therefore asserts the EXACT column list and the values, not only "no exception". Real parquet files in a temp folder; nothing under Data/.
/// </summary>
public sealed class VendorSuffixedSymbolPathTests : IDisposable
{
    private const int BarCount = 40;
    private const string OtherSymbol = "AAA";
    private static readonly string[] CanonicalColumns = { "date", "open", "high", "low", "close", "volume" };

    // Vendor-suffixed symbols whose file stem contains '=': continuous futures (=F) and currency pairs (=X, both spellings).
    public static TheoryData<string> VendorSuffixedSymbols => new() { "CL=F", "GC=F", "HG=F", "JPY=X", "EURUSD=X", "USDJPY=X" };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_futures_" + Guid.NewGuid().ToString("N"));
    private readonly DuckDBConnectionManager _db = new(NullLogger<DuckDBConnectionManager>.Instance);

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best-effort cleanup */ }
    }

    private string Folder(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    // Alternating closes (100, 101, 100, ...): the last RSI window holds one gain and one loss, so RSI(2) is 50 and "RSI < 100" is true.
    private static decimal CloseAt(int index) => 100m + (index % 2);

    private static IEnumerable<DateTime> Dates()
    {
        var date = new DateTime(2024, 1, 1);
        int produced = 0;
        while (produced < BarCount)
        {
            if (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                yield return date;
                produced++;
            }
            date = date.AddDays(1);
        }
    }

    private static List<CandleData> Bars() => Dates()
        .Select((date, index) =>
        {
            decimal close = CloseAt(index);
            return new CandleData(date, close - 0.5m, close + 0.5m, close - 1m, close, 1000 + index);
        })
        .ToList();

    /// <summary>Writes the six canonical daily columns (prices FLOAT, volume BIGINT) to <c>{folder}/{stem}.parquet</c> through the shared <see cref="TestParquetWriter"/>.</summary>
    private string WriteDaily(string folder, string stem)
    {
        string path = Path.Combine(folder, stem + ".parquet");
        TestParquetWriter.Write(_db, path, Bars(), floatPrices: true);
        return path;
    }

    private static List<string> ColumnsOf(string path)
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM read_parquet('{ParquetMarketDataProvider.FormatDuckDbPath(path)}')";
        using var reader = command.ExecuteReader();
        return Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
    }

    private IOptions<MarketDataSettings> Settings(string daily, string? weekly = null, string? monthly = null) =>
        Options.Create(new MarketDataSettings
        {
            DailyDataPath = daily,
            WeeklyDataPath = weekly ?? daily + "_unused_w",
            MonthlyDataPath = monthly ?? daily + "_unused_m",
            MetadataPath = daily + "_unused_meta",
            TickerListPath = Path.Combine(_root, "tickers_" + Guid.NewGuid().ToString("N") + ".json"),
        });

    private ParquetDataService DataService(string daily) =>
        new(_db, Settings(daily), NullLogger<ParquetDataService>.Instance);

    private ParquetMarketDataProvider Provider(string daily) =>
        new(_db, new MockPythonServiceBase(), Settings(daily), NullLogger<ParquetMarketDataProvider>.Instance);

    private static List<decimal> ExpectedCloses() => Enumerable.Range(0, BarCount).Select(CloseAt).ToList();

    [Theory]
    [MemberData(nameof(VendorSuffixedSymbols))]
    public async Task ChartPath_ParquetDataService_ReadsAFileWhoseStemContainsAnEqualsSign(string symbol)
    {
        string daily = Folder("p1");
        string path = WriteDaily(daily, symbol);

        var candles = await DataService(daily).LoadCandlesAsync(symbol, TimeFrame.D1, BarCount * 2);

        Assert.Equal(BarCount, candles.Count);
        Assert.Equal(ExpectedCloses(), candles.Select(c => c.Close).ToList());
        Assert.Equal(CanonicalColumns, ColumnsOf(path)); // no Hive column ("CL", "GC", "HG")
    }

    [Theory]
    [MemberData(nameof(VendorSuffixedSymbols))]
    public async Task Provider_GetTickersDataAsyncAndLastUpdated_ReadTheVendorSuffixedFile(string symbol)
    {
        string daily = Folder("p2");
        WriteDaily(daily, symbol);
        var provider = Provider(daily);

        var candles = await provider.GetTickersDataAsync(symbol, TimeFrame.D1);
        var lastUpdated = await provider.GetTimeSeriesLastUpdatedAsync(symbol);

        Assert.Equal(BarCount, candles.Count);
        Assert.Equal(ExpectedCloses(), candles.Select(c => c.Close).ToList());
        Assert.NotNull(lastUpdated);
        Assert.Equal(Dates().Last().Date, lastUpdated!.Value.Date);
    }

    [Theory]
    [MemberData(nameof(VendorSuffixedSymbols))]
    public async Task Provider_GetLatestPricesAsync_FileListWithFilename_KeysByTheExactSymbol(string symbol)
    {
        string daily = Folder("p3");
        WriteDaily(daily, symbol);
        WriteDaily(daily, OtherSymbol);

        var prices = await Provider(daily).GetLatestPricesAsync(new[] { symbol, OtherSymbol });

        Assert.Equal(2, prices.Count);
        Assert.Equal(CloseAt(BarCount - 1), prices[symbol]); // key is "CL=F", not "CL=F.parquet"
        Assert.Equal(CloseAt(BarCount - 1), prices[OtherSymbol]);
    }

    [Theory]
    [MemberData(nameof(VendorSuffixedSymbols))]
    public async Task Provider_ScreenAsync_GlobWithFilename_FindsTheVendorSuffixedFileNextToAnOrdinaryOne(string symbol)
    {
        string daily = Folder("p4");
        WriteDaily(daily, symbol);
        WriteDaily(daily, OtherSymbol);
        var criteria = new ScreeningCriteria { Conditions = new IScreeningCondition[] { new RsiOversoldCondition(period: 2, threshold: 100m) } };

        var matched = await Provider(daily).ScreenAsync(criteria);

        Assert.Contains(symbol, matched);
        Assert.Contains(OtherSymbol, matched);
    }

    [Theory]
    [MemberData(nameof(VendorSuffixedSymbols))]
    public async Task TimeFrameGenerator_WeeklyAndMonthly_AreGeneratedFromAVendorSuffixedDailyFile(string symbol)
    {
        string daily = Folder("p5d"), weekly = Folder("p5w"), monthly = Folder("p5m");
        WriteDaily(daily, symbol);

        Assert.Equal(0, await new TimeFrameParquetGenerator(_db, Settings(daily, weekly, monthly)).GenerateAsync(symbol));

        foreach (var folder in new[] { weekly, monthly })
        {
            string generated = Path.Combine(folder, symbol + ".parquet");
            Assert.True(File.Exists(generated));
            Assert.Equal(CanonicalColumns, ColumnsOf(generated));
        }
    }

    [Theory]
    [MemberData(nameof(VendorSuffixedSymbols))]
    public void MetadataFileStem_WithAnEqualsSign_IsReadWithExactlyTheWrittenColumns(string symbol)
    {
        string folder = Folder("meta");
        string path = Path.Combine(folder, symbol + ".meta.parquet");
        using (var connection = new DuckDBConnection("DataSource=:memory:"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"COPY (SELECT 'Crude' AS short_name, 'N/A' AS sector, 'USD' AS currency, 1.5 AS current_price) TO '{ParquetMarketDataProvider.FormatDuckDbPath(path)}' (FORMAT PARQUET)";
            command.ExecuteNonQuery();
        }

        Assert.Equal(new[] { "short_name", "sector", "currency", "current_price" }, ColumnsOf(path));
    }

    [Theory]
    [MemberData(nameof(VendorSuffixedSymbols))]
    public void ResolveFilePath_ResolvesAVendorSuffixedStem(string symbol)
    {
        string daily = Folder("resolve");
        WriteDaily(daily, symbol);

        string? resolved = ParquetMarketDataProvider.ResolveFilePath(daily, symbol);

        Assert.NotNull(resolved);
        Assert.Equal(symbol + ".parquet", Path.GetFileName(resolved));
    }

    [Theory]
    [InlineData("CL=F", "CL=F")]
    [InlineData("cl=f", "CL=F")]
    [InlineData("jpy=x", "JPY=X")]
    [InlineData("EURUSD=X", "EURUSD=X")]
    public async Task MasterList_AddTickerAsync_StoresTheCanonicalVendorSuffixedSymbol(string typed, string expected)
    {
        string daily = Folder("master");
        var provider = Provider(daily);

        await provider.AddTickerAsync(typed);
        var tickers = await provider.GetAvailableTickersAsync();

        Assert.Contains(expected, tickers);
    }
}
