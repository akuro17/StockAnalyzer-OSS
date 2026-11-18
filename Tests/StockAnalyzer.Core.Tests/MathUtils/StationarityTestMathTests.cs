using System;
using System.Linq;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Tests.Common;
using Xunit;

namespace StockAnalyzer.Core.Tests.MathUtils;

/// <summary>
/// Pins <see cref="StationarityTestMath"/> to 50-digit mpmath references (the same regression <c>dy_t = c + rho y_(t-1) + sum phi_i dy_(t-i)</c> solved with exact normal equations),
/// to the MacKinnon (2010) 5 % response surface and to the boundary rules of plan section 3.6.
/// <para>
/// Reference recipe: mpmath with mp.dps = 50: build X = [1, y_(t-1), dy_(t-1) ... dy_(t-p)], solve (X'X) b = X'y with mp.lu_solve, tau = b_rho / sqrt(SSR / (T - k) (X'X)^-1_rho,rho). Not re-run on 2026-11-18.
/// MacKinnon coefficients: see the comment in StationarityTestMath.cs (checked against two mirrors of statsmodels adfvalues.py, not against the original paper).
/// Environment of 2026-11-18: Python with scipy 1.18.1, mpmath 1.3.0, numpy 2.5.2; statsmodels and arch are NOT installed.
/// </para>
/// </summary>
public class StationarityTestMathTests
{
    private const double Epsilon = IndicatorDefaultConstants.HalfLifeLogEpsilon;

    // Mean-reverting log-price-like series (AR(1) around 4.6, phi 0.8) and a random walk with drift, 40 values each.
    private static readonly double[] MeanReverting =
    {
        4.6, 4.611406, 4.59997, 4.607909, 4.602372, 4.597298, 4.605703, 4.607145, 4.603603, 4.62, 4.591081, 4.588951, 4.586326, 4.605156, 4.599063, 4.602735, 4.594345,
        4.61325, 4.613829, 4.584433, 4.589647, 4.575581, 4.596475, 4.597884, 4.592537, 4.589711, 4.582046, 4.595235, 4.583876, 4.585968, 4.572599, 4.577121, 4.581433,
        4.58502, 4.587108, 4.602916, 4.604576, 4.606538, 4.613233, 4.625811
    };

    private static readonly double[] RandomWalk =
    {
        4.6, 4.597325, 4.591186, 4.601444, 4.588802, 4.59914, 4.613828, 4.623217, 4.637038, 4.617642, 4.614016, 4.607284, 4.616511, 4.614961, 4.623586, 4.620657, 4.636524,
        4.624001, 4.638857, 4.64384, 4.642141, 4.647246, 4.652546, 4.658174, 4.667949, 4.664676, 4.661954, 4.66056, 4.672049, 4.666466, 4.669699, 4.652307, 4.643869,
        4.636712, 4.641195, 4.647431, 4.633197, 4.644089, 4.667294, 4.659723
    };

    private static AugmentedDickeyFullerResult Run(double[] window, int lagOrder)
    {
        double[] scratch = new double[StationarityTestMath.ScratchLength(window.Length, lagOrder)];
        Assert.True(StationarityTestMath.TryAugmentedDickeyFuller(window, lagOrder, Epsilon, scratch, out AugmentedDickeyFullerResult result));
        return result;
    }

    [Theory]
    [InlineData(0, -2.7387398004946406, -0.40501452598339244, 1.3349822179068336, 39)]
    [InlineData(1, -1.8616288089265098, -0.31922208889688164, 1.8026337169833722, 38)]
    [InlineData(3, -1.474199676571452, -0.3226511290477388, 1.7792676038232005, 36)]
    public void MeanRevertingSeries_MatchesTheReference(int lagOrder, double tau, double rho, double halfLife, int rows)
    {
        AugmentedDickeyFullerResult r = Run(MeanReverting, lagOrder);
        Assert.Equal(tau, r.Tau, 9);
        Assert.Equal(rho, r.Rho, 10);
        Assert.Equal(halfLife, r.HalfLife, 8);
        Assert.Equal(rows, r.EffectiveSampleSize);
    }

    [Theory]
    [InlineData(0, -1.5209656307399508, -0.1012390043010546, 6.493903765073016)]
    [InlineData(1, -1.4516324196775188, -0.10259479591599752, 6.403338152997547)]
    [InlineData(3, -1.5253789544616185, -0.12170949401465765, 5.341027658241418)]
    public void RandomWalk_MatchesTheReference(int lagOrder, double tau, double rho, double halfLife)
    {
        AugmentedDickeyFullerResult r = Run(RandomWalk, lagOrder);
        Assert.Equal(tau, r.Tau, 9);
        Assert.Equal(rho, r.Rho, 10);
        Assert.Equal(halfLife, r.HalfLife, 8);
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // Invariance and scratch hygiene (peer-review follow-up, owner decision D-5 = A: tests first, no product change unless one of them fails).
    // tau and rho do not change when every level is multiplied by a positive constant or shifted by a constant: the constant of the regression absorbs the shift and
    // the scale cancels in rho / se(rho). The double arithmetic of the Householder factorisation does not scale the columns, so the measured agreement is the point of the tests.
    // ------------------------------------------------------------------------------------------------------------------------------

    private static double RelativeDifference(double expected, double actual) =>
        Math.Abs(actual - expected) / Math.Max(Math.Abs(expected), 1e-300);

    public static TheoryData<double[], int, double> ScaledWindows()
    {
        var data = new TheoryData<double[], int, double>();
        foreach (double[] series in new[] { MeanReverting, RandomWalk })
        {
            foreach (int lagOrder in new[] { 0, 1, 3 })
            {
                foreach (double scale in new[] { 1e-6, 1e6 })
                {
                    data.Add(series, lagOrder, scale);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ScaledWindows))]
    public void Tau_Rho_AndHalfLife_DoNotChangeWhenEveryLevelIsMultipliedByAConstant(double[] window, int lagOrder, double scale)
    {
        AugmentedDickeyFullerResult baseline = Run(window, lagOrder);
        AugmentedDickeyFullerResult scaled = Run(window.Select(v => v * scale).ToArray(), lagOrder);

        Assert.True(RelativeDifference(baseline.Tau, scaled.Tau) < 1e-9, $"tau: {baseline.Tau:R} vs {scaled.Tau:R} (scale {scale})");
        Assert.True(RelativeDifference(baseline.Rho, scaled.Rho) < 1e-9, $"rho: {baseline.Rho:R} vs {scaled.Rho:R} (scale {scale})");
        Assert.True(RelativeDifference(baseline.HalfLife, scaled.HalfLife) < 1e-9, $"half-life: {baseline.HalfLife:R} vs {scaled.HalfLife:R} (scale {scale})");
        Assert.Equal(baseline.CriticalValue5Percent, scaled.CriticalValue5Percent);
        Assert.Equal(baseline.EffectiveSampleSize, scaled.EffectiveSampleSize);
    }

    [Theory]
    [InlineData(1, 1e3)]
    [InlineData(1, 1e6)]
    [InlineData(3, 1e3)]
    public void Tau_AndRho_DoNotChangeWhenEveryLevelIsShiftedByAConstant(int lagOrder, double shift)
    {
        AugmentedDickeyFullerResult baseline = Run(MeanReverting, lagOrder);
        AugmentedDickeyFullerResult shifted = Run(MeanReverting.Select(v => v + shift).ToArray(), lagOrder);

        // The shifted level column is about shift / sigma(level) times larger than its spread, so a few digits are lost: the bound is 1e-6, the measured difference is in the message.
        Assert.True(RelativeDifference(baseline.Tau, shifted.Tau) < 1e-6, $"tau: {baseline.Tau:R} vs {shifted.Tau:R} (shift {shift})");
        Assert.True(RelativeDifference(baseline.Rho, shifted.Rho) < 1e-6, $"rho: {baseline.Rho:R} vs {shifted.Rho:R} (shift {shift})");
    }

    [Fact]
    public void AHighLevelWithATinyNoise_IsStillTested()
    {
        // Level 1e6 with a spread of 1e-3: spread / level = 1e-9, far above the rank tolerance (1e-12), so the regression is of full rank and tau is defined.
        var random = new Random(4711);
        double[] window = new double[60];
        for (int i = 0; i < window.Length; i++)
        {
            window[i] = 1e6 + (1e-3 * random.NextDouble());
        }

        AugmentedDickeyFullerResult result = Run(window, 1);
        Assert.True(double.IsFinite(result.Tau));
        Assert.True(double.IsFinite(result.Rho));

        // The same observations with the level removed exactly (a subtraction of nearby doubles is exact) give the same statistic up to the lost digits.
        AugmentedDickeyFullerResult reduced = Run(window.Select(v => v - 1e6).ToArray(), 1);
        Assert.True(RelativeDifference(reduced.Tau, result.Tau) < 1e-5, $"tau: {reduced.Tau:R} vs {result.Tau:R}");
    }

    [Fact]
    public void TheResult_DoesNotDependOnWhatTheScratchMemoryHeldBefore()
    {
        // ArrayPool memory comes back dirty and usually longer than asked for: the kernel must write before it reads and look at the first ScratchLength elements only.
        foreach (double[] window in new[] { MeanReverting, RandomWalk })
        {
            foreach (int lagOrder in new[] { 0, 1, 3 })
            {
                int length = StationarityTestMath.ScratchLength(window.Length, lagOrder);
                AugmentedDickeyFullerResult clean = Call(window, lagOrder, new double[length]);
                foreach (double fill in new[] { double.NaN, 1e300, -1e300 })
                {
                    double[] dirty = new double[length + 7];
                    Array.Fill(dirty, fill);
                    AugmentedDickeyFullerResult result = Call(window, lagOrder, dirty);
                    AssertSame(clean, result, $"fill {fill}, lag {lagOrder}");
                    Assert.All(dirty.Skip(length), value => Assert.Equal(fill, value)); // nothing beyond ScratchLength is written
                }

                // Reuse after a previous run (the buffer now holds that run's matrix).
                double[] reused = new double[length];
                Call(RandomWalk, lagOrder, reused);
                AssertSame(clean, Call(window, lagOrder, reused), $"reused, lag {lagOrder}");
            }
        }
    }

    private static AugmentedDickeyFullerResult Call(double[] window, int lagOrder, double[] scratch)
    {
        Assert.True(StationarityTestMath.TryAugmentedDickeyFuller(window, lagOrder, Epsilon, scratch, out AugmentedDickeyFullerResult result));
        return result;
    }

    private static void AssertSame(AugmentedDickeyFullerResult expected, AugmentedDickeyFullerResult actual, string context)
    {
        Assert.True(BitConverter.DoubleToInt64Bits(expected.Tau) == BitConverter.DoubleToInt64Bits(actual.Tau), $"tau differs ({context})");
        Assert.True(BitConverter.DoubleToInt64Bits(expected.Rho) == BitConverter.DoubleToInt64Bits(actual.Rho), $"rho differs ({context})");
        Assert.True(BitConverter.DoubleToInt64Bits(expected.HalfLife) == BitConverter.DoubleToInt64Bits(actual.HalfLife), $"half-life differs ({context})");
        Assert.Equal(expected.EffectiveSampleSize, actual.EffectiveSampleSize);
    }

    [Fact]
    public void ExactGeometricDecay_HasAHalfLifeButNoTau()
    {
        // y = 0.9^t: dy = -0.1 y_(t-1) exactly, so rho = -0.1, half-life = -ln 2 / ln 0.9 = 6.5788..., and the residual is 0 (perfect fit): tau undefined, rho and the half-life are returned.
        double[] y = new double[30];
        for (int t = 0; t < y.Length; t++)
        {
            y[t] = Math.Pow(0.9, t);
        }

        AugmentedDickeyFullerResult r = Run(y, 0);
        Assert.True(double.IsNaN(r.Tau));
        Assert.Equal(-0.1, r.Rho, 12);
        Assert.Equal(6.578813478960685, r.HalfLife, 6);
        Assert.Equal(-Math.Log(2.0) / Math.Log(0.9), r.HalfLife, 10);
    }

    [Fact]
    public void CriticalValue_IsTheFourTermMacKinnonSurface()
    {
        Assert.Equal(-2.86154, StationarityTestMath.CriticalValue5PercentInfinity);
        Assert.Equal(-2.8903, StationarityTestMath.CriticalValue5PercentInverse1);
        Assert.Equal(-4.234, StationarityTestMath.CriticalValue5PercentInverse2);
        Assert.Equal(-40.040, StationarityTestMath.CriticalValue5PercentInverse3);

        // Hand value of the response surface at T = 60.
        double expected60 = -2.86154 - 2.8903 / 60.0 - 4.234 / 3600.0 - 40.040 / 216000.0;
        Assert.Equal(expected60, StationarityTestMath.CriticalValue5Percent(60), 14);
        Assert.Equal(-2.91107, StationarityTestMath.CriticalValue5Percent(60), 5);

        // Large samples approach the asymptotic value, small samples are more negative.
        Assert.Equal(-2.86154, StationarityTestMath.CriticalValue5Percent(1_000_000_000), 8);
        Assert.True(StationarityTestMath.CriticalValue5Percent(20) < StationarityTestMath.CriticalValue5Percent(500));
        Assert.True(double.IsNaN(StationarityTestMath.CriticalValue5Percent(0)));

        // The kernel reports the critical value of its own effective sample size (T = W - p - 1).
        Assert.Equal(StationarityTestMath.CriticalValue5Percent(38), Run(MeanReverting, 1).CriticalValue5Percent);
    }

    [Theory]
    [InlineData(-0.1, 6.578813478960685)]
    [InlineData(-0.5, 1.0)]
    [InlineData(-0.999, 0.1003)]
    public void HalfLife_IsLnTwoOverMinusLnOnePlusRho(double rho, double expectedApprox)
    {
        double half = StationarityTestMath.HalfLife(rho, Epsilon);
        Assert.Equal(-Math.Log(2.0) / Math.Log(1.0 + rho), half, 12);
        Assert.Equal(expectedApprox, half, 3);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.1)]
    [InlineData(-1.0)]
    [InlineData(-2.0)]
    [InlineData(-1e-13)] // |ln(1 + rho)| below the epsilon: the half-life is effectively infinite
    [InlineData(double.NaN)]
    public void HalfLife_IsUndefinedOutsideTheStationaryRange(double rho)
    {
        Assert.True(double.IsNaN(StationarityTestMath.HalfLife(rho, Epsilon)));
    }

    [Fact]
    public void WindowLengthRule_IsTwoLagsPlusFive()
    {
        Assert.Equal(5, StationarityTestMath.MinWindowLength(0));
        Assert.Equal(25, StationarityTestMath.MinWindowLength(10));
        Assert.Equal(12, StationarityTestMath.ParameterCount(10));
        Assert.Equal(29, StationarityTestMath.EffectiveSampleSize(30, 0));

        // W = 2p + 5 is the shortest valid window (two residual degrees of freedom); W = 2p + 4 is rejected.
        double[] ok = new double[StationarityTestMath.MinWindowLength(3)];
        double[] tooShort = new double[StationarityTestMath.MinWindowLength(3) - 1];
        for (int i = 0; i < ok.Length; i++)
        {
            ok[i] = 4.0 + 0.1 * Math.Sin(1.3 * i) + 0.01 * i * i % 7;
        }

        Array.Copy(ok, tooShort, tooShort.Length);
        double[] scratch = new double[StationarityTestMath.ScratchLength(ok.Length, 3)];
        Assert.True(StationarityTestMath.TryAugmentedDickeyFuller(ok, 3, Epsilon, scratch, out _));
        Assert.False(StationarityTestMath.TryAugmentedDickeyFuller(tooShort, 3, Epsilon, scratch, out _));
    }

    [Fact]
    public void FlatOrNonFiniteWindows_HaveNoResult()
    {
        double[] scratch = new double[StationarityTestMath.ScratchLength(40, 1)];
        double[] flat = new double[40];
        Array.Fill(flat, 4.6);
        Assert.False(StationarityTestMath.TryAugmentedDickeyFuller(flat, 1, Epsilon, scratch, out AugmentedDickeyFullerResult r));
        Assert.True(double.IsNaN(r.Tau) && double.IsNaN(r.Rho) && double.IsNaN(r.HalfLife));

        double[] withNaN = (double[])MeanReverting.Clone();
        withNaN[10] = double.NaN;
        Assert.False(StationarityTestMath.TryAugmentedDickeyFuller(withNaN, 1, Epsilon, scratch, out _));

        Assert.False(StationarityTestMath.TryAugmentedDickeyFuller(MeanReverting, -1, Epsilon, scratch, out _));
        Assert.False(StationarityTestMath.TryAugmentedDickeyFuller(MeanReverting, StationarityTestMath.MaxLagOrder + 1, Epsilon, new double[2000], out _));
        Assert.False(StationarityTestMath.TryAugmentedDickeyFuller(MeanReverting, 1, Epsilon, new double[10], out _)); // scratch too short
    }

    [Fact]
    public void PureLinearTrend_HasNoTauAndNoHalfLife()
    {
        // y = t: dy = 1 exactly, rho = 0 (a unit root), the fit is perfect.
        double[] y = new double[30];
        for (int t = 0; t < y.Length; t++)
        {
            y[t] = t;
        }

        AugmentedDickeyFullerResult r = Run(y, 0);
        Assert.True(double.IsNaN(r.Tau));
        Assert.Equal(0.0, r.Rho, 10);
        Assert.True(double.IsNaN(r.HalfLife));
    }

    [Fact]
    public void Kernel_AllocatesNothing()
    {
        double[] scratch = new double[StationarityTestMath.ScratchLength(MeanReverting.Length, 1)];
        StationarityTestMath.TryAugmentedDickeyFuller(MeanReverting, 1, Epsilon, scratch, out _);
        long allocated = AllocationProbe.Minimum(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                StationarityTestMath.TryAugmentedDickeyFuller(MeanReverting, 1, Epsilon, scratch, out _);
            }
        });

        Assert.True(allocated == 0, $"The kernels allocated {allocated} bytes in every one of {AllocationProbe.Samples} samples.");
    }
}
