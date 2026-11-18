using System;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Tests.Common;
using Xunit;

namespace StockAnalyzer.Core.Tests.MathUtils;

/// <summary>
/// Pins <see cref="NormalityAndTailMath"/> to scipy (jarque_bera; norm.ppf / norm.cdf evaluated in Python with the formulas of plan section 3.5) on fixed windows,
/// and to its boundary rules: minimum counts, flat window, the Cornish-Fisher monotonicity guard and the Sharpe radicand guard.
/// <para>
/// Reference recipe: scipy.stats.jarque_bera(x).statistic / .pvalue; norm.ppf(1 - c); the Cornish-Fisher and Probabilistic Sharpe Ratio values from the formulas of the plan evaluated in plain Python.
/// Re-run on 2026-11-18: the Jarque-Bera statistic and p-value of WindowA and WindowC reproduce the embedded literals exactly (relative difference 0 at 15 digits).
/// Environment of 2026-11-18: Python with scipy 1.18.1, mpmath 1.3.0, numpy 2.5.2; statsmodels and arch are NOT installed.
/// </para>
/// </summary>
public class NormalityAndTailMathTests
{
    private static readonly double[] SpikeWindow = BuildSpikeWindow();

    private static double[] BuildSpikeWindow()
    {
        var window = new double[30];
        Array.Fill(window, 0.001);
        window[29] = 0.5;
        return window;
    }

    [Fact]
    public void JarqueBera_MatchesScipy_AndThePValueIsExpOfMinusHalfTheStatistic()
    {
        Assert.True(NormalityAndTailMath.TryJarqueBera(WindowMomentsTests.Moments(WindowMomentsTests.WindowA), out double jbA, out double pA));
        Assert.Equal(0.27929012369949835, jbA, 12);
        Assert.Equal(0.8696668585706124, pA, 12);
        Assert.Equal(Math.Exp(-jbA / 2.0), pA, 15);

        Assert.True(NormalityAndTailMath.TryJarqueBera(WindowMomentsTests.Moments(WindowMomentsTests.WindowC), out double jbC, out double pC));
        Assert.Equal(16.83355241145889, jbC, 10);
        Assert.Equal(0.00022112637092504769, pC, 14);

        Assert.True(NormalityAndTailMath.TryJarqueBera(WindowMomentsTests.Moments(SpikeWindow), out double jbS, out double pS));
        Assert.Equal(918.5790725326991, jbS, 8);
        Assert.True(Math.Abs(pS - 3.412626851631903e-200) / 3.412626851631903e-200 < 1e-9, $"p = {pS:R}");
    }

    [Fact]
    public void JarqueBera_NeedsEightValuesAndANonFlatWindow()
    {
        double[] seven = { 0.01, -0.02, 0.03, -0.01, 0.0, 0.02, -0.03 };
        Assert.False(NormalityAndTailMath.TryJarqueBera(WindowMomentsTests.Moments(seven), out double statistic, out double pValue));
        Assert.True(double.IsNaN(statistic));
        Assert.True(double.IsNaN(pValue));

        var flat = new double[12];
        Array.Fill(flat, 0.004);
        Assert.False(NormalityAndTailMath.TryJarqueBera(WindowMomentsTests.Moments(flat), out _, out _));
        Assert.False(NormalityAndTailMath.TryJarqueBera(default, out _, out _));
    }

    [Fact]
    public void CornishFisherValueAtRisk_MatchesTheHandFormula()
    {
        Assert.True(NormalityAndTailMath.TryCornishFisherValueAtRisk(WindowMomentsTests.Moments(WindowMomentsTests.WindowA), 0.95, out double gaussA, out double cfA));
        Assert.Equal(-0.0155405573145532, gaussA, 13);
        Assert.Equal(-0.016318807528405498, cfA, 13);

        Assert.True(NormalityAndTailMath.TryCornishFisherValueAtRisk(WindowMomentsTests.Moments(WindowMomentsTests.WindowC), 0.95, out double gaussC, out double cfC));
        Assert.Equal(-0.020420404452569763, gaussC, 13);
        Assert.Equal(-0.022752024561946787, cfC, 12);
        Assert.True(cfC < gaussC, "a fat left tail makes the Cornish-Fisher loss larger (more negative) than the Gaussian one");
    }

    [Fact]
    public void CornishFisherQuantile_IsTheIdentityForANormalDistribution_AndTheDerivativeIsOne()
    {
        Assert.Equal(-1.6448536269514722, NormalityAndTailMath.CornishFisherQuantile(-1.6448536269514722, 0.0, 0.0), 15);
        Assert.Equal(1.0, NormalityAndTailMath.CornishFisherDerivative(-1.6448536269514722, 0.0, 0.0), 15);
    }

    [Fact]
    public void CornishFisherValueAtRisk_IsNullWhereTheExpansionIsNotMonotone_ButTheGaussianValueRemains()
    {
        // Derivative -4.97 at z = -1.645 (scipy side computation): the expansion would give the positive "loss" 0.116.
        Assert.True(NormalityAndTailMath.TryCornishFisherValueAtRisk(WindowMomentsTests.Moments(SpikeWindow), 0.95, out double gauss, out double cf));
        Assert.Equal(-0.13222026473415963, gauss, 12);
        Assert.True(double.IsNaN(cf));
    }

    // Two 30-return windows (fixed values, checked with an independent Python calculation of the same moment definitions) on which the OLD guard (the derivative at the quantile only)
    // accepted a Cornish-Fisher value although the expansion is not increasing between the median and the quantile.
    // W1: G1 = 2.9928, G2 = 17.6785, 95 %: q(z) = +0.3331 (accepted before), the parabola's vertex -0.6957 lies inside (z, 0) with q = -0.3128. The old CF VaR was -0.00968 against -0.05664 Gaussian.
    private static readonly double[] TurnsBackInsideTheInterval =
    {
        0.0099, -0.0004, -0.0052, -0.0021, -0.0879, -0.0007, 0.0035, -0.0027, -0.0024, 0.1569, -0.0058, -0.0013, -0.0121, -0.0005, 0.0030,
        -0.0013, -0.0131, -0.0065, -0.0098, 0.0055, -0.0084, -0.0129, 0.0061, -0.0130, 0.0062, -0.0039, 0.0013, -0.0015, -0.0160, 0.0002,
    };

    // W2: G1 = -0.1784, G2 = 12.6265, 95 %: q(z) = +3.7798 (accepted before) but q(0) = -0.5745: the expansion decreases at the median itself.
    private static readonly double[] DecreasesAtTheMedian =
    {
        -0.0128, -0.0024, -0.0095, 0.0002, 0.0050, 0.0057, 0.0032, -0.0031, 0.0081, -0.0050, 0.1034, -0.0006, 0.0002, 0.0067, -0.0051,
        -0.1079, -0.0007, -0.0054, -0.0162, 0.0036, 0.0013, 0.0026, 0.0015, -0.0151, 0.0074, 0.0115, -0.0053, 0.0060, 0.0006, 0.0025,
    };

    private const double Z95 = -1.6448536269514722;

    [Theory]
    [InlineData(0, 2.9928, 17.6785, 0.3331, -0.0566362020)]
    [InlineData(1, -0.1784, 12.6265, 3.7798, -0.0475978118)]
    public void CornishFisherValueAtRisk_IsNullWhereTheExpansionTurnsBackBetweenTheMedianAndTheQuantile(int which, double skewness, double kurtosis, double derivativeAtTheQuantile, double gaussian)
    {
        WindowMoments moments = WindowMomentsTests.Moments(which == 0 ? TurnsBackInsideTheInterval : DecreasesAtTheMedian);
        Assert.Equal(skewness, moments.SampleSkewness, 4);
        Assert.Equal(kurtosis, moments.SampleExcessKurtosis, 4);

        // The old guard looked at the quantile only and accepted both windows.
        Assert.Equal(derivativeAtTheQuantile, NormalityAndTailMath.CornishFisherDerivative(Z95, moments.SampleSkewness, moments.SampleExcessKurtosis), 4);
        Assert.True(NormalityAndTailMath.CornishFisherDerivative(Z95, moments.SampleSkewness, moments.SampleExcessKurtosis) > 0.0);

        Assert.True(NormalityAndTailMath.TryCornishFisherValueAtRisk(moments, 0.95, out double gaussianVar, out double cornishFisherVar));
        Assert.Equal(gaussian, gaussianVar, 9);
        Assert.True(double.IsNaN(cornishFisherVar));
    }

    [Theory]
    [InlineData(2.5, 14.0, false)] // vertex -0.588 inside (z, 0) with a negative value; q(z) = +0.664 passed the old guard
    [InlineData(2.0, 6.0, false)]
    [InlineData(3.0, 14.0, false)]
    [InlineData(5.477225575051661, 30.0, false)] // the spike window: q(z) < 0 already
    [InlineData(0.0, 0.0, true)] // a = 0: no vertex, no division
    [InlineData(-0.5, 1.0, true)]
    [InlineData(0.4, -1.0, true)] // a < 0 (hill-shaped): the ends decide
    public void CornishFisherMonotoneOnTail_FollowsTheEndsAndTheVertexOfTheDerivativeParabola(double skewness, double kurtosis, bool expected) =>
        Assert.Equal(expected, NormalityAndTailMath.IsCornishFisherMonotoneOnTail(Z95, skewness, kurtosis));

    [Fact]
    public void CornishFisherMonotoneOnTail_AtTheMedian_NeedsOnlyThePositiveDerivativeThere()
    {
        // z = 0: the interval is one point, q(0) = 1 - G2 / 8 + 5 G1^2 / 36.
        Assert.True(NormalityAndTailMath.IsCornishFisherMonotoneOnTail(0.0, 0.0, 7.9));
        Assert.False(NormalityAndTailMath.IsCornishFisherMonotoneOnTail(0.0, 0.0, 8.1));
        Assert.False(NormalityAndTailMath.IsCornishFisherMonotoneOnTail(double.NaN, 0.0, 0.0));
        Assert.False(NormalityAndTailMath.IsCornishFisherMonotoneOnTail(Z95, double.NaN, 0.0));
    }

    [Fact]
    public void CornishFisherMonotoneOnTail_AgreesWithAGridCheckOfTheDerivativeOnTheWholeInterval()
    {
        int accepted = 0;
        int rejected = 0;
        foreach (double confidence in new[] { 0.9, 0.95, 0.99, 0.999 })
        {
            double z = SpecialFunctionsMath.NormalQuantile(1.0 - confidence);
            for (double skewness = -4.0; skewness <= 4.0; skewness += 0.5)
            {
                for (double kurtosis = 0.0; kurtosis <= 30.0; kurtosis += 1.5)
                {
                    double lowest = double.PositiveInfinity;
                    for (int step = 0; step <= 400; step++)
                    {
                        lowest = Math.Min(lowest, NormalityAndTailMath.CornishFisherDerivative(z * step / 400.0, skewness, kurtosis));
                    }

                    bool helper = NormalityAndTailMath.IsCornishFisherMonotoneOnTail(z, skewness, kurtosis);
                    if (helper)
                    {
                        accepted++;
                        Assert.True(lowest > 0.0, $"accepted but the derivative reaches {lowest} (G1 {skewness}, G2 {kurtosis}, c {confidence})");
                        double previous = double.NegativeInfinity;
                        for (int step = 400; step >= 0; step--)
                        {
                            double value = NormalityAndTailMath.CornishFisherQuantile(z * step / 400.0, skewness, kurtosis);
                            Assert.True(value >= previous - 1e-12, $"not increasing from z to 0 (G1 {skewness}, G2 {kurtosis}, c {confidence})");
                            previous = value;
                        }
                    }
                    else
                    {
                        rejected++;
                        // A rejection is justified by the grid unless the lowest point sits within the grid spacing of zero (the vertex falls between two grid points).
                        Assert.True(lowest < 1e-3, $"rejected but the derivative stays at {lowest} (G1 {skewness}, G2 {kurtosis}, c {confidence})");
                    }
                }
            }
        }

        Assert.True(accepted > 0 && rejected > 0, "the grid must contain both outcomes");
    }

    [Fact]
    public void CornishFisherValueAtRisk_IsUnchangedWhereTheExpansionIsMonotone()
    {
        // The two reference windows of the hand-formula test: the new guard accepts them and the values stay as before.
        Assert.True(NormalityAndTailMath.TryCornishFisherValueAtRisk(WindowMomentsTests.Moments(WindowMomentsTests.WindowC), 0.95, out _, out double cf));
        Assert.Equal(-0.022752024561946787, cf, 12);
    }

    [Fact]
    public void CornishFisherValueAtRisk_FlatWindow_GaussianIsTheMeanAndCornishFisherIsUndefined()
    {
        var flat = new double[10];
        Array.Fill(flat, -0.002);
        Assert.True(NormalityAndTailMath.TryCornishFisherValueAtRisk(WindowMomentsTests.Moments(flat), 0.99, out double gauss, out double cf));
        Assert.Equal(-0.002, gauss, 15);
        Assert.True(double.IsNaN(cf));
    }

    [Theory]
    [InlineData(0.499)]
    [InlineData(0.9991)]
    [InlineData(double.NaN)]
    [InlineData(1.0)]
    public void CornishFisherValueAtRisk_RejectsAConfidenceLevelOutsideTheRange(double confidence)
    {
        Assert.False(NormalityAndTailMath.TryCornishFisherValueAtRisk(WindowMomentsTests.Moments(WindowMomentsTests.WindowA), confidence, out double gauss, out double cf));
        Assert.True(double.IsNaN(gauss));
        Assert.True(double.IsNaN(cf));
    }

    [Fact]
    public void CornishFisherValueAtRisk_AtTheLowestConfidence_IsTheMedianBasedZeroQuantile()
    {
        // c = 0.5 -> alpha = 0.5 -> z = 0: the Gaussian VaR is the mean and the expansion adds nothing at z = 0 except -(2 z^3 - 5 z) = 0 and (z^2 - 1) G1 / 6.
        WindowMoments m = WindowMomentsTests.Moments(WindowMomentsTests.WindowA);
        Assert.True(NormalityAndTailMath.TryCornishFisherValueAtRisk(m, 0.5, out double gauss, out double cf));
        Assert.Equal(m.Mean, gauss, 15);
        Assert.Equal(m.Mean + (-1.0 / 6.0 * m.SampleSkewness * m.SampleStandardDeviation), cf, 14);
    }

    [Fact]
    public void ProbabilisticSharpeRatio_MatchesTheHandFormula()
    {
        Assert.Equal(0.777075032846509, NormalityAndTailMath.ProbabilisticSharpeRatio(WindowMomentsTests.Moments(WindowMomentsTests.WindowA)), 12);
        Assert.Equal(0.0010105818661493278, NormalityAndTailMath.ProbabilisticSharpeRatio(WindowMomentsTests.Moments(WindowMomentsTests.WindowC)), 12);
        Assert.Equal(0.9833926598275224, NormalityAndTailMath.ProbabilisticSharpeRatio(WindowMomentsTests.Moments(SpikeWindow)), 12);
    }

    [Fact]
    public void ProbabilisticSharpeRatio_IsHalfForAZeroSharpeRatio_AndNullWhenTheRadicandIsNotPositive()
    {
        Assert.Equal(0.5, NormalityAndTailMath.ProbabilisticSharpeRatio(0.0, 0.3, 1.0, 50), 15);

        // radicand = 1 - G1 SR + (G2 + 2) SR^2 / 4: for G1 = 4, SR = 0.25, G2 = -2: 1 - 1 + 0 = 0 -> not above the variance epsilon.
        Assert.True(double.IsNaN(NormalityAndTailMath.ProbabilisticSharpeRatio(0.25, 4.0, -2.0, 50)));
        Assert.True(double.IsNaN(NormalityAndTailMath.ProbabilisticSharpeRatio(0.5, 6.0, -2.0, 50)));
        Assert.True(double.IsNaN(NormalityAndTailMath.ProbabilisticSharpeRatio(double.NaN, 0.0, 0.0, 50)));
    }

    [Fact]
    public void ProbabilisticSharpeRatio_NeedsFourValuesAndANonFlatWindow()
    {
        double[] three = { 0.01, -0.02, 0.03 };
        Assert.True(double.IsNaN(NormalityAndTailMath.ProbabilisticSharpeRatio(WindowMomentsTests.Moments(three))));
        var flat = new double[10];
        Array.Fill(flat, 0.01);
        Assert.True(double.IsNaN(NormalityAndTailMath.ProbabilisticSharpeRatio(WindowMomentsTests.Moments(flat))));
    }

    [Fact]
    public void Kernels_AllocateNothing()
    {
        WindowMoments m = WindowMomentsTests.Moments(WindowMomentsTests.WindowC);
        NormalityAndTailMath.TryJarqueBera(m, out _, out _);
        NormalityAndTailMath.TryCornishFisherValueAtRisk(m, 0.95, out _, out _);
        NormalityAndTailMath.ProbabilisticSharpeRatio(m);
        long allocated = AllocationProbe.Minimum(() =>
        {
            for (int round = 0; round < 50; round++)
            {
                NormalityAndTailMath.TryJarqueBera(m, out _, out _);
                NormalityAndTailMath.TryCornishFisherValueAtRisk(m, 0.95, out _, out _);
                NormalityAndTailMath.ProbabilisticSharpeRatio(m);
            }
        });

        Assert.True(allocated == 0, $"The kernels allocated {allocated} bytes in every one of {AllocationProbe.Samples} samples.");
    }
}
