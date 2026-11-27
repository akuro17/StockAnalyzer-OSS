using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Drawing;

public sealed class GarchVolatilityConeValuesTests
{
    private static readonly DateTime Start = new(2026, 1, 1);

    /// <summary>A price path with volatility clustering from a small deterministic generator (no random seed from the clock).</summary>
    private static List<CoreCandleData> ClusteredCandles(int count, decimal firstClose = 100m)
    {
        var list = new List<CoreCandleData>(count);
        double state = 12345.0;
        double variance = 1.0;
        decimal close = firstClose;
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
            {
                state = (state * 1103515245.0 + 12345.0) % 2147483648.0;
                double uniform = state / 2147483648.0;
                double shock = (uniform - 0.5) * 3.4641016151377544;   // mean 0, variance 1
                double ret = Math.Sqrt(variance) * shock;
                variance = 0.2 + 0.1 * ret * ret + 0.8 * variance;
                close = Math.Round(close * (decimal)Math.Exp(ret * 0.01), 6);
            }

            list.Add(new CoreCandleData(Start.AddDays(i), close, close, close, close, 1000L));
        }

        return list;
    }

    private static List<CoreCandleData> WithCloses(IEnumerable<decimal> closes)
    {
        var list = new List<CoreCandleData>();
        int i = 0;
        foreach (decimal close in closes)
        {
            list.Add(new CoreCandleData(Start.AddDays(i++), close, close, close, close, 1000L));
        }

        return list;
    }

    [Fact]
    public void Bands_FollowTheFormulaOfTheSpecification_OnEveryStep()
    {
        var candles = ClusteredCandles(260);
        const int horizon = 20;

        GarchVolatilityCone cone = GarchVolatilityConeValues.Calculate(candles, 10, 249, horizon);

        Assert.True(cone.IsOk);
        Assert.Equal(240, cone.RangeBars);
        Assert.Equal(240, cone.EffectiveBars);
        Assert.Equal(horizon, cone.Horizon);
        Assert.Equal(candles[249].Close, cone.CenterPrice);

        // An independent transcription: the returns of the bars of the range (bar 10 .. 249 -> 239 returns), the kernel fit, then the recursion of the specification.
        var returns = new double[239];
        for (int k = 0; k < returns.Length; k++)
        {
            returns[k] = EgarchMath.PercentLogReturn((double)candles[10 + k].Close, (double)candles[11 + k].Close);
        }

        Assert.True(Garch11Math.TryFit(returns, 0, returns.Length, IndicatorDefaultConstants.Garch11DefaultMaxIterations, out Garch11Fit fit));
        double persistence = fit.Alpha + fit.Beta;
        double sbar2 = fit.Omega / (1.0 - persistence);
        double cumulative = 0.0;
        double center = (double)candles[249].Close;
        for (int h = 1; h <= horizon; h++)
        {
            double s2 = sbar2 + Math.Pow(persistence, h - 1) * (fit.NextVariance - sbar2);
            cumulative += s2;
            double sigma = 0.01 * Math.Sqrt(cumulative);
            AssertClose(center * Math.Exp(sigma), cone.Upper1[h - 1]);
            AssertClose(center * Math.Exp(-sigma), cone.Lower1[h - 1]);
            AssertClose(center * Math.Exp(2.0 * sigma), cone.Upper2[h - 1]);
            AssertClose(center * Math.Exp(-2.0 * sigma), cone.Lower2[h - 1]);
        }

        Assert.Equal(fit.Alpha, cone.Fit.Alpha);
        Assert.Equal(fit.Beta, cone.Fit.Beta);
        Assert.Equal(sbar2, cone.LongRunVariance, 12);
    }

    private static void AssertClose(double expected, decimal? actual)
    {
        Assert.True(actual.HasValue);
        Assert.Equal(expected, (double)actual!.Value, Math.Max(1e-9, Math.Abs(expected) * 1e-12));
    }

    [Fact]
    public void Bands_AreSymmetricInLogSpace_AndWidenWithTheHorizon()
    {
        var candles = ClusteredCandles(200);

        GarchVolatilityCone cone = GarchVolatilityConeValues.Calculate(candles, 0, 199, 30);

        Assert.True(cone.IsOk);
        double center = (double)cone.CenterPrice;
        for (int h = 0; h < 30; h++)
        {
            double upper2 = (double)cone.Upper2[h]!.Value;
            double lower2 = (double)cone.Lower2[h]!.Value;
            double upper1 = (double)cone.Upper1[h]!.Value;
            double lower1 = (double)cone.Lower1[h]!.Value;

            // Zero drift: the geometric mean of the two edges is the centre.
            Assert.Equal(center * center, upper2 * lower2, 4);
            Assert.Equal(center * center, upper1 * lower1, 4);
            Assert.True(upper2 > upper1 && upper1 > center && center > lower1 && lower1 > lower2);
            if (h > 0)
            {
                Assert.True(upper2 > (double)cone.Upper2[h - 1]!.Value);
                Assert.True(lower2 < (double)cone.Lower2[h - 1]!.Value);
            }
        }
    }

    [Fact]
    public void PercentUnitIsScaledBackToAFractionByOnePercent()
    {
        // 100 * ln(1.01) percent returns alternating with 100 * ln(1/1.01): the fitted window variance is (100 ln 1.01)^2 percent^2.
        var closes = new List<decimal> { 100m };
        for (int i = 1; i < 120; i++)
        {
            closes.Add(i % 3 == 0 ? closes[i - 1] / 1.02m : closes[i - 1] * 1.01m);
        }

        GarchVolatilityCone cone = GarchVolatilityConeValues.Calculate(WithCloses(closes), 0, 119, 5);

        Assert.True(cone.IsOk);
        // One-step band: P * exp(0.01 * sqrt(s2(T+1))); with s2 of the order of the window variance (a few percent squared) the band is a few per cent wide, not 100x more or less.
        double width = Math.Log((double)cone.Upper1[0]!.Value / (double)cone.CenterPrice);
        Assert.InRange(width, 0.003, 0.05);
    }

    [Fact]
    public void RangeBeforeTheMinimum_HasNoBands_AndThirtyOneBarsIsTheSmallestFit()
    {
        var candles = ClusteredCandles(80);

        GarchVolatilityCone tooShort = GarchVolatilityConeValues.Calculate(candles, 0, 29, 20);
        Assert.Equal(GarchConeStatus.TooFewBars, tooShort.Status);
        Assert.Equal(30, tooShort.RangeBars);
        Assert.Equal(0, tooShort.EffectiveBars);
        Assert.Empty(tooShort.Upper1);

        GarchVolatilityCone smallest = GarchVolatilityConeValues.Calculate(candles, 0, 30, 20);
        Assert.Equal(GarchConeStatus.Ok, smallest.Status);
        Assert.Equal(31, smallest.RangeBars);
        Assert.Equal(31, GarchVolatilityConeValues.MinBars);
    }

    [Fact]
    public void LongRange_IsFittedOnTheNewestBarsOnly_AndReportsBothCounts()
    {
        const int total = GarchVolatilityConeValues.MaxBars + 300;
        var candles = ClusteredCandles(total);

        GarchVolatilityCone cone = GarchVolatilityConeValues.Calculate(candles, 0, total - 1, 20);

        Assert.True(cone.IsOk);
        Assert.Equal(total, cone.RangeBars);
        Assert.Equal(GarchVolatilityConeValues.MaxBars, cone.EffectiveBars);
        Assert.True(cone.IsTruncated);
        Assert.Equal($"Last {GarchVolatilityConeValues.MaxBars} bars", GarchVolatilityConeValues.TruncationBadge(cone));

        // The same bands as a cone over exactly the newest bars.
        GarchVolatilityCone newest = GarchVolatilityConeValues.Calculate(candles, total - GarchVolatilityConeValues.MaxBars, total - 1, 20);
        Assert.Equal(newest.Upper2, cone.Upper2);
        Assert.Equal(newest.Lower1, cone.Lower1);
        Assert.False(newest.IsTruncated);
        Assert.Equal(string.Empty, GarchVolatilityConeValues.TruncationBadge(newest));
    }

    [Fact]
    public void TheBarBeforeTheRange_IsNeverRead()
    {
        var candles = ClusteredCandles(120);
        GarchVolatilityCone before = GarchVolatilityConeValues.Calculate(candles, 20, 110, 10);

        var changed = new List<CoreCandleData>(candles);
        changed[19] = new CoreCandleData(candles[19].Timestamp, 999m, 999m, 999m, 999m, 1L);
        GarchVolatilityCone after = GarchVolatilityConeValues.Calculate(changed, 20, 110, 10);

        Assert.Equal(before.Upper2, after.Upper2);
        Assert.Equal(before.Lower2, after.Lower2);
    }

    [Fact]
    public void FlatPrice_AndNonPositivePrice_GiveNoBands()
    {
        GarchVolatilityCone flat = GarchVolatilityConeValues.Calculate(WithCloses(Enumerable.Repeat(100m, 60)), 0, 59, 10);
        Assert.Equal(GarchConeStatus.FitFailed, flat.Status);
        Assert.Empty(flat.Upper2);
        Assert.Equal(60, flat.RangeBars);
        Assert.Equal(60, flat.EffectiveBars);

        var candles = ClusteredCandles(60);
        candles[30] = new CoreCandleData(candles[30].Timestamp, 0m, 0m, 0m, 0m, 1L);
        Assert.Equal(GarchConeStatus.NonPositivePrice, GarchVolatilityConeValues.Calculate(candles, 0, 59, 10).Status);
    }

    [Fact]
    public void Horizon_IsClampedToTheSettingsRange()
    {
        var candles = ClusteredCandles(100);

        Assert.Equal(GarchVolatilityConeValues.MinHorizon, GarchVolatilityConeValues.Calculate(candles, 0, 99, 1).Horizon);
        Assert.Equal(GarchVolatilityConeValues.MaxHorizon, GarchVolatilityConeValues.Calculate(candles, 0, 99, 5000).Horizon);
        Assert.Equal(GarchVolatilityConeValues.DefaultHorizon, GarchVolatilityConeValues.Calculate(candles, 0, 99, GarchVolatilityConeValues.DefaultHorizon).Horizon);
    }

    [Fact]
    public void InvalidIndexes_GiveTheEmptyCone()
    {
        var candles = ClusteredCandles(50);

        Assert.Equal(GarchConeStatus.NoRange, GarchVolatilityConeValues.Calculate(candles, -1, 10, 20).Status);
        Assert.Equal(GarchConeStatus.NoRange, GarchVolatilityConeValues.Calculate(candles, 10, 50, 20).Status);
        Assert.Equal(GarchConeStatus.NoRange, GarchVolatilityConeValues.Calculate(candles, 20, 10, 20).Status);
        Assert.Equal(GarchConeStatus.NoRange, GarchVolatilityConeValues.Calculate(new List<CoreCandleData>(), 0, 0, 20).Status);
    }

    [Fact]
    public void Values_ListTheHorizonEndTheFitAndTheBarCounts()
    {
        const int total = GarchVolatilityConeValues.MaxBars + 40;
        var candles = ClusteredCandles(total);
        GarchVolatilityCone cone = GarchVolatilityConeValues.Calculate(candles, 0, total - 1, 25);

        IReadOnlyList<DrawingCalculatedValue> values = GarchVolatilityConeValues.Calculate(cone, IndicatorColor.Gray);

        string[] keys = values.Select(v => v.Key).ToArray();
        Assert.Equal(new[]
        {
            "GarchCenter", "GarchUpper1", "GarchLower1", "GarchUpper2", "GarchLower2",
            "GarchAlpha", "GarchBeta", "GarchPersistence", "GarchLongRunVolatility", "RangeBars", "EffectiveBars",
        }, keys);
        Assert.Equal(cone.Upper2[24], values.Single(v => v.Key == "GarchUpper2").NumericValue);
        Assert.Equal(cone.Lower1[24], values.Single(v => v.Key == "GarchLower1").NumericValue);
        Assert.Equal(total, values.Single(v => v.Key == "RangeBars").NumericValue);
        Assert.Equal(GarchVolatilityConeValues.MaxBars, values.Single(v => v.Key == "EffectiveBars").NumericValue);
        Assert.Equal("+25 bars", values.Single(v => v.Key == "GarchUpper2").Label.Split(' ', 2)[1]);
        Assert.Equal(cone.CenterPrice.ToString("F3", DrawingValueFormat.Culture), values.Single(v => v.Key == "GarchCenter").FormattedText);
    }

    [Fact]
    public void Values_OfACone_WithoutBands_CarryTheStateAndTheCounts()
    {
        var candles = ClusteredCandles(40);

        IReadOnlyList<DrawingCalculatedValue> tooShort = GarchVolatilityConeValues.Calculate(GarchVolatilityConeValues.Calculate(candles, 0, 19, 20), IndicatorColor.Gray);
        Assert.Equal(new[] { "State", "RangeBars", "EffectiveBars" }, tooShort.Select(v => v.Key).ToArray());
        Assert.Equal("Insufficient bars (< 31)", tooShort[0].FormattedText);
        Assert.Null(tooShort[0].NumericValue);
        Assert.Equal(20, tooShort[1].NumericValue);

        Assert.Empty(GarchVolatilityConeValues.Calculate(GarchVolatilityCone.Empty, IndicatorColor.Gray));
        Assert.Equal("GARCH fit failed", GarchVolatilityConeValues.StatusText(GarchConeStatus.FitFailed));
        Assert.Equal("No bars in the range", GarchVolatilityConeValues.StatusText(GarchConeStatus.NoRange));
        Assert.Equal(string.Empty, GarchVolatilityConeValues.StatusText(GarchConeStatus.Ok));
    }

    [Fact]
    public void ModelLimit_IsTheLimitOfTheGarchIndicator()
    {
        Assert.Equal(IndicatorDefaultConstants.Garch11MaxPeriod, GarchVolatilityConeValues.MaxReturns);
        Assert.Equal(IndicatorDefaultConstants.Garch11DefaultMaxIterations, GarchVolatilityConeValues.FitMaxIterations);
    }
}
