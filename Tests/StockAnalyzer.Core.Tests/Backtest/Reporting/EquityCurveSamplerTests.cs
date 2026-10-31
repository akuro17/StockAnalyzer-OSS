using System;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Reporting;

public class EquityCurveSamplerTests
{
    private static readonly DateTime BaseUtc = ReportTestHelpers.BaseUtc; // 2024-01-01, a Monday

    private static ImmutableArray<EquityPoint> Daily(params decimal[] equity) =>
        equity.Select((e, i) => new EquityPoint(i + 10, BaseUtc.AddDays(i), e, e, 0m, 0m)).ToImmutableArray();

    [Fact]
    public void EmptyAndDefaultInput_YieldEmpty()
    {
        foreach (EquityCurveResolution resolution in Enum.GetValues<EquityCurveResolution>())
        {
            Assert.Empty(EquityCurveSampler.Sample(100m, ImmutableArray<EquityPoint>.Empty, resolution));
            ImmutableArray<EquityCurvePoint> fromDefault = EquityCurveSampler.Sample(100m, default, resolution);
            Assert.False(fromDefault.IsDefault);
            Assert.Empty(fromDefault);
        }
    }

    [Fact]
    public void Bar_ReturnsEveryPoint_WithDrawdownRatioAgainstTheRunningHighIncludingInitialCapital()
    {
        ImmutableArray<EquityCurvePoint> curve = EquityCurveSampler.Sample(100m, Daily(90m, 110m, 99m, 120m), EquityCurveResolution.Bar);

        Assert.Equal(new decimal?[] { -0.1m, 0m, -0.1m, 0m }, curve.Select(p => p.DrawdownRatio).ToArray());
        Assert.Equal(new[] { 10, 11, 12, 13 }, curve.Select(p => p.BarIndex).ToArray());
        Assert.Equal(new[] { 90m, 110m, 99m, 120m }, curve.Select(p => p.Equity).ToArray());
        Assert.All(curve, p => Assert.Null(p.DrawdownReason));
    }

    [Fact]
    public void SinglePoint_YieldsOnePoint_ForEveryResolution()
    {
        foreach (EquityCurveResolution resolution in Enum.GetValues<EquityCurveResolution>())
        {
            EquityCurvePoint only = Assert.Single(EquityCurveSampler.Sample(100m, Daily(95m), resolution));
            Assert.Equal(-0.05m, only.DrawdownRatio);
        }
    }

    [Fact]
    public void ConstantEquityAtInitialCapital_HasZeroDrawdownEverywhere()
    {
        ImmutableArray<EquityCurvePoint> curve = EquityCurveSampler.Sample(100m, Daily(100m, 100m, 100m), EquityCurveResolution.Bar);

        Assert.All(curve, p => Assert.Equal(0m, p.DrawdownRatio));
    }

    [Fact]
    public void MonotonicIncrease_HasZeroDrawdownEverywhere()
    {
        ImmutableArray<EquityCurvePoint> curve = EquityCurveSampler.Sample(100m, Daily(101m, 102m, 150m), EquityCurveResolution.Bar);

        Assert.All(curve, p => Assert.Equal(0m, p.DrawdownRatio));
    }

    [Fact]
    public void Weekly_KeepsTheLastBarOfEachIsoWeek_AndTheRunningHighComesFromEveryBar()
    {
        // 10 daily bars from Monday 2024-01-01: week 1 = bars 0..6, week 2 = bars 7..9. The 150 peak is on bar 2, i.e. not on a sampled bar.
        decimal[] equity = { 100m, 110m, 150m, 140m, 130m, 125m, 120m, 121m, 122m, 130m };

        ImmutableArray<EquityCurvePoint> weekly = EquityCurveSampler.Sample(100m, Daily(equity), EquityCurveResolution.Weekly);

        Assert.Equal(new[] { 16, 19 }, weekly.Select(p => p.BarIndex).ToArray());
        Assert.Equal(new decimal?[] { (120m - 150m) / 150m, (130m - 150m) / 150m }, weekly.Select(p => p.DrawdownRatio).ToArray());
    }

    [Fact]
    public void Monthly_KeepsTheLastBarOfEachCalendarMonth()
    {
        // 40 daily bars from 2024-01-01: January = bars 0..30, February starts at bar 31 (2024-02-01), the data ends on bar 39 (2024-02-09).
        decimal[] equity = Enumerable.Range(0, 40).Select(i => 100m + i).ToArray();
        equity[20] = 200m;

        ImmutableArray<EquityCurvePoint> monthly = EquityCurveSampler.Sample(100m, Daily(equity), EquityCurveResolution.Monthly);

        Assert.Equal(new[] { 40, 49 }, monthly.Select(p => p.BarIndex).ToArray());
        Assert.Equal(new[] { new DateTime(2024, 1, 31, 0, 0, 0, DateTimeKind.Utc), new DateTime(2024, 2, 9, 0, 0, 0, DateTimeKind.Utc) }, monthly.Select(p => p.Timestamp).ToArray());
        Assert.Equal(new decimal?[] { (130m - 200m) / 200m, (139m - 200m) / 200m }, monthly.Select(p => p.DrawdownRatio).ToArray());
    }

    [Fact]
    public void SampledPoints_AreIdenticalToTheSameBarsOfTheBarResolutionCurve()
    {
        var random = new Random(20261030);
        decimal[] equity = Enumerable.Range(0, 120).Select(_ => (decimal)random.Next(60, 140)).ToArray();
        ImmutableArray<EquityPoint> points = Daily(equity);
        ImmutableArray<EquityCurvePoint> bar = EquityCurveSampler.Sample(100m, points, EquityCurveResolution.Bar);

        foreach (EquityCurveResolution resolution in new[] { EquityCurveResolution.Weekly, EquityCurveResolution.Monthly })
        {
            ImmutableArray<EquityCurvePoint> thinned = EquityCurveSampler.Sample(100m, points, resolution);

            Assert.True(thinned.Length < bar.Length);
            Assert.All(thinned, point => Assert.Equal(bar.Single(b => b.BarIndex == point.BarIndex), point));
        }
    }

    [Fact]
    public void NegativeEquity_GivesARatioBelowMinusOne()
    {
        EquityCurvePoint point = Assert.Single(EquityCurveSampler.Sample(100m, Daily(-20m), EquityCurveResolution.Bar));

        Assert.Equal(-1.2m, point.DrawdownRatio);
    }

    [Fact]
    public void NonPositiveRunningHigh_GivesNullRatioWithReason()
    {
        ImmutableArray<EquityCurvePoint> curve = EquityCurveSampler.Sample(0m, Daily(-5m, -3m, 4m), EquityCurveResolution.Bar);

        Assert.Null(curve[0].DrawdownRatio);
        Assert.Equal(EquityCurveSampler.NonPositivePeakReason, curve[0].DrawdownReason);
        Assert.Null(curve[1].DrawdownRatio);
        Assert.Equal(0m, curve[2].DrawdownRatio);
        Assert.Null(curve[2].DrawdownReason);
    }

    [Fact]
    public void ARatioBeyondTheDecimalRange_PropagatesAnOverflow_ForTheCallerToReport()
    {
        // (-7e28 - 0.01) / 0.01 does not fit a decimal; the tool names the part in diagnosticsUnavailable instead of failing the run.
        Assert.Throws<OverflowException>(() => EquityCurveSampler.Sample(0.01m, Daily(-70_000_000_000_000_000_000_000_000_000m), EquityCurveResolution.Bar));
    }

    [Fact]
    public void UnknownResolution_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EquityCurveSampler.Sample(100m, Daily(100m), (EquityCurveResolution)99));
    }
}
