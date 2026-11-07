using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.MathUtils;

/// <summary>
/// Plan tasks T5/T6: W1 and MN1 bars of different markets are matched by period (a bar is dated by its first trading day, so a holiday gives the same
/// period two different dates), and the result states when returns of different holding periods are paired.
/// </summary>
public class ReturnStatisticsPeriodAlignmentTests
{
    private static readonly DateTime FirstMonday = new(2024, 1, 1);

    private static CandleData Bar(DateTime date, decimal close) => new(date, close, close, close, close, 1000);

    private static decimal PriceA(int i) => 100m + 3m * i + (i * i % 5);

    private static decimal PriceB(int i) => 50m + 2m * i + (i * 7 % 4);

    private static IReadOnlyDictionary<string, IReadOnlyList<CandleData>> Universe(IReadOnlyList<CandleData> a, IReadOnlyList<CandleData> b)
        => new Dictionary<string, IReadOnlyList<CandleData>> { ["A"] = a, ["B"] = b };

    /// <summary>Eight weekly bars; the label of week <paramref name="shiftedWeek"/> is the Tuesday (the Monday was a holiday), all others the Monday.</summary>
    private static List<CandleData> Weekly(Func<int, decimal> price, int shiftedWeek = -1, int skippedWeek = -1)
    {
        var bars = new List<CandleData>();
        for (int week = 0; week < 8; week++)
        {
            if (week == skippedWeek)
            {
                continue;
            }

            bars.Add(Bar(FirstMonday.AddDays(7 * week + (week == shiftedWeek ? 1 : 0)), price(week)));
        }

        return bars;
    }

    /// <summary>Eight monthly bars from January 2024; month <paramref name="shiftedMonth"/> is dated the 2nd instead of the 1st.</summary>
    private static List<CandleData> Monthly(Func<int, decimal> price, int shiftedMonth = -1)
    {
        var bars = new List<CandleData>();
        for (int month = 0; month < 8; month++)
        {
            bars.Add(Bar(new DateTime(2024, 1 + month, month == shiftedMonth ? 2 : 1), price(month)));
        }

        return bars;
    }

    [Fact]
    public void W1_MatchesTheSameWeekDatedByDifferentFirstTradingDays()
    {
        var universe = Universe(Weekly(PriceA), Weekly(PriceB, shiftedWeek: 3));

        CrossAssetStatsResult w1 = ReturnStatistics.Calculate(universe, timeFrame: TimeFrame.W1);

        // Returns of weeks 1..7 are all common, the week that B dated on the Tuesday included.
        Assert.Equal(7, w1.CommonDateCount);
        Assert.Equal(new DateTime(2024, 1, 8), w1.StartDate);
        Assert.Equal(FirstMonday.AddDays(49), w1.EndDate);
        Assert.NotNull(w1.CorrelationMatrix[0][1]);
        Assert.Contains(ReturnStatistics.PeriodStartDateNote, w1.Notes);

        // The default (D1) keeps the exact-date match, so the shifted week drops out: this is what W1 used to do.
        CrossAssetStatsResult exact = ReturnStatistics.Calculate(universe);
        Assert.Equal(6, exact.CommonDateCount);
        Assert.DoesNotContain(ReturnStatistics.PeriodStartDateNote, exact.Notes);
    }

    [Fact]
    public void W1_WithoutHolidays_EqualsTheDefaultExactDateResult()
    {
        var universe = Universe(Weekly(PriceA), Weekly(PriceB));

        CrossAssetStatsResult w1 = ReturnStatistics.Calculate(universe, includeCovariance: true, timeFrame: TimeFrame.W1);
        CrossAssetStatsResult exact = ReturnStatistics.Calculate(universe, includeCovariance: true);

        Assert.Equal(exact.CommonDateCount, w1.CommonDateCount);
        Assert.Equal(exact.StartDate, w1.StartDate);
        Assert.Equal(exact.EndDate, w1.EndDate);
        Assert.Equal(exact.CorrelationMatrix[0][1], w1.CorrelationMatrix[0][1]);
        Assert.Equal(exact.CovarianceMatrix![0][1], w1.CovarianceMatrix![0][1]);
    }

    [Fact]
    public void MN1_MatchesTheSameMonthDatedByDifferentFirstTradingDays_AndBenchmarkToo()
    {
        var universe = Universe(Monthly(PriceA), Monthly(PriceB, shiftedMonth: 2));

        CrossAssetStatsResult mn1 = ReturnStatistics.Calculate(universe, benchmarkSymbol: "A", timeFrame: TimeFrame.MN1);

        Assert.Equal(7, mn1.CommonDateCount);
        Assert.Equal(new DateTime(2024, 2, 1), mn1.StartDate);
        Assert.Equal(new DateTime(2024, 8, 1), mn1.EndDate);
        Assert.Equal(2, mn1.BetaAlpha.Count);
        Assert.Contains(ReturnStatistics.PeriodStartDateNote, mn1.Notes);
        Assert.Equal(6, ReturnStatistics.Calculate(universe, benchmarkSymbol: "A").CommonDateCount);
    }

    [Fact]
    public void D1_ExplicitTimeFrame_EqualsTheDefault()
    {
        var universe = Universe(Weekly(PriceA, shiftedWeek: 2), Weekly(PriceB));

        CrossAssetStatsResult explicitD1 = ReturnStatistics.Calculate(universe, includeCovariance: true, timeFrame: TimeFrame.D1);
        CrossAssetStatsResult defaulted = ReturnStatistics.Calculate(universe, includeCovariance: true);

        Assert.Equal(defaulted.CommonDateCount, explicitD1.CommonDateCount);
        Assert.Equal(defaulted.CorrelationMatrix[0][1], explicitD1.CorrelationMatrix[0][1]);
        Assert.Equal(defaulted.MultiBarReturnCount, explicitD1.MultiBarReturnCount);
        Assert.Equal(defaulted.Notes, explicitD1.Notes);
    }

    // ---- multiBarReturnCount (R-2) ----------------------------------------------------------------------------------------------

    private static List<CandleData> Days(Func<int, decimal> price, params int[] dayNumbers)
        => dayNumbers.Select(n => Bar(FirstMonday.AddDays(n - 1), price(n))).ToList();

    [Fact]
    public void MultiBarReturnCount_CountsTheDateAfterADayOnlyOneMarketTradedOn()
    {
        // B is closed on day 3. Common return dates are 2, 4, 5; on day 4 A's return covers one day, B's two.
        CrossAssetStatsResult result = ReturnStatistics.Calculate(Universe(Days(PriceA, 1, 2, 3, 4, 5), Days(PriceB, 1, 2, 4, 5)));

        Assert.Equal(3, result.CommonDateCount);
        Assert.Equal(1, result.MultiBarReturnCount);
        Assert.Contains(result.Notes, n => n.StartsWith("1 of 3 common return dates combine returns over different holding periods", StringComparison.Ordinal));
    }

    [Fact]
    public void MultiBarReturnCount_IsZeroWhenBothMarketsTradeTheSameDays_EvenAcrossAWeekend()
    {
        // Monday to Friday of two weeks for both; Friday -> Monday is three calendar days for both series, not a mismatch.
        int[] weekdays = { 1, 2, 3, 4, 5, 8, 9, 10, 11, 12 };
        CrossAssetStatsResult result = ReturnStatistics.Calculate(Universe(Days(PriceA, weekdays), Days(PriceB, weekdays)));

        Assert.Equal(9, result.CommonDateCount);
        Assert.Equal(0, result.MultiBarReturnCount);
        Assert.DoesNotContain(result.Notes, n => n.Contains("different holding periods", StringComparison.Ordinal));
    }

    [Fact]
    public void MultiBarReturnCount_CountsTheFirstCommonDateWhenThePreviousBarsDiffer()
    {
        // A trades days 1..5, B days 1,3,4,5: common return dates 3,4,5. On day 3 A's previous bar is day 2, B's day 1.
        CrossAssetStatsResult result = ReturnStatistics.Calculate(Universe(Days(PriceA, 1, 2, 3, 4, 5), Days(PriceB, 1, 3, 4, 5)));

        Assert.Equal(3, result.CommonDateCount);
        Assert.Equal(1, result.MultiBarReturnCount);
    }

    [Fact]
    public void MultiBarReturnCount_ComparesPeriodsForW1()
    {
        // B has no bar for week 3 (index 2): common return weeks are 1, 3, 4, 5, 6, 7 -> only week 3 (previous bars: A week 2, B week 1) is mixed.
        CrossAssetStatsResult result = ReturnStatistics.Calculate(Universe(Weekly(PriceA), Weekly(PriceB, skippedWeek: 2)), timeFrame: TimeFrame.W1);

        Assert.Equal(6, result.CommonDateCount);
        Assert.Equal(1, result.MultiBarReturnCount);
    }

    [Fact]
    public void MultiBarReturnCount_IncludesAnExternalBenchmark()
    {
        // The universe trades every day; the external benchmark is closed on day 3.
        CrossAssetStatsResult result = ReturnStatistics.Calculate(
            Universe(Days(PriceA, 1, 2, 3, 4, 5), Days(PriceB, 1, 2, 3, 4, 5)),
            benchmarkSymbol: "BENCH",
            benchmarkCandles: Days(n => 10m + n * n % 7 + n, 1, 2, 4, 5));

        Assert.Equal(3, result.CommonDateCount);
        Assert.Equal(1, result.MultiBarReturnCount);
    }
}
