using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using Xunit;
using static StockAnalyzer.Core.Tests.TestHelpers.DayNumberedBars;

namespace StockAnalyzer.Core.Tests.MathUtils;

/// <summary>
/// HoldingPeriodAlignmentMode: IncludeAll keeps every common return date (the standard, unchanged output); StrictEqualHoldingPeriod leaves out the dates whose returns
/// cover different holding periods in the series (a market traded on a day another was closed) and calculates on the rest.
/// </summary>
public class HoldingPeriodAlignmentModeTests
{
    private static decimal Return(Func<int, decimal> price, int fromDay, int toDay) => (price(toDay) - price(fromDay)) / price(fromDay);

    // A trades days 1..8, B is closed on day 3. Common return dates 2, 4, 5, 6, 7, 8; only day 4 is mixed (A's return covers day 3 -> 4, B's day 2 -> 4).
    private static IReadOnlyDictionary<string, IReadOnlyList<CandleData>> OneClosedDay()
        => Universe(Days(PriceA, 1, 2, 3, 4, 5, 6, 7, 8), Days(PriceB, 1, 2, 4, 5, 6, 7, 8));

    [Fact]
    public void IncludeAll_IsTheDefault_AndKeepsEveryCommonDate()
    {
        CrossAssetStatsResult defaulted = ReturnStatistics.Calculate(OneClosedDay());
        CrossAssetStatsResult explicitMode = ReturnStatistics.Calculate(OneClosedDay(), holdingPeriodMode: HoldingPeriodAlignmentMode.IncludeAll);

        Assert.Equal(HoldingPeriodAlignmentMode.IncludeAll, defaulted.HoldingPeriodMode);
        Assert.Equal(6, defaulted.CommonDateCount);
        Assert.Equal(1, defaulted.MultiBarReturnCount);
        Assert.Equal(0, defaulted.ExcludedMultiBarDateCount);
        Assert.Equal(defaulted.CorrelationMatrix[0][1], explicitMode.CorrelationMatrix[0][1]);
        Assert.Equal(defaulted.Notes, explicitMode.Notes);
        Assert.Contains(defaulted.Notes, n => n.StartsWith("1 of 6 common return dates combine returns over different holding periods", StringComparison.Ordinal));
        Assert.Contains(defaulted.Notes, n => n.EndsWith("They are used unchanged in the correlation, covariance, beta and alpha.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HoldingPeriodAlignmentMode.IncludeAll)]
    [InlineData(HoldingPeriodAlignmentMode.StrictEqualHoldingPeriod)]
    public void TheResultNamesTheRequestedMode_OnEveryReturnPath(HoldingPeriodAlignmentMode mode)
    {
        // Fewer than two symbols with returns: the early return before any common date is looked for.
        var oneSymbol = new Dictionary<string, IReadOnlyList<CandleData>> { ["A"] = Days(PriceA, 1, 2, 3, 4, 5) };
        // Two symbols with fewer than MinCommonDates common return dates (days 2 and 3 only).
        var fewCommonDates = Universe(Days(PriceA, 1, 2, 3), Days(PriceB, 1, 2, 3));

        Assert.Equal(mode, ReturnStatistics.Calculate(oneSymbol, holdingPeriodMode: mode).HoldingPeriodMode);
        Assert.Equal(mode, ReturnStatistics.Calculate(fewCommonDates, holdingPeriodMode: mode).HoldingPeriodMode);
        Assert.Equal(mode, ReturnStatistics.Calculate(OneClosedDay(), holdingPeriodMode: mode).HoldingPeriodMode);
    }

    [Fact]
    public void StrictEqualHoldingPeriod_LeavesOutTheMixedDate_AndMatchesTheHandCalculation()
    {
        CrossAssetStatsResult strict = ReturnStatistics.Calculate(OneClosedDay(), includeCovariance: true, holdingPeriodMode: HoldingPeriodAlignmentMode.StrictEqualHoldingPeriod);

        Assert.Equal(HoldingPeriodAlignmentMode.StrictEqualHoldingPeriod, strict.HoldingPeriodMode);
        Assert.Equal(5, strict.CommonDateCount);
        Assert.Equal(1, strict.MultiBarReturnCount);
        Assert.Equal(1, strict.ExcludedMultiBarDateCount);
        Assert.Equal(new[] { 2, 5, 6, 7, 8 }.Select(d => Day1.AddDays(d - 1)).ToArray(), strict.CommonDates.ToArray());
        Assert.Equal(Day1.AddDays(1), strict.StartDate);
        Assert.Equal(Day1.AddDays(7), strict.EndDate);

        // Day 2: both 1 -> 2. Days 5..8: both one bar back. (Day 4 is left out: A 3 -> 4, B 2 -> 4.)
        decimal[] expectedA = { Return(PriceA, 1, 2), Return(PriceA, 4, 5), Return(PriceA, 5, 6), Return(PriceA, 6, 7), Return(PriceA, 7, 8) };
        decimal[] expectedB = { Return(PriceB, 1, 2), Return(PriceB, 4, 5), Return(PriceB, 5, 6), Return(PriceB, 6, 7), Return(PriceB, 7, 8) };
        Assert.Equal(expectedA, strict.AlignedReturns[0]);
        Assert.Equal(expectedB, strict.AlignedReturns[1]);

        double meanA = expectedA.Average(v => (double)v);
        double meanB = expectedB.Average(v => (double)v);
        double cov = expectedA.Zip(expectedB, (a, b) => ((double)a - meanA) * ((double)b - meanB)).Sum() / 4.0;
        double varA = expectedA.Sum(a => Math.Pow((double)a - meanA, 2)) / 4.0;
        double varB = expectedB.Sum(b => Math.Pow((double)b - meanB, 2)) / 4.0;
        Assert.Equal(cov / Math.Sqrt(varA * varB), strict.CorrelationMatrix[0][1]!.Value, 9);
        Assert.Equal(cov, strict.CovarianceMatrix![0][1]!.Value, 12);

        Assert.Contains(strict.Notes, n => n.StartsWith("1 of 6 common return dates were left out because they combine returns over different holding periods", StringComparison.Ordinal));
        Assert.DoesNotContain(strict.Notes, n => n.Contains("used unchanged", StringComparison.Ordinal));
    }

    [Fact]
    public void StrictEqualHoldingPeriod_GivesOtherStatisticsThanIncludeAll_WhenADateIsMixed()
    {
        CrossAssetStatsResult all = ReturnStatistics.Calculate(OneClosedDay());
        CrossAssetStatsResult strict = ReturnStatistics.Calculate(OneClosedDay(), holdingPeriodMode: HoldingPeriodAlignmentMode.StrictEqualHoldingPeriod);

        Assert.NotEqual(all.CorrelationMatrix[0][1], strict.CorrelationMatrix[0][1]);
    }

    [Fact]
    public void BothModes_AreIdentical_WhenEveryDateCoversTheSameHoldingPeriod()
    {
        // Both markets trade the same days (a weekend between day 5 and day 8 is the same gap for both).
        int[] sameDays = { 1, 2, 3, 4, 5, 8, 9, 10, 11, 12 };
        var universe = Universe(Days(PriceA, sameDays), Days(PriceB, sameDays));

        CrossAssetStatsResult all = ReturnStatistics.Calculate(universe, includeCovariance: true);
        CrossAssetStatsResult strict = ReturnStatistics.Calculate(universe, includeCovariance: true, holdingPeriodMode: HoldingPeriodAlignmentMode.StrictEqualHoldingPeriod);

        Assert.Equal(0, strict.ExcludedMultiBarDateCount);
        Assert.Equal(all.CommonDateCount, strict.CommonDateCount);
        Assert.Equal(all.CommonDates, strict.CommonDates);
        Assert.Equal(all.CorrelationMatrix[0][1], strict.CorrelationMatrix[0][1]);
        Assert.Equal(all.CovarianceMatrix![0][1], strict.CovarianceMatrix![0][1]);
        Assert.Equal(all.Notes, strict.Notes);
    }

    [Fact]
    public void StrictEqualHoldingPeriod_TheFirstCommonDateIsMixedWhenThePreviousBarsDiffer_AndLeavingItOutCanLeaveTooFewDates()
    {
        // A trades days 1..5, B days 1, 3, 4, 5: common return dates 3, 4, 5. On day 3 A's previous bar is day 2, B's day 1 (mixed); days 4 and 5 are not.
        var universe = Universe(Days(PriceA, 1, 2, 3, 4, 5), Days(PriceB, 1, 3, 4, 5));

        CrossAssetStatsResult all = ReturnStatistics.Calculate(universe);
        CrossAssetStatsResult strict = ReturnStatistics.Calculate(universe, holdingPeriodMode: HoldingPeriodAlignmentMode.StrictEqualHoldingPeriod);

        Assert.Equal(3, all.CommonDateCount);
        Assert.Equal(2, strict.CommonDateCount);
        Assert.True(strict.CommonDateCount < ReturnStatistics.MinCommonDates);
        Assert.Equal(1, strict.MultiBarReturnCount);
        Assert.Equal(1, strict.ExcludedMultiBarDateCount);
        Assert.Equal(Day1.AddDays(3), strict.StartDate);
        Assert.Equal(Day1.AddDays(4), strict.EndDate);
        Assert.Empty(strict.CorrelationMatrix);
        Assert.Empty(strict.AlignedReturns);
    }

    [Fact]
    public void StrictEqualHoldingPeriod_ChoosesTheDatesOnceOnTheCommonDates_NotAgainOnTheRemainingOnes()
    {
        // B is closed on days 3 and 6. Common return dates 2, 4, 5, 7, 8. Mixed: 4 (A 3 -> 4, B 2 -> 4) and 7 (A 6 -> 7, B 5 -> 7); 5 and 8 are plain.
        // Left out one date at a time, day 5 would see day 2 as its previous common date; chosen once, day 5 (previous common date 4, both series' previous bar 4) stays.
        var universe = Universe(Days(PriceA, 1, 2, 3, 4, 5, 6, 7, 8), Days(PriceB, 1, 2, 4, 5, 7, 8));

        CrossAssetStatsResult strict = ReturnStatistics.Calculate(universe, holdingPeriodMode: HoldingPeriodAlignmentMode.StrictEqualHoldingPeriod);

        Assert.Equal(2, strict.MultiBarReturnCount);
        Assert.Equal(2, strict.ExcludedMultiBarDateCount);
        Assert.Equal(new[] { 2, 5, 8 }.Select(d => Day1.AddDays(d - 1)).ToArray(), strict.CommonDates.ToArray());
    }

    [Fact]
    public void StrictEqualHoldingPeriod_IncludesAnExternalBenchmark_InTheChoiceOfDatesAndInTheRegression()
    {
        // The universe trades days 1..6; the external benchmark is closed on day 3: common dates 2, 4, 5, 6; day 4 is mixed (A 3 -> 4, benchmark 2 -> 4).
        var universe = Universe(Days(PriceA, 1, 2, 3, 4, 5, 6), Days(PriceB, 1, 2, 3, 4, 5, 6));
        List<CandleData> benchmark = Days(n => 10m + n * n % 7 + n, 1, 2, 4, 5, 6);

        CrossAssetStatsResult strict = ReturnStatistics.Calculate(
            universe, benchmarkSymbol: "BENCH", benchmarkCandles: benchmark, holdingPeriodMode: HoldingPeriodAlignmentMode.StrictEqualHoldingPeriod);

        Assert.Equal(3, strict.CommonDateCount);
        Assert.Equal(1, strict.ExcludedMultiBarDateCount);
        Assert.Equal(new[] { 2, 5, 6 }.Select(d => Day1.AddDays(d - 1)).ToArray(), strict.CommonDates.ToArray());
        Assert.Equal(3, strict.BenchmarkAlignedReturns!.Length);
        Assert.All(strict.AlignedReturns, r => Assert.Equal(3, r.Length));
        Assert.Equal(2, strict.BetaAlpha.Count);
    }

    [Fact]
    public void StrictEqualHoldingPeriod_ComparesPeriodsForW1()
    {
        // B has no bar for the fourth week: common return weeks are 1, 3, 4, 5, 6, 7 (indices of eight weeks); only week index 3 is mixed.
        List<CandleData> WeeklyBars(Func<int, decimal> price, int skippedWeek)
            => Enumerable.Range(0, 8).Where(w => w != skippedWeek).Select(w => Bar(Day1.AddDays(7 * w), price(w))).ToList();

        var universe = Universe(WeeklyBars(PriceA, -1), WeeklyBars(PriceB, 2));

        CrossAssetStatsResult strict = ReturnStatistics.Calculate(universe, timeFrame: TimeFrame.W1, holdingPeriodMode: HoldingPeriodAlignmentMode.StrictEqualHoldingPeriod);

        Assert.Equal(5, strict.CommonDateCount);
        Assert.Equal(1, strict.ExcludedMultiBarDateCount);
        Assert.DoesNotContain(Day1.AddDays(21), strict.CommonDates);
    }
}
