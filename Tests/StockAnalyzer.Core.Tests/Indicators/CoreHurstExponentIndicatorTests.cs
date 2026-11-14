using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

/// <summary>
/// WebAiExpansion plan 04 T4: Hurst exponent = slope of ln (R/S)_m against ln m over m = n, n/2, n/4, ... (&gt;= 8) of the window of the last Period log returns,
/// clamped to [0, 1], no small-sample correction. First value at bar Period.
/// </summary>
public class CoreHurstExponentIndicatorTests
{
    private const double Tolerance = 1e-9;
    private const int Seed = 12345;

    private static CoreCandleData Bar(int index, decimal close) =>
        new(new DateTime(2020, 1, 1).AddDays(index), close, close, close, close, 1000);

    private static List<CoreCandleData> FromCloses(IEnumerable<decimal> closes) => closes.Select((c, i) => Bar(i, c)).ToList();

    /// <summary>Candles whose log returns (bar i + 1) are the given values.</summary>
    private static List<CoreCandleData> FromReturns(IReadOnlyList<double> returns)
    {
        var closes = new List<decimal> { 100m };
        double price = 100.0;
        foreach (double r in returns)
        {
            price *= Math.Exp(r);
            closes.Add((decimal)price);
        }

        return FromCloses(closes);
    }

    private static double[] NormalReturns(int count, double sigma)
    {
        var random = new Random(Seed);
        var result = new double[count];
        for (int i = 0; i < count; i++)
        {
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            result[i] = sigma * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        return result;
    }

    private static IReadOnlyList<decimal?> Run(IReadOnlyList<CoreCandleData> candles, int period) =>
        new CoreHurstExponentIndicator { Period = period }.Calculate(candles).MainValues;

    // Independent, deliberately plain implementation of the specification for one window.
    private static double? Reference(double[] window)
    {
        int n = window.Length;
        var points = new List<(double x, double y)>();
        for (int m = n; m >= 8; m /= 2)
        {
            int q = n / m;
            var ratios = new List<double>();
            for (int s = 0; s < q; s++)
            {
                double[] segment = window.Skip(n - (s + 1) * m).Take(m).ToArray();
                double mu = segment.Average();
                double cumulative = 0, max = double.MinValue, min = double.MaxValue;
                foreach (double v in segment)
                {
                    cumulative += v - mu;
                    max = Math.Max(max, cumulative);
                    min = Math.Min(min, cumulative);
                }

                double sd = Math.Sqrt(segment.Sum(v => (v - mu) * (v - mu)) / m);
                if (sd == 0) continue;
                ratios.Add((max - min) / sd);
            }

            if (ratios.Count == 0) continue;
            double mean = ratios.Average();
            if (mean <= 0) continue;
            points.Add((Math.Log(m), Math.Log(mean)));
        }

        if (points.Count < 2) return null;
        double mx = points.Average(p => p.x), my = points.Average(p => p.y);
        double den = points.Sum(p => (p.x - mx) * (p.x - mx));
        if (den == 0) return null;
        double slope = points.Sum(p => (p.x - mx) * (p.y - my)) / den;
        return Math.Clamp(slope, 0.0, 1.0);
    }

    [Fact]
    public void RescaledRange_OfAlternatingPlusMinusOne_IsOne()
    {
        // mean 0, Y = 1, 0, 1, 0 -> R = 1; S = sqrt(4 / 4) = 1.
        Assert.Equal(1.0, HurstMath.RescaledRange(new[] { 1.0, -1.0, 1.0, -1.0 }), Tolerance);
    }

    [Fact]
    public void RescaledRange_OfAConstantSegmentIsNotANumber()
    {
        Assert.True(double.IsNaN(HurstMath.RescaledRange(new[] { 0.01, 0.01, 0.01 })));
        Assert.True(double.IsNaN(HurstMath.RescaledRange(ReadOnlySpan<double>.Empty)));
    }

    [Fact]
    public void RescaledRange_HandComputedMonotoneSegment()
    {
        // {1, 2, 3, 4}: mean 2.5, deviations -1.5, -0.5, 0.5, 1.5, Y = -1.5, -2, -1.5, 0 -> R = 0 - (-2) = 2; S = sqrt((2.25 + 0.25 + 0.25 + 2.25) / 4) = sqrt(1.25).
        Assert.Equal(2.0 / Math.Sqrt(1.25), HurstMath.RescaledRange(new[] { 1.0, 2.0, 3.0, 4.0 }), Tolerance);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(100)]
    [InlineData(257)]
    public void Exponent_MatchesAnIndependentImplementationOfTheSpecification(int n)
    {
        double[] window = NormalReturns(n, 0.01);

        double? expected = Reference(window);

        Assert.NotNull(expected);
        Assert.Equal(expected!.Value, HurstMath.Exponent(window), 1e-12);
    }

    [Fact]
    public void ConstantReturns_GiveNull()
    {
        // Prices doubling every bar: the 40 log returns are all exactly ln 2.
        var closes = Enumerable.Range(0, 41).Select(i => 100m * (decimal)Math.Pow(2, i)).ToList();

        var values = Run(FromCloses(closes), 32);

        Assert.Equal(41, values.Count);
        Assert.All(values, v => Assert.Null(v));
    }

    [Fact]
    public void AConstantPriceGivesNull()
    {
        Assert.All(Run(FromCloses(Enumerable.Repeat(100m, 60)), 32), v => Assert.Null(v));
    }

    [Fact]
    public void IndependentNormalReturns_GiveAnExponentNearOneHalf()
    {
        double[] window = NormalReturns(2000, 0.01);

        double hurst = HurstMath.Exponent(window);

        Assert.InRange(hurst, 0.35, 0.75);
    }

    [Fact]
    public void LongRunsOfTheSameSign_GiveALargerExponentThanIndependentReturns()
    {
        var persistent = new List<double>();
        for (int block = 0; block < 8; block++)
            for (int k = 0; k < 50; k++)
                persistent.Add(block % 2 == 0 ? 0.01 : -0.01);

        double[] independent = NormalReturns(persistent.Count, 0.01);

        var persistentValues = Run(FromReturns(persistent), 100).Where(v => v.HasValue).Select(v => (double)v!.Value).ToList();
        var independentValues = Run(FromReturns(independent), 100).Where(v => v.HasValue).Select(v => (double)v!.Value).ToList();

        Assert.NotEmpty(persistentValues);
        Assert.NotEmpty(independentValues);
        Assert.True(persistentValues.Average() > independentValues.Average(), $"{persistentValues.Average()} vs {independentValues.Average()}");
    }

    [Fact]
    public void TheOutputIsInsideZeroToOne_AndTheFirstValueIsAtBarPeriod()
    {
        var values = Run(FromReturns(NormalReturns(300, 0.01)), 64);

        Assert.Equal(301, values.Count);
        Assert.All(values.Take(64), v => Assert.Null(v));
        Assert.All(values.Skip(64), v => Assert.InRange((double)v!.Value, 0.0, 1.0));
    }

    [Fact]
    public void TheValueAtABarDoesNotChangeWhenLaterBarsAreRemoved()
    {
        var all = FromReturns(NormalReturns(400, 0.01));

        var full = Run(all, 50);
        var truncated = Run(all.Take(250).ToList(), 50);

        for (int i = 0; i < truncated.Count; i++) Assert.Equal(full[i], truncated[i]);
    }

    [Fact]
    public void AnUnusablePriceNullsEveryWindowContainingItsReturns()
    {
        var candles = FromReturns(NormalReturns(200, 0.01));
        candles[100] = Bar(100, 0m);   // r_100 and r_101 unusable

        var values = Run(candles, 40);

        Assert.NotNull(values[99]);
        for (int i = 100; i <= 140; i++) Assert.Null(values[i]);   // the window of bar i is r_(i-39) .. r_i
        Assert.NotNull(values[141]);   // window r_102 .. r_141
    }

    [Fact]
    public void EmptyInput_ReturnsASuccessfulEmptyResult()
    {
        var result = new CoreHurstExponentIndicator().Calculate(new List<CoreCandleData>());

        Assert.True(result.IsSuccessful);
        Assert.Empty(result.MainValues);
    }

    [Fact]
    public void FewerBarsThanPeriodPlusOne_AreAllNull()
    {
        Assert.All(Run(FromReturns(NormalReturns(31, 0.01)), 32), v => Assert.Null(v));
    }

    [Fact]
    public void Parameters_DefaultsAndTheMinimumOf32()
    {
        Assert.Equal(100, new CoreHurstExponentParameter().Period);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreHurstExponentParameter { Period = 31 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreHurstExponentParameter { Period = 2001 }.Validate());
        new CoreHurstExponentParameter { Period = 32 }.Validate();

        var result = new CoreHurstExponentIndicator { Period = 31 }.Calculate(FromReturns(NormalReturns(100, 0.01)));
        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public void TheTypeIsCreatedByTheFactory()
    {
        Assert.IsType<CoreHurstExponentIndicator>(IndicatorFactory.CreateStatic(IndicatorType.HurstExponent));
    }
}
