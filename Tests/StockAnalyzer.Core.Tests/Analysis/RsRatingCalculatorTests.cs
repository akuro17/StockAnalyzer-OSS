using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

public sealed class RsRatingCalculatorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DuckDBConnectionManager _dbManager;

    public RsRatingCalculatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RsRatingCalculatorTests_" + Guid.NewGuid().ToString("N"));
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
            // Best-effort cleanup of temp parquet fixtures.
        }
    }

    private void CreateParquetFile(string symbol, IReadOnlyList<(DateOnly Date, float? Close)> bars)
    {
        string filePath = Path.Combine(_tempDir, $"{symbol}.parquet").Replace("\\", "/");
        string escapedPath = ParquetMarketDataProvider.EscapeDuckDbPath(filePath);

        using var conn = new DuckDBConnection("DataSource=:memory:");
        conn.Open();
        using var cmd = conn.CreateCommand();

        var rows = new List<string>(bars.Count);
        for (int i = 0; i < bars.Count; i++)
        {
            (DateOnly date, float? close) = bars[i];
            string dateSql = $"'{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}'::DATE";
            string closeSql = close.HasValue
                ? $"{close.Value.ToString("R", CultureInfo.InvariantCulture)}::FLOAT"
                : "NULL::FLOAT";
            rows.Add($"({dateSql}, {closeSql}, {closeSql}, {closeSql}, {closeSql}, 1000::BIGINT)");
        }

        string tempTable = $"tmp_{Guid.NewGuid():N}";
        cmd.CommandText = $@"
            CREATE TEMPORARY TABLE {tempTable} (date DATE, open FLOAT, high FLOAT, low FLOAT, close FLOAT, volume BIGINT);
            INSERT INTO {tempTable} VALUES {string.Join(", ", rows)};
            COPY {tempTable} TO '{escapedPath}' (FORMAT PARQUET);
            DROP TABLE {tempTable};";
        cmd.ExecuteNonQuery();
    }

    private DuckDbHeatmapReturnProvider CreateProvider()
    {
        var settings = Options.Create(new MarketDataSettings { DailyDataPath = _tempDir });
        return new DuckDbHeatmapReturnProvider(_dbManager, settings, NullLogger<DuckDbHeatmapReturnProvider>.Instance);
    }

    private static RsRatingCloses FlatClosesWithLatest(decimal latest, decimal baseline = 100m)
        => new(latest, baseline, baseline, baseline, baseline);

    [Fact]
    public void Calculate_EmptyOrNull_ReturnsEmptyDictionary()
    {
        Assert.Empty(RsRatingCalculator.Calculate(null));
        Assert.Empty(RsRatingCalculator.Calculate(new Dictionary<string, RsRatingCloses>()));
    }

    [Fact]
    public void Calculate_SingleValidSymbol_Returns50()
    {
        var input = new Dictionary<string, RsRatingCloses>
        {
            ["ONLY"] = FlatClosesWithLatest(120m)
        };

        var ratings = RsRatingCalculator.Calculate(input);

        Assert.Single(ratings);
        Assert.Equal(50, ratings["ONLY"]);
    }

    [Fact]
    public void Calculate_TwoDistinctSymbols_Returns1And99()
    {
        var input = new Dictionary<string, RsRatingCloses>
        {
            ["LOW"] = FlatClosesWithLatest(90m),
            ["HIGH"] = FlatClosesWithLatest(150m)
        };

        var ratings = RsRatingCalculator.Calculate(input);

        Assert.Equal(2, ratings.Count);
        Assert.Equal(1, ratings["LOW"]);
        Assert.Equal(99, ratings["HIGH"]);
    }

    [Fact]
    public void Calculate_ThreeDistinctSymbols_Returns1And50And99()
    {
        var input = new Dictionary<string, RsRatingCloses>
        {
            ["MID"] = FlatClosesWithLatest(110m),
            ["TOP"] = FlatClosesWithLatest(140m),
            ["BOT"] = FlatClosesWithLatest(95m)
        };

        var ratings = RsRatingCalculator.Calculate(input);

        Assert.Equal(3, ratings.Count);
        Assert.Equal(1, ratings["BOT"]);
        Assert.Equal(50, ratings["MID"]);
        Assert.Equal(99, ratings["TOP"]);
    }

    [Fact]
    public void Calculate_100DistinctSymbols_SpanInclusive1To99Monotonically()
    {
        var input = new Dictionary<string, RsRatingCloses>();
        for (int i = 0; i < 100; i++)
        {
            // Strictly increasing latest price with fixed 100 baseline -> strictly increasing raw scores.
            input[$"SYM{i:D3}"] = FlatClosesWithLatest(100m + i);
        }

        var ratings = RsRatingCalculator.Calculate(input);

        Assert.Equal(100, ratings.Count);
        Assert.Equal(1, ratings["SYM000"]);
        Assert.Equal(99, ratings["SYM099"]);

        int prev = 0;
        for (int i = 0; i < 100; i++)
        {
            int rating = ratings[$"SYM{i:D3}"];
            Assert.InRange(rating, 1, 99);
            Assert.True(rating >= prev, $"Expected monotonic non-decreasing ratings at {i}: {prev} -> {rating}");
            prev = rating;
        }
    }

    [Fact]
    public void Calculate_TiedSymbols_ReceiveSameRatingRegardlessOfOrder()
    {
        var inputOrderA = new Dictionary<string, RsRatingCloses>
        {
            ["A"] = FlatClosesWithLatest(90m),
            ["TIE1"] = FlatClosesWithLatest(110m),
            ["TIE2"] = FlatClosesWithLatest(110m),
            ["D"] = FlatClosesWithLatest(140m)
        };

        var inputOrderB = new Dictionary<string, RsRatingCloses>
        {
            ["D"] = FlatClosesWithLatest(140m),
            ["TIE2"] = FlatClosesWithLatest(110m),
            ["A"] = FlatClosesWithLatest(90m),
            ["TIE1"] = FlatClosesWithLatest(110m)
        };

        var ratingsA = RsRatingCalculator.Calculate(inputOrderA);
        var ratingsB = RsRatingCalculator.Calculate(inputOrderB);

        // Ranks for [raw(90), raw(110), raw(110), raw(140)] with M=4:
        // rank(A)=1 -> 1; rank(TIE*)=2 -> round(1 + 98 * 1 / 3) = round(33.666...) = 34; rank(D)=4 -> 99
        Assert.Equal(1, ratingsA["A"]);
        Assert.Equal(34, ratingsA["TIE1"]);
        Assert.Equal(34, ratingsA["TIE2"]);
        Assert.Equal(99, ratingsA["D"]);

        foreach (string key in ratingsA.Keys)
        {
            Assert.Equal(ratingsA[key], ratingsB[key]);
        }
    }

    [Fact]
    public void Calculate_RecentQuarterWeight04ExceedsOlderQuarterWeight02()
    {
        // Both start from base 100 with latest C0 = 120:
        // - RECENT_STRONG dipped on C63 = 80 (+50% recent quarter, 0% earlier) -> raw = 0.4 * 0.5 = 0.20
        // - OLDER_STRONG dipped on C252 = 80 (+50% 4Q-back, 0% other 3)       -> raw = 0.2 * 0.5 = 0.10
        var input = new Dictionary<string, RsRatingCloses>
        {
            ["RECENT_STRONG"] = new RsRatingCloses(C0: 120m, C63: 80m, C126: 120m, C189: 120m, C252: 120m),
            ["OLDER_STRONG"] = new RsRatingCloses(C0: 120m, C63: 120m, C126: 120m, C189: 120m, C252: 80m)
        };

        var ratings = RsRatingCalculator.Calculate(input);

        Assert.Equal(99, ratings["RECENT_STRONG"]);
        Assert.Equal(1, ratings["OLDER_STRONG"]);
    }

    [Theory]
    [InlineData(0, 100, 100, 100, 100)]
    [InlineData(100, 0, 100, 100, 100)]
    [InlineData(100, 100, 0, 100, 100)]
    [InlineData(100, 100, 100, 0, 100)]
    [InlineData(100, 100, 100, 100, 0)]
    [InlineData(100, -5, 100, 100, 100)]
    public void Calculate_NonPositiveCloseInAnySlot_ExcludesSymbolAndShrinksUniverse(
        decimal c0, decimal c63, decimal c126, decimal c189, decimal c252)
    {
        var input = new Dictionary<string, RsRatingCloses>
        {
            ["BAD"] = new RsRatingCloses(c0, c63, c126, c189, c252),
            ["GOOD"] = FlatClosesWithLatest(110m)
        };

        var ratings = RsRatingCalculator.Calculate(input);

        Assert.DoesNotContain("BAD", ratings.Keys);
        // Only GOOD remains (M = 1), so GOOD receives 50.
        Assert.Single(ratings);
        Assert.Equal(50, ratings["GOOD"]);
    }

    [Fact]
    public void Calculate_DecimalOverflow_ExcludesOverflowingSymbol()
    {
        var input = new Dictionary<string, RsRatingCloses>
        {
            ["OVERFLOW"] = new RsRatingCloses(decimal.MaxValue, 0.0001m, 100m, 100m, 100m),
            ["VALID1"] = FlatClosesWithLatest(105m),
            ["VALID2"] = FlatClosesWithLatest(120m)
        };

        var ratings = RsRatingCalculator.Calculate(input);

        Assert.DoesNotContain("OVERFLOW", ratings.Keys);
        Assert.Equal(2, ratings.Count);
        Assert.Equal(1, ratings["VALID1"]);
        Assert.Equal(99, ratings["VALID2"]);
    }

    [Fact]
    public async Task DuckDbGetRsRatingClosesAsync_CountsNonNullClosesInReverseChronologicalOrderAndExcludesShortFiles()
    {
        // FULL has 260 non-null close bars plus 3 interleaved NULL close bars (263 total rows).
        // Index 0..259 in ascending chronological order; i-th non-null bar from end:
        //   latest (1st)   -> bar 259 -> close = 1000 + 259 = 1259
        //   64th from end  -> bar 259 - 63  = 196 -> 1196
        //   127th from end -> bar 259 - 126 = 133 -> 1133
        //   190th from end -> bar 259 - 189 = 70  -> 1070
        //   253rd from end -> bar 259 - 252 = 7   -> 1007
        var fullBars = new List<(DateOnly Date, float? Close)>();
        var baseDate = new DateOnly(2025, 1, 1);
        int nonNullIndex = 0;
        for (int row = 0; row < 263; row++)
        {
            DateOnly date = baseDate.AddDays(row);
            if (row == 50 || row == 150 || row == 240)
            {
                fullBars.Add((date, null));
            }
            else
            {
                fullBars.Add((date, 1000f + nonNullIndex));
                nonNullIndex++;
            }
        }
        Assert.Equal(260, nonNullIndex);

        // EXACT253 has exactly 253 non-null bars (200..452).
        var exactBars = new List<(DateOnly Date, float? Close)>(253);
        for (int i = 0; i < 253; i++)
        {
            exactBars.Add((baseDate.AddDays(i), 200f + i));
        }

        // SHORT252 has 252 non-null bars plus 2 NULL bars (must be excluded because < 253 non-null closes).
        var shortBars = new List<(DateOnly Date, float? Close)>(254);
        for (int i = 0; i < 252; i++)
        {
            shortBars.Add((baseDate.AddDays(i), 500f + i));
        }
        shortBars.Add((baseDate.AddDays(252), null));
        shortBars.Add((baseDate.AddDays(253), null));

        // ZERO_CLOSE has 253 non-null bars where C252 is 0; retrieval keeps it, calculator excludes it.
        var zeroCloseBars = new List<(DateOnly Date, float? Close)>(253);
        for (int i = 0; i < 253; i++)
        {
            zeroCloseBars.Add((baseDate.AddDays(i), i == 0 ? 0f : 300f + i));
        }

        CreateParquetFile("FULL", fullBars);
        CreateParquetFile("EXACT253", exactBars);
        CreateParquetFile("SHORT252", shortBars);
        CreateParquetFile("ZERO_CLOSE", zeroCloseBars);

        var provider = CreateProvider();
        var closes = await provider.GetRsRatingClosesAsync(
            new[] { "FULL", "EXACT253", "SHORT252", "ZERO_CLOSE", "MISSING_TICKER" });

        Assert.Equal(3, closes.Count);
        Assert.DoesNotContain("SHORT252", closes.Keys);
        Assert.DoesNotContain("MISSING_TICKER", closes.Keys);

        Assert.Equal(new RsRatingCloses(1259m, 1196m, 1133m, 1070m, 1007m), closes["FULL"]);
        Assert.Equal(new RsRatingCloses(452m, 389m, 326m, 263m, 200m), closes["EXACT253"]);
        Assert.Equal(new RsRatingCloses(552m, 489m, 426m, 363m, 0m), closes["ZERO_CLOSE"]);

        // End-to-end check with RsRatingCalculator:
        // FULL raw: 0.4*(1259/1196-1) + 0.2*(1259/1133-1) + 0.2*(1259/1070-1) + 0.2*(1259/1007-1) ≈ 0.12867
        // EXACT253 raw: 0.4*(452/389-1) + 0.2*(452/326-1) + 0.2*(452/263-1) + 0.2*(452/200-1) ≈ 0.53780
        // ZERO_CLOSE is excluded by calculator because C252 == 0.
        var ratings = RsRatingCalculator.Calculate(closes);
        Assert.Equal(2, ratings.Count);
        Assert.Equal(1, ratings["FULL"]);
        Assert.Equal(99, ratings["EXACT253"]);
        Assert.DoesNotContain("ZERO_CLOSE", ratings.Keys);
    }

    [Fact]
    public async Task DuckDbGetRsRatingClosesAsync_EmptyOrCancelled_HandlesCleanly()
    {
        var provider = CreateProvider();

        Assert.Empty(await provider.GetRsRatingClosesAsync(Array.Empty<string>()));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            provider.GetRsRatingClosesAsync(new[] { "AAPL" }, cts.Token));
    }
}
