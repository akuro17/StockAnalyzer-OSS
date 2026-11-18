using System;
using System.Linq;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Tests.Common;
using Xunit;

namespace StockAnalyzer.Core.Tests.MathUtils;

/// <summary>
/// Pins <see cref="WindowMoments"/> to scipy.stats (skew / kurtosis with and without the sample adjustment: bias=False is G1 / G2, bias=True is b1 / b2) on fixed windows,
/// and to the flat-window rules and the minimum counts of plan section 3.1 / 3.4.
/// <para>
/// Reference recipe: scipy.stats.skew(x, bias=False) = G1, scipy.stats.kurtosis(x, bias=False) = G2 (Fisher, excess); bias=True gives b1 / b2. Not re-run on 2026-11-18.
/// Environment of 2026-11-18: Python with scipy 1.18.1, mpmath 1.3.0, numpy 2.5.2; statsmodels and arch are NOT installed.
/// </para>
/// </summary>
public class WindowMomentsTests
{
    private const double Tolerance = 1e-12;

    internal static readonly double[] WindowA = { 0.012, -0.008, 0.003, 0.021, -0.015, 0.007, -0.002, 0.01 };

    internal static readonly double[] WindowC =
    {
        1.2e-05, -0.011766, 0.000422, -0.005685, 0.001744, 0.014437, -0.038229, -0.004667, 0.001849, -0.006014, -0.020421, -0.006162, -0.000262,
        -0.001173, -0.013034, -0.012548, -0.003763, -0.012767, -0.001621, 0.004551, 0.002694, -0.016583, 0.006675, -0.006905, -0.005271, 0.008621,
        -0.001666, -0.002919, -0.003595, -0.003274, -0.000208, 0.01155, -0.012435, -0.025209, 0.000905, -0.006325, -0.002097, 0.009073, 0.000623,
        -0.006717,
    };

    internal static WindowMoments Moments(double[] window, FlatWindowRule rule = FlatWindowRule.Returns)
    {
        Assert.True(WindowMoments.TryCompute(window, rule, out WindowMoments moments));
        return moments;
    }

    [Fact]
    public void WindowA_MatchesScipy()
    {
        WindowMoments m = Moments(WindowA);
        Assert.Equal(8, m.Count);
        Assert.Equal(0.0035, m.Mean, 15);
        Assert.Equal(0.011575836902790225, m.SampleStandardDeviation, 14);
        Assert.Equal(-0.21219113566518655, m.SampleSkewness, 12);
        Assert.Equal(-0.3844954332813528, m.SampleExcessKurtosis, 12);
        Assert.Equal(-0.17013139932205648, m.PopulationSkewness, 12);
        Assert.Equal(-0.8497597301339779, m.PopulationExcessKurtosis, 12);
        Assert.False(m.IsFlat);
    }

    [Fact]
    public void WindowC_MatchesScipy()
    {
        WindowMoments m = Moments(WindowC);
        Assert.Equal(-0.004204, m.Mean, 14);
        Assert.Equal(0.00985887387598422, m.SampleStandardDeviation, 14);
        Assert.Equal(-1.1152571358718084, m.SampleSkewness, 11);
        Assert.Equal(2.832321636801751, m.SampleExcessKurtosis, 11);
        Assert.Equal(-1.0729915656224402, m.PopulationSkewness, 11);
        Assert.Equal(2.344117711909483, m.PopulationExcessKurtosis, 11);
        Assert.Equal(m.M2 / 40.0, m.PopulationVariance);
    }

    [Fact]
    public void SpikeWindow_HasTheClosedFormMoments()
    {
        // 29 equal values and one spike: G1 = sqrt(30), G2 = 30 exactly in exact arithmetic (scipy: 5.47722557505166, 30.0).
        var window = new double[30];
        Array.Fill(window, 0.001);
        window[29] = 0.5;
        WindowMoments m = Moments(window);
        Assert.Equal(Math.Sqrt(30.0), m.SampleSkewness, 10);
        Assert.Equal(30.0, m.SampleExcessKurtosis, 9);
    }

    [Fact]
    public void ShortWindows_LeaveTheHigherMomentsUndefined()
    {
        double[] twoValues = { 1.0, 3.0 };
        WindowMoments two = Moments(twoValues);
        Assert.Equal(Math.Sqrt(2.0), two.SampleStandardDeviation, 15);
        Assert.True(double.IsNaN(two.SampleSkewness));
        Assert.True(double.IsNaN(two.SampleExcessKurtosis));

        double[] threeValues = { 1.0, 2.0, 4.0 };
        WindowMoments three = Moments(threeValues);
        Assert.False(double.IsNaN(three.SampleSkewness));
        Assert.True(double.IsNaN(three.SampleExcessKurtosis));

        double[] oneValue = { 5.0 };
        WindowMoments one = Moments(oneValue);
        Assert.True(double.IsNaN(one.SampleStandardDeviation));
        Assert.True(one.IsFlat);
    }

    [Fact]
    public void ConstantWindow_IsFlatUnderBothRules_AndDividesByNothing()
    {
        var window = new double[20];
        Array.Fill(window, 0.0123);
        foreach (FlatWindowRule rule in Enum.GetValues<FlatWindowRule>())
        {
            WindowMoments m = Moments(window, rule);
            Assert.True(m.IsFlat);
            Assert.True(double.IsNaN(m.PopulationSkewness));
            Assert.True(double.IsNaN(m.PopulationExcessKurtosis));
            Assert.True(double.IsNaN(m.SampleSkewness));
            Assert.True(double.IsNaN(m.SampleExcessKurtosis));
            // The mean of equal values carries a rounding error of one unit in the last place, so s is a few 1e-18, not exactly 0.
            Assert.InRange(m.SampleStandardDeviation, 0.0, 1e-15);
        }
    }

    [Fact]
    public void TheTwoFlatRules_DifferWhereTheirThresholdsDiffer()
    {
        // Population deviation 1e-8: variance 1e-16 <= 1e-14 (flat for returns) but 1e-8 >= 1e-10 (not flat for a series in its own unit).
        var window = new double[20];
        for (int i = 0; i < window.Length; i++)
        {
            window[i] = 1.0 + (i % 2 == 0 ? 1e-8 : -1e-8);
        }

        window[3] += 3e-9;
        Assert.True(Moments(window, FlatWindowRule.Returns).IsFlat);
        WindowMoments series = Moments(window, FlatWindowRule.Series);
        Assert.False(series.IsFlat);
        Assert.False(double.IsNaN(series.PopulationSkewness));

        // The two thresholds sit exactly at the documented constants.
        var tiny = new double[20];
        for (int i = 0; i < tiny.Length; i++)
        {
            tiny[i] = 1e-11 * (i % 2 == 0 ? 1 : -1);
        }

        Assert.True(Moments(tiny, FlatWindowRule.Series).IsFlat);
    }

    [Theory]
    [InlineData(1_000_000.1)]
    [InlineData(123_456_789.123)]
    public void ALargeConstantWithAFraction_IsFlatUnderTheSeriesRule_ByItsRelativeTerm(double value)
    {
        // The mean of 30 equal doubles is off by an ulp or so, so the two-pass deviation of a constant near 1e6 is about 5e-10 (and 3e-8 near 1e8): above the absolute threshold of 1e-10.
        double[] constant = Enumerable.Repeat(value, 30).ToArray();
        WindowMoments moments = Moments(constant, FlatWindowRule.Series);

        double deviation = Math.Sqrt(moments.PopulationVariance);
        Assert.True(deviation >= ComparisonSeriesDefaults.FlatWindowDeviationEpsilon, $"the absolute rule alone would not see a constant here (deviation {deviation:E2})");
        Assert.True(deviation < WindowMoments.SeriesRelativeFlatEpsilon * Math.Abs(moments.Mean));
        Assert.True(moments.IsFlat);
        Assert.True(double.IsNaN(moments.SampleSkewness));
        Assert.True(double.IsNaN(moments.SampleExcessKurtosis));
    }

    [Fact]
    public void TheRelativeTerm_DoesNotHideARealSpread_AndLeavesSmallValuesToTheAbsoluteRule()
    {
        // A real spread at a large level: 1e6 +- 1 (deviation 1) and +- 1e-5 (deviation 1e-5, above 1e-12 x 1e6 = 1e-6) are not flat; +- 1e-7 is.
        foreach (double spread in new[] { 1.0, 1e-5 })
        {
            Assert.False(Moments(Alternating(1e6, spread), FlatWindowRule.Series).IsFlat, $"spread {spread}");
        }

        Assert.True(Moments(Alternating(1e6, 1e-7), FlatWindowRule.Series).IsFlat);

        // Near 1e5 the absolute threshold still decides (1e-12 x 1e5 = 1e-7 < 1e-10 is false, the larger of the two is 1e-7: a deviation of 1e-8 is flat, 1e-6 is not).
        Assert.True(Moments(Alternating(1e5, 1e-8), FlatWindowRule.Series).IsFlat);
        Assert.False(Moments(Alternating(1e5, 1e-6), FlatWindowRule.Series).IsFlat);

        // A small level keeps the old absolute rule: 1e-11 around 0 is flat, 1e-8 around 1 is not (existing test), the relative term is below 1e-10 for |mean| < 100.
        Assert.False(Moments(Alternating(50.0, 1e-8), FlatWindowRule.Series).IsFlat);
    }

    [Fact]
    public void TheReturnsRule_IsNotTouchedByTheRelativeTerm()
    {
        // Returns are judged by their variance only (<= 1e-14), whatever the mean.
        Assert.False(Moments(Alternating(0.5, 1e-3), FlatWindowRule.Returns).IsFlat);
        Assert.True(Moments(Alternating(0.5, 1e-8), FlatWindowRule.Returns).IsFlat);
    }

    private static double[] Alternating(double level, double spread)
    {
        var window = new double[20];
        for (int i = 0; i < window.Length; i++)
        {
            window[i] = level + (i % 2 == 0 ? spread : -spread);
        }

        return window;
    }

    [Fact]
    public void NonFiniteOrEmptyInput_IsRejected()
    {
        Assert.False(WindowMoments.TryCompute(ReadOnlySpan<double>.Empty, FlatWindowRule.Returns, out _));
        Assert.False(WindowMoments.TryCompute(new[] { 1.0, double.NaN, 2.0 }, FlatWindowRule.Returns, out _));
        Assert.False(WindowMoments.TryCompute(new[] { 1.0, double.PositiveInfinity }, FlatWindowRule.Returns, out _));
        // The fourth power sum overflows.
        Assert.False(WindowMoments.TryCompute(new[] { 1e100, -1e100, 1e100, -1e100 }, FlatWindowRule.Series, out _));
    }

    [Fact]
    public void TryCompute_AllocatesNothing()
    {
        double[] window = WindowC;
        WindowMoments.TryCompute(window, FlatWindowRule.Returns, out _);
        long allocated = AllocationProbe.Minimum(() =>
        {
            for (int round = 0; round < 50; round++)
            {
                WindowMoments.TryCompute(window, FlatWindowRule.Returns, out WindowMoments m);
                _ = m.SampleSkewness + m.SampleExcessKurtosis + m.SampleStandardDeviation;
            }
        });

        Assert.True(allocated == 0, $"The kernels allocated {allocated} bytes in every one of {AllocationProbe.Samples} samples.");
    }
}
