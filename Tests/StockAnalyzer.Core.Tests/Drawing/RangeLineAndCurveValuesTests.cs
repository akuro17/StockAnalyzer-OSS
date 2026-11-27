using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Drawing;

/// <summary>Robust and weighted regression lines and the PCHIP / spline anchor curves of a drawing (DSL expansion run D-3 and D-4).</summary>
public sealed class RangeLineAndCurveValuesTests
{
    private static readonly DateTime Start = new(2026, 1, 1);

    private static List<CoreCandleData> Candles(IEnumerable<decimal> closes, long volume = 1000L)
    {
        var list = new List<CoreCandleData>();
        int i = 0;
        foreach (decimal close in closes)
        {
            list.Add(new CoreCandleData(Start.AddDays(i++), close, close, close, close, volume));
        }

        return list;
    }

    private static List<CoreCandleData> Noisy(int count)
    {
        double state = 99.0;
        var closes = new List<decimal>(count);
        for (int i = 0; i < count; i++)
        {
            state = (state * 1103515245.0 + 12345.0) % 2147483648.0;
            closes.Add(Math.Round(100m + (i * 0.3m) + (decimal)((state / 2147483648.0) - 0.5) * 2m, 4));
        }

        return Candles(closes);
    }

    // ---------------------------------------------------------------- robust

    [Fact]
    public void Robust_OneOutlier_MovesTheLineLessThanTheOrdinaryFit()
    {
        var closes = Enumerable.Range(0, 30).Select(i => 100m + (i * 0.5m)).ToList();
        closes[20] += 60m;   // one outlier
        var candles = Candles(closes);

        RangeLineFit robust = RobustRegressionTrendValues.Calculate(candles, 0, 29, false, 2.0m, false);
        RegressionTrendFit ols = RegressionTrendValues.Fit(candles, candles[0].Timestamp, candles[29].Timestamp);

        Assert.True(robust.IsOk);
        Assert.Equal(0.5m, robust.Slope, 6);            // the outlier does not move the Theil-Sen slope
        Assert.True(Math.Abs((double)ols.Result.Slope - 0.5) > 0.05);   // the ordinary fit does
        Assert.Equal(100m, robust.Intercept, 6);
    }

    [Fact]
    public void Robust_FitsOnlyTheNewestThirtyBars_AndKeepsTheCounts()
    {
        var candles = Noisy(120);

        RangeLineFit all = RobustRegressionTrendValues.Calculate(candles, 10, 109, false, 2.0m, false);
        RangeLineFit newest = RobustRegressionTrendValues.Calculate(candles, 80, 109, false, 2.0m, false);
        RangeLineFit exactly30 = RobustRegressionTrendValues.Calculate(candles, 80, 109, false, 2.0m, false);
        RangeLineFit thirtyOne = RobustRegressionTrendValues.Calculate(candles, 79, 109, false, 2.0m, false);

        Assert.True(all.IsOk);
        Assert.Equal(100, all.RangeBars);
        Assert.Equal(30, all.EffectiveBars);
        Assert.True(all.IsTruncated);
        Assert.Equal(newest.Slope, all.Slope);
        Assert.Equal(newest.Curve.Prices, all.Curve.Prices);
        Assert.Equal(30, all.Curve.Count);
        Assert.Equal(candles[80].Timestamp, all.Curve.Times[0]);   // nothing is drawn for the excluded first part
        Assert.False(exactly30.IsTruncated);
        Assert.Equal(30, thirtyOne.EffectiveBars);
        Assert.Equal(31, thirtyOne.RangeBars);
        Assert.Equal("Last 30 bars", RangeFitText.TruncationBadge(all.RangeBars, all.EffectiveBars));
    }

    [Fact]
    public void Robust_SiegelSwitch_ExtendRightAndTheBand()
    {
        var candles = Noisy(100);
        var theil = RobustRegressionTrendValues.Calculate(candles, 40, 80, false, 2.0m, false);
        var siegel = RobustRegressionTrendValues.Calculate(candles, 40, 80, true, 2.0m, false);
        var extended = RobustRegressionTrendValues.Calculate(candles, 40, 80, false, 1.5m, true);

        Assert.True(theil.IsOk && siegel.IsOk);
        Assert.NotEqual(theil.Slope, siegel.Slope);
        Assert.Equal(30, theil.Curve.Count);
        Assert.Equal(30 + 19, extended.Curve.Count);   // the existing bars 81 .. 99
        Assert.Equal(30, extended.Curve.FittedCount);
        Assert.Equal(candles[99].Timestamp, extended.Curve.Times[^1]);
        // The line continues with the same slope.
        Assert.Equal(extended.Curve.Prices[30] - extended.Curve.Prices[29], extended.Slope, 9);
        Assert.Equal(extended.Sigma * 1.5m, extended.Curve.BandHalfWidth);
    }

    [Fact]
    public void Robust_ShortAndInvalidRanges_AndTheValues()
    {
        var candles = Noisy(60);

        Assert.Equal(RangeFitStatus.TooFewBars, RobustRegressionTrendValues.Calculate(candles, 5, 5, false, 2.0m, false).Status);
        Assert.Equal(RangeFitStatus.Ok, RobustRegressionTrendValues.Calculate(candles, 5, 6, false, 2.0m, false).Status);
        Assert.Equal(RangeFitStatus.NoRange, RobustRegressionTrendValues.Calculate(candles, -1, 6, false, 2.0m, false).Status);
        Assert.Equal(RangeFitStatus.NoRange, RobustRegressionTrendValues.Calculate(candles, 7, 60, false, 2.0m, false).Status);
        Assert.Equal(0.1m, RobustRegressionTrendValues.Calculate(candles, 5, 40, false, 0m, false).StdDevMultiplier);
        Assert.Equal(10m, RobustRegressionTrendValues.Calculate(candles, 5, 40, false, 99m, false).StdDevMultiplier);

        RangeLineFit fit = RobustRegressionTrendValues.Calculate(candles, 5, 35, false, 2.0m, false);
        IReadOnlyList<DrawingCalculatedValue> values = RobustRegressionTrendValues.Calculate(fit, IndicatorColor.Gray);
        Assert.Equal(new[] { "RobustSlope", "RobustIntercept", "RobustFittedLast", "RobustUpper", "RobustLower", "RobustSigma", "RangeBars", "EffectiveBars" }, values.Select(v => v.Key).ToArray());
        Assert.Equal(fit.FittedLast, values.Single(v => v.Key == "RobustFittedLast").NumericValue);
        Assert.Equal(31, values.Single(v => v.Key == "RangeBars").NumericValue);
        Assert.Equal(30, values.Single(v => v.Key == "EffectiveBars").NumericValue);

        var tooShort = RobustRegressionTrendValues.Calculate(RobustRegressionTrendValues.Calculate(candles, 5, 5, false, 2.0m, false), IndicatorColor.Gray);
        Assert.Equal(new[] { "State", "RangeBars", "EffectiveBars" }, tooShort.Select(v => v.Key).ToArray());
        Assert.Equal("Insufficient bars (< 2)", tooShort[0].FormattedText);
        Assert.Empty(RobustRegressionTrendValues.Calculate(RangeLineFit.Empty, IndicatorColor.Gray));
    }

    // ---------------------------------------------------------------- weighted

    [Fact]
    public void Weighted_EqualVolumes_GiveTheOrdinaryLeastSquaresLine()
    {
        var candles = Noisy(90);

        RangeLineFit weighted = WeightedRegressionTrendValues.Calculate(candles, 10, 79, WeightedRegressionMode.VolumeWeighted, 0.94, 2.0m, false);
        RegressionTrendFit ols = RegressionTrendValues.Fit(candles, candles[10].Timestamp, candles[79].Timestamp);

        Assert.True(weighted.IsOk);
        Assert.Equal((double)ols.Result.Slope, (double)weighted.Slope, 8);
        Assert.Equal((double)ols.Result.GetValueAt(0), (double)weighted.Intercept, 6);
        Assert.Equal((double)ols.Result.StdDev, (double)weighted.Sigma, 6);   // uniform weights: sum w e^2 / sum w is the divisor-N variance
        Assert.Equal(70, weighted.Curve.Count);
    }

    [Fact]
    public void Weighted_ExponentialDecay_FollowsTheRecentBarsMoreThanTheOrdinaryFit()
    {
        // Flat for 60 bars, then a steep rise over the last 20: the decayed line is steeper than the ordinary one.
        var closes = Enumerable.Repeat(100m, 60).Concat(Enumerable.Range(1, 20).Select(i => 100m + (i * 2m))).ToList();
        var candles = Candles(closes);

        RangeLineFit decayed = WeightedRegressionTrendValues.Calculate(candles, 0, 79, WeightedRegressionMode.ExponentialDecay, 0.90, 2.0m, false);
        RangeLineFit uniform = WeightedRegressionTrendValues.Calculate(candles, 0, 79, WeightedRegressionMode.VolumeWeighted, 0.90, 2.0m, false);

        Assert.True(decayed.IsOk && uniform.IsOk);
        Assert.True(decayed.Slope > uniform.Slope * 1.5m);
        // The fitted last value is closer to the last close than the ordinary line is.
        Assert.True(Math.Abs(decayed.FittedLast - closes[^1]) < Math.Abs(uniform.FittedLast - closes[^1]));
    }

    [Fact]
    public void Weighted_VolumeWeights_PullTheLineToTheHeavyBars()
    {
        var closes = Enumerable.Range(0, 40).Select(i => 100m + i).ToList();
        closes[5] = 160m;    // an off-trend bar
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < closes.Count; i++)
        {
            candles.Add(new CoreCandleData(Start.AddDays(i), closes[i], closes[i], closes[i], closes[i], i == 5 ? 1_000_000L : 10L));
        }

        RangeLineFit heavy = WeightedRegressionTrendValues.Calculate(candles, 0, 39, WeightedRegressionMode.VolumeWeighted, 0.94, 2.0m, false);

        // The heavy off-trend bar dominates: the line passes near it at offset 5.
        decimal atBar5 = heavy.Intercept + (heavy.Slope * 5);
        Assert.True(Math.Abs(atBar5 - 160m) < 5m);
    }

    [Fact]
    public void Weighted_LimitsParametersAndStates()
    {
        var candles = Noisy(40);

        Assert.Equal(RangeFitStatus.TooFewBars, WeightedRegressionTrendValues.Calculate(candles, 0, 1, WeightedRegressionMode.VolumeWeighted, 0.94, 2.0m, false).Status);
        Assert.Equal("Insufficient bars (< 3)", WeightedRegressionTrendValues.StatusText(WeightedRegressionTrendValues.Calculate(candles, 0, 1, WeightedRegressionMode.VolumeWeighted, 0.94, 2.0m, false)));
        // The decay factor is clamped to [0.80, 0.99]: a factor of 5 gives the same fit as 0.99, one of -1 the same as 0.80.
        var high = WeightedRegressionTrendValues.Calculate(candles, 0, 39, WeightedRegressionMode.ExponentialDecay, 5.0, 2.0m, false);
        var top = WeightedRegressionTrendValues.Calculate(candles, 0, 39, WeightedRegressionMode.ExponentialDecay, 0.99, 2.0m, false);
        var low = WeightedRegressionTrendValues.Calculate(candles, 0, 39, WeightedRegressionMode.ExponentialDecay, -1.0, 2.0m, false);
        var bottom = WeightedRegressionTrendValues.Calculate(candles, 0, 39, WeightedRegressionMode.ExponentialDecay, 0.80, 2.0m, false);
        Assert.Equal(top.Slope, high.Slope);
        Assert.Equal(bottom.Slope, low.Slope);

        var long512 = Noisy(WeightedRegressionTrendValues.MaxBars + 50);
        var cut = WeightedRegressionTrendValues.Calculate(long512, 0, long512.Count - 1, WeightedRegressionMode.ExponentialDecay, 0.94, 2.0m, false);
        Assert.Equal(512, cut.EffectiveBars);
        Assert.True(cut.IsTruncated);

        var values = WeightedRegressionTrendValues.Calculate(cut, IndicatorColor.Gray);
        Assert.Equal("WeightedSlope", values[0].Key);
        Assert.Equal(long512.Count, values.Single(v => v.Key == "RangeBars").NumericValue);
    }

    // ---------------------------------------------------------------- anchor curves

    private static DrawingAnchor Anchor(List<CoreCandleData> candles, int index, decimal price) => new(candles[index].Timestamp, price);

    [Fact]
    public void BarPosition_RoundTrips_AcrossGapsAndOutsideTheData()
    {
        var times = new List<CoreCandleData>();
        DateTime t = Start;
        for (int i = 0; i < 30; i++)
        {
            t = t.AddDays(i % 5 == 4 ? 3 : 1);   // weekend-like gaps
            times.Add(new CoreCandleData(t, 1m, 1m, 1m, 1m, 1L));
        }

        TimeSpan span = DrawingRangeBars.MeanBarSpan(times);
        for (double position = -3.0; position <= 33.0; position += 0.37)
        {
            DateTime time = DrawingRangeBars.TimeAtBarPosition(times, position, span);
            Assert.Equal(position, DrawingRangeBars.BarPositionOf(times, time, span), 6);
        }

        Assert.Equal(7.0, DrawingRangeBars.BarPositionOf(times, times[7].Timestamp, span), 12);
        Assert.Equal(times[7].Timestamp, DrawingRangeBars.TimeAtBarPosition(times, 7.0, span));
    }

    [Theory]
    [InlineData(InterpolationCurveKind.Pchip)]
    [InlineData(InterpolationCurveKind.CubicSpline)]
    public void Curve_PassesThroughEveryAnchor_AndDoesNotExtrapolate(InterpolationCurveKind kind)
    {
        var candles = Noisy(80);
        // Placed out of order on purpose.
        var anchors = new[] { Anchor(candles, 40, 112m), Anchor(candles, 10, 100m), Anchor(candles, 60, 105m), Anchor(candles, 25, 120m) };

        InterpolationCurve curve = InterpolationCurveValues.Calculate(candles, anchors, kind);

        Assert.True(curve.IsOk);
        Assert.Equal(4, curve.DistinctAnchors);
        Assert.Equal((3 * InterpolationCurveValues.SamplesPerSegment) + 1, curve.Count);
        Assert.Equal(candles[10].Timestamp, curve.Times[0]);
        Assert.Equal(candles[60].Timestamp, curve.Times[^1]);
        Assert.Equal(100m, curve.Prices[0]);
        Assert.Equal(105m, curve.Prices[^1]);
        Assert.Equal(120m, curve.Prices[InterpolationCurveValues.SamplesPerSegment]);   // the second anchor in time order
        Assert.Equal(112m, curve.Prices[2 * InterpolationCurveValues.SamplesPerSegment]);
        Assert.True(curve.Times.Zip(curve.Times.Skip(1), (a, b) => b > a).All(x => x));   // strictly increasing: nothing before the first or after the last anchor
    }

    [Fact]
    public void Curve_TwoAnchorsAreAStraightSegment_AndFewerDrawNothing()
    {
        var candles = Noisy(40);

        InterpolationCurve two = InterpolationCurveValues.Calculate(candles, new[] { Anchor(candles, 5, 100m), Anchor(candles, 30, 130m) }, InterpolationCurveKind.CubicSpline);
        Assert.True(two.IsOk);
        Assert.Equal(2, two.Count);

        InterpolationCurve one = InterpolationCurveValues.Calculate(candles, new[] { Anchor(candles, 5, 100m) }, InterpolationCurveKind.Pchip);
        Assert.Equal(InterpolationCurveStatus.TooFewAnchors, one.Status);
        Assert.Equal("Needs at least 2 distinct anchors", InterpolationCurveValues.StatusText(one.Status));

        Assert.Equal(InterpolationCurveStatus.NoData, InterpolationCurveValues.Calculate(new List<CoreCandleData>(), new[] { new DrawingAnchor(Start, 1m) }, InterpolationCurveKind.Pchip).Status);
    }

    [Fact]
    public void Curve_TwoAnchorsOnTheSameBar_KeepTheLastPlaced()
    {
        var candles = Noisy(40);
        var anchors = new[] { Anchor(candles, 5, 100m), Anchor(candles, 20, 110m), Anchor(candles, 20, 150m), Anchor(candles, 35, 120m) };

        InterpolationCurve curve = InterpolationCurveValues.Calculate(candles, anchors, InterpolationCurveKind.Pchip);

        Assert.True(curve.IsOk);
        Assert.Equal(3, curve.DistinctAnchors);
        Assert.Equal(150m, curve.Prices[InterpolationCurveValues.SamplesPerSegment]);
        Assert.DoesNotContain(110m, curve.Prices);

        // Two anchors on one bar and nothing else: a single distinct anchor.
        Assert.Equal(InterpolationCurveStatus.TooFewAnchors, InterpolationCurveValues.Calculate(candles, new[] { Anchor(candles, 5, 1m), Anchor(candles, 5, 2m) }, InterpolationCurveKind.Pchip).Status);
    }

    [Fact]
    public void Pchip_NeverOvershootsMonotoneData_WhereTheSplineDoes()
    {
        var candles = Noisy(60);
        // A staircase: flat, jump, flat. The monotone interpolant stays inside [100, 150]; a natural spline rings outside it.
        var anchors = new[] { Anchor(candles, 5, 100m), Anchor(candles, 15, 100m), Anchor(candles, 20, 150m), Anchor(candles, 30, 150m), Anchor(candles, 40, 150m) };

        InterpolationCurve pchip = InterpolationCurveValues.Calculate(candles, anchors, InterpolationCurveKind.Pchip);
        InterpolationCurve spline = InterpolationCurveValues.Calculate(candles, anchors, InterpolationCurveKind.CubicSpline);

        Assert.True(pchip.IsOk && spline.IsOk);
        Assert.All(pchip.Prices, p => Assert.InRange(p, 99.999999m, 150.000001m));   // the Hermite form leaves rounding noise of ~1e-14 at a flat node
        Assert.Contains(spline.Prices, p => p < 100m || p > 150m);
    }

    [Fact]
    public void Spline_HasANaturalEnd_AndMatchesTheDslKernel()
    {
        var candles = Noisy(70);
        var anchors = new[] { Anchor(candles, 5, 100m), Anchor(candles, 25, 130m), Anchor(candles, 45, 95m), Anchor(candles, 65, 120m) };

        InterpolationCurve curve = InterpolationCurveValues.Calculate(candles, anchors, InterpolationCurveKind.CubicSpline);

        // The same values as the kernel on the same grid (positions are the bar indexes of the anchors).
        double[] xs = { 5, 25, 45, 65 };
        double[] ys = { 100, 130, 95, 120 };
        TimeSpan span = DrawingRangeBars.MeanBarSpan(candles);
        for (int i = 0; i < curve.Count; i++)
        {
            double x = DrawingRangeBars.BarPositionOf(candles, curve.Times[i], span);
            MathResult<double> expected = DslInterpolationMath.InterpCubicSpline(xs, ys, x);
            Assert.True(expected.IsOk);
            Assert.Equal(expected.Value, (double)curve.Prices[i], 4);
        }

        // Natural end: the second difference at the first samples is about 0 (S'' = 0 at the end).
        double h = 20.0 / InterpolationCurveValues.SamplesPerSegment;
        double secondDifference = (double)(curve.Prices[0] - (2m * curve.Prices[1]) + curve.Prices[2]) / (h * h);
        double interior = (double)(curve.Prices[InterpolationCurveValues.SamplesPerSegment - 1] - (2m * curve.Prices[InterpolationCurveValues.SamplesPerSegment]) + curve.Prices[InterpolationCurveValues.SamplesPerSegment + 1]) / (h * h);
        Assert.True(Math.Abs(secondDifference) < Math.Abs(interior));
    }

    [Fact]
    public void Curve_TooManyAnchors_AndTheValues()
    {
        var candles = Noisy(300);
        var many = Enumerable.Range(0, InterpolationCurveValues.MaxAnchors + 1).Select(i => Anchor(candles, i * 4, 100m + (i % 7))).ToList();
        InterpolationCurve tooMany = InterpolationCurveValues.Calculate(candles, many, InterpolationCurveKind.CubicSpline);
        Assert.Equal(InterpolationCurveStatus.TooManyAnchors, tooMany.Status);
        Assert.Equal("Too many anchors (> 64)", InterpolationCurveValues.StatusText(tooMany.Status));

        var limit = many.Take(InterpolationCurveValues.MaxAnchors).ToList();
        InterpolationCurve ok = InterpolationCurveValues.Calculate(candles, limit, InterpolationCurveKind.CubicSpline);
        Assert.True(ok.IsOk);
        IReadOnlyList<DrawingCalculatedValue> values = InterpolationCurveValues.Calculate(ok, IndicatorColor.Gray);
        Assert.Equal(new[] { "AnchorCount", "SampleCount", "CurveStart", "CurveEnd" }, values.Select(v => v.Key).ToArray());
        Assert.Equal(64, values[0].NumericValue);
        Assert.Equal(ok.Count, values[1].NumericValue);

        var state = InterpolationCurveValues.Calculate(tooMany, IndicatorColor.Gray);
        Assert.Equal(new[] { "State", "AnchorCount" }, state.Select(v => v.Key).ToArray());
        Assert.Empty(InterpolationCurveValues.Calculate(InterpolationCurve.Empty, IndicatorColor.Gray));
    }
}
