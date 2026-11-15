using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// WebAiExpansion plan 08, T2: a daily file that also holds the corporate-action columns (adj_close, dividends, stock_splits)
/// must give every reader exactly what the same bars in a six-column file give. adj_close is deliberately different from close,
/// so a reader that picked it up by position or by mistake would change <see cref="CandleData.Close"/>.
/// </summary>
public sealed class NineColumnDailyParquetReadPathTests : IDisposable
{
    private const string Symbol = "AAA";
    private const int BarCount = 70;
    private const decimal AdjCloseFactor = 0.9m;
    private static readonly string[] CanonicalColumns = { "date", "open", "high", "low", "close", "volume" };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_nine_col_" + Guid.NewGuid().ToString("N"));
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

    private static IEnumerable<(DateTime Date, decimal Close)> Bars()
    {
        var date = new DateTime(2024, 1, 1);
        int produced = 0;
        while (produced < BarCount)
        {
            if (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                yield return (date, 100m + produced * 0.37m);
                produced++;
            }
            date = date.AddDays(1);
        }
    }

    /// <summary>Writes <see cref="BarCount"/> bars (prices Float32, volume BIGINT); the nine-column form adds adj_close, dividends and stock_splits (Float64).</summary>
    private static void WriteDaily(string folder, string symbol, bool nineColumns)
    {
        var rows = new List<string>();
        int index = 0;
        foreach (var (date, close) in Bars())
        {
            string head = FormattableString.Invariant(
                $"DATE '{date:yyyy-MM-dd}', {close - 0.2m}, {close + 0.5m}, {close - 0.5m}, {close}, {1000 + index}");
            if (nineColumns)
            {
                // First half: stored before the columns existed (null = not fetched). Second half: fetched; one dividend, one split.
                string actions = index < BarCount / 2
                    ? "CAST(NULL AS DOUBLE), CAST(NULL AS DOUBLE)"
                    : FormattableString.Invariant($"{(index == 40 ? 0.25 : 0.0)}, {(index == 50 ? 10.0 : 0.0)}");
                string adj = FormattableString.Invariant($"CAST({close * AdjCloseFactor} AS DOUBLE)");
                rows.Add($"({head}, {adj}, {actions})");
            }
            else
            {
                rows.Add($"({head})");
            }
            index++;
        }

        string select = nineColumns
            ? "date, CAST(open AS FLOAT) AS open, CAST(high AS FLOAT) AS high, CAST(low AS FLOAT) AS low, CAST(close AS FLOAT) AS close, CAST(volume AS BIGINT) AS volume, adj_close, dividends, stock_splits"
            : "date, CAST(open AS FLOAT) AS open, CAST(high AS FLOAT) AS high, CAST(low AS FLOAT) AS low, CAST(close AS FLOAT) AS close, CAST(volume AS BIGINT) AS volume";
        string names = nineColumns
            ? "date, open, high, low, close, volume, adj_close, dividends, stock_splits"
            : "date, open, high, low, close, volume";
        string path = Path.Combine(folder, symbol + ".parquet").Replace('\\', '/');
        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"COPY (SELECT {select} FROM (VALUES {string.Join(",", rows)}) AS t({names})) TO '{path}' (FORMAT PARQUET)";
        command.ExecuteNonQuery();
    }

    private static (List<string> Columns, List<string> Rows) ReadAll(string path)
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM read_parquet('{path.Replace('\\', '/')}') ORDER BY date";
        using var reader = command.ExecuteReader();
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount)
                .Select(i => Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))));
        return (columns, rows);
    }

    // MarketDataSettings.Validate rejects two time frames sharing one folder, so unused ones get their own sibling folders.
    private static IOptions<MarketDataSettings> Settings(string daily, string? weekly = null, string? monthly = null) =>
        Options.Create(new MarketDataSettings
        {
            DailyDataPath = daily,
            WeeklyDataPath = weekly ?? daily + "_unused_w",
            MonthlyDataPath = monthly ?? daily + "_unused_m",
        });

    private ParquetDataService DataService(string daily, string? weekly = null, string? monthly = null) =>
        new(_db, Settings(daily, weekly, monthly), NullLogger<ParquetDataService>.Instance);

    private ParquetMarketDataProvider Provider(string daily) =>
        new(_db, new MockPythonServiceBase(), Settings(daily), NullLogger<ParquetMarketDataProvider>.Instance);

    [Fact]
    public async Task ParquetDataService_ReturnsTheSameDailyCandlesFromSixAndNineColumnFiles()
    {
        string six = Folder("six"), nine = Folder("nine");
        WriteDaily(six, Symbol, nineColumns: false);
        WriteDaily(nine, Symbol, nineColumns: true);

        var fromSix = await DataService(six).LoadCandlesAsync(Symbol, TimeFrame.D1, BarCount * 2);
        var fromNine = await DataService(nine).LoadCandlesAsync(Symbol, TimeFrame.D1, BarCount * 2);

        Assert.Equal(BarCount, fromSix.Count);
        Assert.Equal(fromSix, fromNine);
    }

    [Fact]
    public async Task ParquetMarketDataProvider_ReturnsTheSameCandlesAndNeverPicksUpAdjClose()
    {
        string six = Folder("six"), nine = Folder("nine");
        WriteDaily(six, Symbol, nineColumns: false);
        WriteDaily(nine, Symbol, nineColumns: true);

        var fromSix = await Provider(six).GetTickersDataAsync(Symbol, TimeFrame.D1);
        var fromNine = await Provider(nine).GetTickersDataAsync(Symbol, TimeFrame.D1);

        Assert.Equal(BarCount, fromSix.Count);
        Assert.Equal(fromSix, fromNine);
        // adj_close = close * 0.9 in the nine-column file; the indicator input stays close (owner rule: Close, never Adj Close).
        var expected = Bars().Select(b => TrainingSourceSnapshotProvider.ToDecimal((float)b.Close)).ToList();
        Assert.Equal(expected, fromNine.Select(c => c.Close).ToList());
    }

    [Fact]
    public async Task OneProviderReadsSixAndNineColumnFilesInAnyOrder_ColumnMapCacheStaysCorrect()
    {
        string mixed = Folder("mixed");
        WriteDaily(mixed, "SIX", nineColumns: false);
        WriteDaily(mixed, "NIN", nineColumns: true);
        var provider = Provider(mixed);

        var sixFirst = await provider.GetTickersDataAsync("SIX", TimeFrame.D1);
        var nineAfterSix = await provider.GetTickersDataAsync("NIN", TimeFrame.D1);
        var sixAfterNine = await provider.GetTickersDataAsync("SIX", TimeFrame.D1);
        var nineAgain = await provider.GetTickersDataAsync("NIN", TimeFrame.D1);

        Assert.Equal(BarCount, sixFirst.Count);
        Assert.Equal(sixFirst, nineAfterSix);
        Assert.Equal(sixFirst, sixAfterNine);
        Assert.Equal(sixFirst, nineAgain);
    }

    [Fact]
    public void RlLocalParquetReader_ReadsTheSameBarsDatesAndStorageDtype()
    {
        string six = Folder("six"), nine = Folder("nine");
        WriteDaily(six, Symbol, nineColumns: false);
        WriteDaily(nine, Symbol, nineColumns: true);
        var start = new DateOnly(2000, 1, 1);
        var end = new DateOnly(2100, 1, 1);
        var limits = RlTrainingValidation.FreezeLimits();

        var fromSix = RlLocalParquetReader.Read(Path.Combine(six, Symbol + ".parquet"), start, end, limits, CancellationToken.None);
        var fromNine = RlLocalParquetReader.Read(Path.Combine(nine, Symbol + ".parquet"), start, end, limits, CancellationToken.None);

        Assert.Equal(BarCount, fromSix.Bars.Length);
        Assert.Equal(fromSix.Bars, fromNine.Bars);
        Assert.Equal(fromSix.SourceRows, fromNine.SourceRows);
        Assert.Equal(fromSix.StorageDtype, fromNine.StorageDtype);
        Assert.Contains("close:FLOAT", fromNine.StorageDtype); // the price columns stay Float32 in the file
        Assert.Equal(
            RlLocalParquetReader.ReadDates(Path.Combine(six, Symbol + ".parquet"), end, limits, CancellationToken.None),
            RlLocalParquetReader.ReadDates(Path.Combine(nine, Symbol + ".parquet"), end, limits, CancellationToken.None));
    }

    [Fact]
    public async Task WeeklyAndMonthlyFilesGeneratedFromANineColumnDailyFileAreIdenticalAndStaySixColumns()
    {
        string sixDaily = Folder("six_d"), sixWeekly = Folder("six_w"), sixMonthly = Folder("six_m");
        string nineDaily = Folder("nine_d"), nineWeekly = Folder("nine_w"), nineMonthly = Folder("nine_m");
        WriteDaily(sixDaily, Symbol, nineColumns: false);
        WriteDaily(nineDaily, Symbol, nineColumns: true);

        Assert.Equal(0, await new TimeFrameParquetGenerator(_db, Settings(sixDaily, sixWeekly, sixMonthly)).GenerateAsync(Symbol));
        Assert.Equal(0, await new TimeFrameParquetGenerator(_db, Settings(nineDaily, nineWeekly, nineMonthly)).GenerateAsync(Symbol));

        foreach (var (sixFolder, nineFolder) in new[] { (sixWeekly, nineWeekly), (sixMonthly, nineMonthly) })
        {
            var expected = ReadAll(Path.Combine(sixFolder, Symbol + ".parquet"));
            var actual = ReadAll(Path.Combine(nineFolder, Symbol + ".parquet"));
            Assert.True(expected.Rows.Count > 1);
            Assert.Equal(expected.Rows, actual.Rows);
            Assert.Equal(CanonicalColumns, actual.Columns); // U-19=A: weekly/monthly files stay at the six canonical columns
        }
    }

    [Fact]
    public async Task ReGeneratingFromADailyFileWhoseOnlyPastRowsChangedRewritesWeeklyAndMonthly()
    {
        // The generator has no "did the daily file change" check: every run rebuilds from the whole daily file,
        // so a full-history re-fetch (split re-sync) reaches the weekly and monthly files without any marker file.
        string daily = Folder("d"), weekly = Folder("w"), monthly = Folder("m");
        WriteDaily(daily, Symbol, nineColumns: true);
        var generator = new TimeFrameParquetGenerator(_db, Settings(daily, weekly, monthly));
        Assert.Equal(0, await generator.GenerateAsync(Symbol));
        var before = ReadAll(Path.Combine(weekly, Symbol + ".parquet")).Rows;

        // Rewrite the daily file with the first half scaled by 10 (the last bar, i.e. the last date, is unchanged).
        string path = Path.Combine(daily, Symbol + ".parquet").Replace('\\', '/');
        using (var connection = new DuckDBConnection("DataSource=:memory:"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            string temp = path + ".rewrite";
            command.CommandText =
                $"COPY (SELECT date, CASE WHEN date < DATE '2024-02-01' THEN open*10 ELSE open END AS open, " +
                $"CASE WHEN date < DATE '2024-02-01' THEN high*10 ELSE high END AS high, " +
                $"CASE WHEN date < DATE '2024-02-01' THEN low*10 ELSE low END AS low, " +
                $"CASE WHEN date < DATE '2024-02-01' THEN close*10 ELSE close END AS close, volume, adj_close, dividends, stock_splits " +
                $"FROM read_parquet('{path}')) TO '{temp}' (FORMAT PARQUET)";
            command.ExecuteNonQuery();
            File.Move(temp.Replace('/', Path.DirectorySeparatorChar), path.Replace('/', Path.DirectorySeparatorChar), overwrite: true);
        }

        Assert.Equal(0, await generator.GenerateAsync(Symbol));
        var after = ReadAll(Path.Combine(weekly, Symbol + ".parquet")).Rows;

        Assert.Equal(before.Count, after.Count);
        Assert.NotEqual(before.First(), after.First()); // an old week changed
        Assert.Equal(before.Last(), after.Last());      // the last week did not
    }
}
