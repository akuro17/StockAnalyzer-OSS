using System;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Tests.Common;
using Xunit;

namespace StockAnalyzer.Core.Tests.MathUtils;

/// <summary>
/// Pins <see cref="SerialDependenceMath"/>: the Ljung-Box statistic and p-value to numpy / scipy (chi2.sf) on fixed windows, the effective lag rule of owner decision U-5 = A,
/// and the variance-ratio test to the existing <see cref="HurstMath.VarianceRatio"/> (no second formula) with the two-sided normal p-value of its Z*.
/// <para>
/// Reference recipe: numpy autocovariances gamma_k = (1 / n) sum (x_t - mean)(x_(t-k) - mean), Q = n (n + 2) sum rho_k^2 / (n - k), p = scipy.stats.chi2.sf(Q, m).
/// Re-run on 2026-11-18: Q and p of WindowA (m = 3) and WindowC (m = 8) reproduce the embedded literals exactly (relative difference 0 at 15 digits).
/// The variance ratio is checked against the in-file transcription below, not against a library: statsmodels and arch (which have a variance-ratio test) are not installed here.
/// Environment of 2026-11-18: Python with scipy 1.18.1, mpmath 1.3.0, numpy 2.5.2; statsmodels and arch are NOT installed.
/// </para>
/// </summary>
public class SerialDependenceMathTests
{
    [Fact]
    public void LjungBox_MatchesTheReference()
    {
        Assert.True(SerialDependenceMath.TryLjungBox(WindowMomentsTests.WindowA, 3, out double qA, out double pA));
        Assert.Equal(4.222378057928451, qA, 11);
        Assert.Equal(0.2384309456187162, pA, 12);

        Assert.True(SerialDependenceMath.TryLjungBox(WindowMomentsTests.WindowC, 3, out double qC3, out double pC3));
        Assert.Equal(4.745132083476314, qC3, 11);
        Assert.Equal(0.19143982028968562, pC3, 12);

        Assert.True(SerialDependenceMath.TryLjungBox(WindowMomentsTests.WindowC, 8, out double qC8, out double pC8));
        Assert.Equal(5.036413912677799, qC8, 11);
        Assert.Equal(0.7536771676198712, pC8, 12);
    }

    [Fact]
    public void LjungBox_HandVector_AlternatingSeriesHasStrongNegativeFirstAutocorrelation()
    {
        // x = +1, -1, +1, -1, ... (n = 10): mean 0, gamma0 = 1, gamma1 = -0.9, gamma2 = 0.8; Q(2) = 10 * 12 * (0.81 / 9 + 0.64 / 8) = 120 * 0.17 = 20.4.
        var x = new double[10];
        for (int i = 0; i < x.Length; i++)
        {
            x[i] = i % 2 == 0 ? 1.0 : -1.0;
        }

        Assert.True(SerialDependenceMath.TryLjungBox(x, 2, out double q, out double p));
        Assert.Equal(20.4, q, 12);
        Assert.Equal(Math.Exp(-20.4 / 2.0), p, 14); // two degrees of freedom: the upper tail is exp(-Q / 2)
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(8)]
    [InlineData(9)]
    public void LjungBox_RejectsALagOutsideOneToWindowMinusOne(int lags)
    {
        Assert.False(SerialDependenceMath.TryLjungBox(WindowMomentsTests.WindowA, lags, out double q, out double p));
        Assert.True(double.IsNaN(q));
        Assert.True(double.IsNaN(p));
    }

    [Fact]
    public void LjungBox_RejectsAFlatOrNonFiniteWindow()
    {
        var flat = new double[20];
        Array.Fill(flat, 0.003);
        Assert.False(SerialDependenceMath.TryLjungBox(flat, 4, out _, out _));
        Assert.False(SerialDependenceMath.TryLjungBox(new[] { 0.01, double.NaN, 0.02, -0.01, 0.0, 0.03 }, 2, out _, out _));
        Assert.False(SerialDependenceMath.TryLjungBox(ReadOnlySpan<double>.Empty, 1, out _, out _));
    }

    [Theory]
    [InlineData(60, 10, 10)]
    [InlineData(20, 10, 4)]
    [InlineData(59, 30, 11)]
    [InlineData(4, 10, 0)]
    [InlineData(500, 30, 30)]
    public void EffectiveLjungBoxLag_IsTheSmallerOfMaxLagAndAFifthOfTheWindow(int window, int maxLag, int expected) =>
        Assert.Equal(expected, SerialDependenceMath.EffectiveLjungBoxLag(window, maxLag));

    [Fact]
    public void VarianceRatio_IsTheExistingEstimatorPlusTheTwoSidedPValue()
    {
        double[] window = WindowMomentsTests.WindowC;
        foreach (int q in new[] { 2, 3, 5, 8 })
        {
            double expectedVr = HurstMath.VarianceRatio(window, q, out double expectedZ);
            VarianceRatioTestResult result = SerialDependenceMath.VarianceRatio(window, q);
            Assert.Equal(expectedVr, result.VarianceRatio);
            Assert.Equal(expectedZ, result.ZStar);
            Assert.True(double.IsFinite(result.ZStar), $"q={q}: Z* must be defined for this window");
            Assert.Equal(SpecialFunctionsMath.Erfc(Math.Abs(expectedZ) / Math.Sqrt(2.0)), result.PValue);
            Assert.InRange(result.PValue, 0.0, 1.0);
        }
    }

    [Fact]
    public void VarianceRatio_UndefinedWhereTheExistingEstimatorIsUndefined()
    {
        double[] window = WindowMomentsTests.WindowC;
        foreach (VarianceRatioTestResult result in new[]
                 {
                     SerialDependenceMath.VarianceRatio(window, 1),
                     SerialDependenceMath.VarianceRatio(window.AsSpan(0, 9), 5),
                     SerialDependenceMath.VarianceRatio(new double[30], 5),
                 })
        {
            Assert.True(double.IsNaN(result.VarianceRatio));
            Assert.True(double.IsNaN(result.ZStar));
            Assert.True(double.IsNaN(result.PValue));
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // Independent reference of the Lo-MacKinlay variance ratio. The test above only proves that SerialDependenceMath calls HurstMath.VarianceRatio; the checks below compare
    // that estimator with a second, deliberately plain O(n q) transcription of the paper (Lo and MacKinlay 1988, with the 1990 unbiased overlapping estimator), so that
    // "the same bug in both" is impossible unless it is also in this transcription.
    //   d_t = x_t - mean; sigma_a^2 = sum d^2 / (n - 1); m = q (n - q + 1) (1 - q / n); sigma_c^2 = sum_{t=q-1}^{n-1} (sum_{i<q} d_{t-i})^2 / m; VR = sigma_c^2 / sigma_a^2;
    //   delta_j = sum_{t=j}^{n-1} d_t^2 d_{t-j}^2 / (sum d^2)^2; theta = sum_{j=1}^{q-1} (2 (q - j) / q)^2 delta_j; Z* = (VR - 1) / sqrt(theta).
    // The paper's z* = sqrt(nq) (VR - 1) / sqrt(theta_hat) carries the factor nq in delta_hat as well; the two factors cancel, which gives the form above.
    // Recipe of the order of the synthetic windows: System.Random with the seeds below, Box-Muller normals x 0.01, AR(1) x_t = phi x_(t-1) + e_t.
    // ------------------------------------------------------------------------------------------------------------------------------

    private static (double Vr, double ZStar, double PValue) ReferenceVarianceRatio(double[] x, int q)
    {
        int n = x.Length;
        double mean = 0.0;
        for (int i = 0; i < n; i++)
        {
            mean += x[i];
        }

        mean /= n;
        double[] d = new double[n];
        double sumSquares = 0.0;
        for (int i = 0; i < n; i++)
        {
            d[i] = x[i] - mean;
            sumSquares += d[i] * d[i];
        }

        double sigmaA2 = sumSquares / (n - 1);
        double m = q * (double)(n - q + 1) * (1.0 - ((double)q / n));
        double sumOfBlocks = 0.0;
        for (int t = q - 1; t < n; t++)
        {
            double block = 0.0;
            for (int i = 0; i < q; i++)
            {
                block += d[t - i];
            }

            sumOfBlocks += block * block;
        }

        double vr = (sumOfBlocks / m) / sigmaA2;
        double theta = 0.0;
        for (int j = 1; j < q; j++)
        {
            double cross = 0.0;
            for (int t = j; t < n; t++)
            {
                cross += d[t] * d[t] * d[t - j] * d[t - j];
            }

            double delta = cross / (sumSquares * sumSquares);
            double weight = 2.0 * (q - j) / q;
            theta += weight * weight * delta;
        }

        double z = (vr - 1.0) / Math.Sqrt(theta);
        return (vr, z, SpecialFunctionsMath.Erfc(Math.Abs(z) / Math.Sqrt(2.0)));
    }

    private static double[] SyntheticReturns(int count, int seed, double phi)
    {
        var random = new Random(seed);
        double[] x = new double[count];
        double previous = 0.0;
        for (int i = 0; i < count; i++)
        {
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            double noise = 0.01 * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            previous = (phi * previous) + noise;
            x[i] = previous;
        }

        return x;
    }

    private static void AssertRelative(double expected, double actual, double tolerance, string what)
    {
        double scale = Math.Max(1.0, Math.Abs(expected));
        Assert.True(Math.Abs(actual - expected) <= tolerance * scale, $"{what}: expected {expected:R}, actual {actual:R}");
    }

    [Theory]
    [InlineData(0.0, 2)]
    [InlineData(0.0, 5)]
    [InlineData(0.0, 10)]
    [InlineData(0.5, 2)]
    [InlineData(0.5, 5)]
    [InlineData(0.5, 10)]
    [InlineData(-0.5, 2)]
    [InlineData(-0.5, 5)]
    [InlineData(-0.5, 10)]
    public void VarianceRatio_EqualsAnIndependentTranscriptionOfTheLoMacKinlayEstimator(double phi, int horizon)
    {
        double[] window = SyntheticReturns(120, 20261118, phi);
        (double vr, double z, double p) = ReferenceVarianceRatio(window, horizon);
        VarianceRatioTestResult result = SerialDependenceMath.VarianceRatio(window, horizon);

        AssertRelative(vr, result.VarianceRatio, 1e-12, "VR");
        AssertRelative(z, result.ZStar, 1e-12, "Z*");
        AssertRelative(p, result.PValue, 1e-12, "p-value");
    }

    [Fact]
    public void VarianceRatio_OfSeriallyDependentWindows_LiesOnTheExpectedSideOfOne()
    {
        // Positive autocorrelation -> momentum -> VR above 1; negative -> mean reversion -> VR below 1 (aggregation horizon 5, 120 seeded values).
        Assert.True(SerialDependenceMath.VarianceRatio(SyntheticReturns(120, 20261118, 0.5), 5).VarianceRatio > 1.0);
        Assert.True(SerialDependenceMath.VarianceRatio(SyntheticReturns(120, 20261118, -0.5), 5).VarianceRatio < 1.0);
    }

    [Fact]
    public void VarianceRatio_NeedsTwoHorizonsPlusTwoValues()
    {
        double[] six = { 0.01, -0.02, 0.015, 0.0, -0.005, 0.02 };
        Assert.True(double.IsNaN(SerialDependenceMath.VarianceRatio(six.AsSpan(0, 5), 2).VarianceRatio)); // n = 2q + 1
        Assert.True(double.IsFinite(SerialDependenceMath.VarianceRatio(six, 2).VarianceRatio)); // n = 2q + 2
    }

    [Fact]
    public void Kernels_AllocateNothing()
    {
        double[] window = WindowMomentsTests.WindowC;
        SerialDependenceMath.TryLjungBox(window, 8, out _, out _);
        SerialDependenceMath.VarianceRatio(window, 5);
        long allocated = AllocationProbe.Minimum(() =>
        {
            for (int round = 0; round < 50; round++)
            {
                SerialDependenceMath.TryLjungBox(window, 8, out _, out _);
                SerialDependenceMath.VarianceRatio(window, 5);
            }
        });

        Assert.True(allocated == 0, $"The kernels allocated {allocated} bytes in every one of {AllocationProbe.Samples} samples.");
    }
}
