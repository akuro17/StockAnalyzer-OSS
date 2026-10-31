using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Reporting;

public class PeriodicReturnsCalculatorTests
{
    private static DateTime Day(string text) =>
        DateTime.SpecifyKind(DateTime.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture), DateTimeKind.Utc);

    private static ImmutableArray<EquityPoint> Points(params (string Date, decimal Equity)[] bars) =>
        bars.Select((b, i) => new EquityPoint(i, Day(b.Date), b.Equity, b.Equity, 0m, 0m)).ToImmutableArray();

    private static PeriodicReturns Calculate(decimal initial, string start, string end, params (string Date, decimal Equity)[] bars) =>
        PeriodicReturnsCalculator.Calculate(initial, Points(bars), Day(start), Day(end));

    [Fact]
    public void AReturnBeyondTheDecimalRange_PropagatesAnOverflow_ForTheCallerToReport()
    {
        // 7e28 / 0.01 does not fit a decimal; the tool names the part in diagnosticsUnavailable instead of failing the run.
        Assert.Throws<OverflowException>(() => Calculate(0.01m, "2024-01-01", "2024-01-31", ("2024-01-15", 70_000_000_000_000_000_000_000_000_000m)));
    }

    [Fact]
    public void EmptyAndDefaultInput_YieldEmptyLists()
    {
        PeriodicReturns empty = PeriodicReturnsCalculator.Calculate(100m, ImmutableArray<EquityPoint>.Empty, new DateTime(2024, 1, 1), new DateTime(2024, 12, 31));
        PeriodicReturns fromDefault = PeriodicReturnsCalculator.Calculate(100m, default, new DateTime(2024, 1, 1), new DateTime(2024, 12, 31));

        Assert.True(empty.Monthly.IsEmpty && empty.Yearly.IsEmpty);
        Assert.False(fromDefault.Monthly.IsDefault);
        Assert.False(fromDefault.Yearly.IsDefault);
        Assert.True(fromDefault.Monthly.IsEmpty && fromDefault.Yearly.IsEmpty);
    }

    [Fact]
    public void SinglePoint_IsOnePartialMonthAndOnePartialYear_WhenTheEvaluationPeriodIsInsideTheMonth()
    {
        PeriodicReturns result = Calculate(100m, "2024-03-10", "2024-03-20", ("2024-03-15", 110m));

        Assert.Equal(new PeriodicReturn(2024, 3, 0.1m, null, true), Assert.Single(result.Monthly));
        Assert.Equal(new PeriodicReturn(2024, null, 0.1m, null, true), Assert.Single(result.Yearly));
    }

    [Fact]
    public void FullMonthsAndFullYear_AreNotPartial_AndChainFromTheInitialCapital()
    {
        PeriodicReturns result = Calculate(
            100m, "2024-01-01", "2024-12-31",
            ("2024-01-02", 105m), ("2024-01-31", 110m), ("2024-02-29", 99m), ("2024-03-28", 108.9m), ("2024-12-31", 108.9m));

        Assert.Equal(
            new[]
            {
                new PeriodicReturn(2024, 1, 0.1m, null, false),
                new PeriodicReturn(2024, 2, -0.1m, null, false),
                new PeriodicReturn(2024, 3, 0.1m, null, false),
                new PeriodicReturn(2024, 12, 0m, null, false),
            },
            result.Monthly.ToArray());
        Assert.Equal(new PeriodicReturn(2024, null, 0.089m, null, false), Assert.Single(result.Yearly));
    }

    [Fact]
    public void AMonthWithoutBars_IsSkipped_AndTheNextStartsFromThePreviousBarsEquity()
    {
        PeriodicReturns result = Calculate(100m, "2024-01-01", "2024-03-31", ("2024-01-31", 110m), ("2024-03-28", 121m));

        Assert.Equal(new[] { 1, 3 }, result.Monthly.Select(m => m.Month!.Value).ToArray());
        Assert.Equal(0.1m, result.Monthly[1].ReturnRatio);
    }

    [Fact]
    public void YearBoundary_SplitsYearlyReturns_AndMarksOnlyTheYearsTheEvaluationPeriodCutsThrough()
    {
        PeriodicReturns result = Calculate(
            100m, "2023-12-15", "2024-01-31",
            ("2023-12-29", 120m), ("2024-01-31", 90m));

        Assert.Equal(
            new[] { new PeriodicReturn(2023, 12, 0.2m, null, true), new PeriodicReturn(2024, 1, -0.25m, null, false) },
            result.Monthly.ToArray());
        Assert.Equal(
            new[] { new PeriodicReturn(2023, null, 0.2m, null, true), new PeriodicReturn(2024, null, -0.25m, null, true) },
            result.Yearly.ToArray());
    }

    [Fact]
    public void LastMonthEndingBeforeItsLastDay_IsPartial()
    {
        PeriodicReturns result = Calculate(100m, "2024-01-01", "2024-02-15", ("2024-01-31", 110m), ("2024-02-15", 99m));

        Assert.False(result.Monthly[0].IsPartial);
        Assert.True(result.Monthly[1].IsPartial);
    }

    [Fact]
    public void NonPositiveStartEquity_GivesNullReturnWithReason()
    {
        PeriodicReturns result = Calculate(100m, "2024-01-01", "2024-03-31", ("2024-01-31", 0m), ("2024-02-29", 50m), ("2024-03-28", 60m));

        Assert.Equal(-1m, result.Monthly[0].ReturnRatio);
        Assert.Null(result.Monthly[1].ReturnRatio);
        Assert.Equal(PeriodicReturnsCalculator.NonPositiveStartEquityReason, result.Monthly[1].Reason);
        Assert.Equal(0.2m, result.Monthly[2].ReturnRatio);
        Assert.Equal(-0.4m, Assert.Single(result.Yearly).ReturnRatio);
    }

    [Fact]
    public void ConstantEquity_GivesZeroReturns()
    {
        PeriodicReturns result = Calculate(100m, "2024-01-01", "2024-02-29", ("2024-01-31", 100m), ("2024-02-29", 100m));

        Assert.All(result.Monthly, m => Assert.Equal(0m, m.ReturnRatio));
    }

    [Fact]
    public void TheCompoundedMonthlyReturns_EqualTheYearlyReturn()
    {
        var random = new Random(20261030);
        EquityPoint[] points = Enumerable.Range(0, 400)
            .Select(i => new EquityPoint(i, new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i), 50m + random.Next(0, 100), 0m, 0m, 0m))
            .ToArray();

        PeriodicReturns result = PeriodicReturnsCalculator.Calculate(100m, points.ToImmutableArray(), points[0].Timestamp, points[^1].Timestamp);

        foreach (PeriodicReturn year in result.Yearly)
        {
            decimal compounded = result.Monthly.Where(m => m.Year == year.Year).Aggregate(1m, (product, m) => product * (1m + m.ReturnRatio!.Value)) - 1m;
            Assert.True(Math.Abs(compounded - year.ReturnRatio!.Value) < 0.0000000001m, $"year {year.Year}");
        }
    }
}
