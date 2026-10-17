#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Core.Models.StoredPeriod;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// The sample estimate and the sufficiency verdict of the Training Wizard warnings. The estimate is checked against what the real Python trainer functions count
/// (Tests/TrainingPopulationParity.json, written by generate_training_population_parity.py) and against pinned values measured on the stored data.
/// </summary>
public sealed class TrainingDataSufficiencyTests
{
    private const int Window = 60, Horizon = 5, Splits = 5, DefaultRequired = 2374, Cap = 10_000;
    private static readonly DateOnly Start = new(2017, 5, 1);

    private static SymbolBarInfo Info(string symbol, int inRange, int? upToEnd = null, int required = DefaultRequired, int? fromMinimum = null)
    {
        int total = upToEnd ?? inRange;
        return new SymbolBarInfo(symbol, inRange, total, total >= required ? Start : null, fromMinimum ?? (total >= required ? inRange : null));
    }

    private static IReadOnlyList<SymbolBarInfo> Tickers(int count, int bars = DefaultRequired)
        => Enumerable.Range(0, count).Select(i => Info("T" + i, bars)).ToArray();

    private static TrainingSufficiencyResult Evaluate(IReadOnlyList<SymbolBarInfo> infos, bool retained = false, int cap = Cap, int maxLag = 0)
        => TrainingDataSufficiency.Evaluate(infos, DefaultRequired, Window, Horizon, maxLag, Splits, retained, cap);

    // ----- the estimate -----

    [Theory]
    [InlineData(2374, false, 1925)]   // 2310 windows: 5 * (2310 / 6); the trainer counted 1861 on the stored data
    [InlineData(2374, true, 2310)]
    [InlineData(0, false, 0)]
    [InlineData(-5, true, 0)]
    [InlineData(64, false, 0)]        // window + horizon - 1 bars give no window
    [InlineData(65, true, 1)]
    public void TheEstimateOfOneSymbolIsPinned(int bars, bool retained, long expected)
        => Assert.Equal(expected, TrainingDataSufficiency.EstimateSamples(bars, Window, Horizon, 0, Splits, retained));

    [Fact]
    public void ALagRemovesItsWindowsAndASplitCountBelowOneIsRefused()
    {
        Assert.Equal(2310 - 8, TrainingDataSufficiency.EstimateSamples(2374, Window, Horizon, 8, Splits, retained: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrainingDataSufficiency.EstimateSamples(2374, Window, Horizon, 0, 0, false));
    }

    [Fact]
    public void TheEstimateEqualsThePythonFigureAndNeverFallsBelowWhatTheTrainerCounts()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StockAnalyzer.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "Tests", "TrainingPopulationParity.json")));
        var rows = document.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        Assert.True(rows.Length > 500, "the parity file is missing rows; regenerate it with generate_training_population_parity.py");
        foreach (var row in rows)
        {
            int bars = row.GetProperty("bars").GetInt32(), window = row.GetProperty("window").GetInt32(), horizon = row.GetProperty("horizon").GetInt32();
            int splits = row.GetProperty("splits").GetInt32(), maxLag = row.GetProperty("max_lag").GetInt32();
            bool retained = row.GetProperty("retained").GetBoolean();
            long real = row.GetProperty("real").GetInt64(), expected = row.GetProperty("estimate").GetInt64();
            long actual = TrainingDataSufficiency.EstimateSamples(bars, window, horizon, maxLag, splits, retained);
            string at = $"bars={bars} window={window} horizon={horizon} splits={splits} lag={maxLag} retained={retained}";
            Assert.True(expected == actual, at + $": expected {expected}, got {actual}");
            Assert.True(actual >= real, at + $": estimate {actual} is below the real {real}");
            if (retained) Assert.True(actual == real, at + $": retained mode must be exact ({actual} vs {real})");
        }
    }

    // ----- the verdict -----

    [Theory]
    [InlineData(4, false, 7_700, false)]
    [InlineData(5, false, 9_625, false)]
    [InlineData(6, false, 11_550, true)]
    [InlineData(12, false, 23_100, true)]     // the 12-ticker run that the trainer refused
    [InlineData(4, true, 9_240, false)]
    [InlineData(5, true, 11_550, true)]
    public void TheTotalIsComparedWithTheCapStrictly(int count, bool retained, long total, bool over)
    {
        var result = Evaluate(Tickers(count), retained);
        Assert.Equal(total, result.TotalEstimate);
        Assert.Equal(over, result.OverLimit);
        Assert.Equal(count, result.TickerCount);
    }

    [Fact]
    public void ATotalEqualToTheCapIsNotOver_AndOneAboveIs()
    {
        var infos = Tickers(1);   // 1925
        Assert.False(Evaluate(infos, cap: 1925).OverLimit);
        Assert.True(Evaluate(infos, cap: 1924).OverLimit);
    }

    [Theory]
    [InlineData(false, Cap, 5)]
    [InlineData(true, Cap, 4)]
    [InlineData(false, 1900, 0)]
    [InlineData(false, 1925, 1)]
    [InlineData(false, 3849, 1)]
    [InlineData(false, 3850, 2)]
    public void TheMostTickersThatFitAtTheShortestPeriodIsTheCapOverOneSymbolsEstimate(bool retained, int cap, int expected)
    {
        var result = Evaluate(Tickers(3), retained, cap);
        Assert.Equal(expected, result.MaxTickers);
        Assert.Equal(retained ? 2310 : 1925, result.EstimateAtRequiredBars);
    }

    [Fact]
    public void ARequirementThatYieldsNoWindowHasNoTickerLimit()
    {
        var result = TrainingDataSufficiency.Evaluate(Tickers(2, 10), requiredBars: 10, Window, Horizon, 0, Splits, retained: false, Cap);
        Assert.Equal(0, result.EstimateAtRequiredBars);
        Assert.Null(result.MaxTickers);
        Assert.False(result.OverLimit);
    }

    [Fact]
    public void ShortSymbolsAreOrderedFewestBarsFirstThenByOrdinalName_AndUnreachableOnesAreAlsoShort()
    {
        var infos = new[]
        {
            Info("ZZZ", 100, upToEnd: 5000),   // short in the period, but enough history before the start
            Info("BBB", 100, upToEnd: 100),    // nothing more exists: unreachable
            Info("AAA", 100, upToEnd: 100),
            Info("MMM", 500, upToEnd: 3000),
            Info("OK", 2400),
        };
        var result = Evaluate(infos);
        Assert.Equal(new[] { "AAA", "BBB", "ZZZ", "MMM" }, result.ShortSymbols.Select(i => i.Symbol));
        Assert.Equal(new[] { "AAA", "BBB" }, result.Unreachable.Select(i => i.Symbol));
        Assert.All(result.Unreachable, u => Assert.Contains(u, result.ShortSymbols));
    }

    [Fact]
    public void TheMinimumStartIsTheEarliestRequiredStart_AndTheTotalAtItNeedsEverySymbolToReachTheRequirement()
    {
        var early = new SymbolBarInfo("A", 3000, 3000, new DateOnly(2016, 1, 4), 3000);
        var late = new SymbolBarInfo("B", 2400, 2400, new DateOnly(2017, 3, 1), 2500);
        var reachable = Evaluate(new[] { early, late });
        Assert.Equal(new DateOnly(2016, 1, 4), reachable.MinimumStart);
        Assert.Equal(TrainingDataSufficiency.EstimateSamples(3000, Window, Horizon, 0, Splits, false)
                     + TrainingDataSufficiency.EstimateSamples(2500, Window, Horizon, 0, Splits, false), reachable.TotalAtMinimumStart);

        var withUnreachable = Evaluate(new[] { early, new SymbolBarInfo("C", 100, 100, null, 100) });
        Assert.Equal(new DateOnly(2016, 1, 4), withUnreachable.MinimumStart);
        Assert.Null(withUnreachable.TotalAtMinimumStart);

        var none = Evaluate(new[] { new SymbolBarInfo("C", 100, 100, null, null) });
        Assert.Null(none.MinimumStart);
        Assert.Null(none.TotalAtMinimumStart);
    }

    [Fact]
    public void NoSymbolsGiveAnEmptyVerdict()
    {
        var result = Evaluate(Array.Empty<SymbolBarInfo>());
        Assert.Empty(result.ShortSymbols);
        Assert.Empty(result.Unreachable);
        Assert.False(result.OverLimit);
        Assert.Equal(0, result.TotalEstimate);
        Assert.Equal(0, result.TickerCount);
        Assert.Null(result.MinimumStart);
    }
}
