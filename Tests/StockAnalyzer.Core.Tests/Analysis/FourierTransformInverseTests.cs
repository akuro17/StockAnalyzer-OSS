#nullable enable
using System;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

/// <summary>
/// Verifies <see cref="FourierTransform.Inverse"/>: exact round-trip against <see cref="FourierTransform.Forward"/>,
/// cross-check against the frozen naive inverse-DFT oracle, Hermitian→real reconstruction, and linearity,
/// for both the radix-2 (power-of-two N) and Bluestein (arbitrary N) code paths.
/// </summary>
public class FourierTransformInverseTests
{
    private const double RoundTripTolerance = 1e-9;
    private const double OracleRelativeTolerance = 1e-8;

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(13)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(50)]
    [InlineData(60)]
    [InlineData(64)]
    [InlineData(100)]
    [InlineData(128)]
    [InlineData(257)]
    public void Inverse_OfForward_ReconstructsOriginalSignal(int n)
    {
        double[] signal = MakeRealSignal(n, seed: 5000 + n);

        var xr = new double[n];
        var xi = new double[n];
        FourierTransform.Forward(signal, xr, xi);

        var yr = new double[n];
        var yi = new double[n];
        FourierTransform.Inverse(xr, xi, yr, yi);

        double scale = Math.Max(1.0, MaxAbs(signal));
        for (int i = 0; i < n; i++)
        {
            Assert.True(Math.Abs(yr[i] - signal[i]) / scale < RoundTripTolerance, $"re[{i}] n={n}: {yr[i]} vs {signal[i]}");
            Assert.True(Math.Abs(yi[i]) / scale < RoundTripTolerance, $"im[{i}] n={n}: {yi[i]} should be ~0");
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(9)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(45)]
    [InlineData(64)]
    [InlineData(97)]
    [InlineData(128)]
    [InlineData(200)]
    public void Inverse_MatchesNaiveInverseDftOracle_ForComplexSpectrum(int n)
    {
        var (xr, xi) = MakeComplexSpectrum(n, seed: 9000 + n);

        var (refReal, refImag) = DiscreteFourierTransformReference.Inverse(xr, xi);

        var fastReal = new double[n];
        var fastImag = new double[n];
        FourierTransform.Inverse(xr, xi, fastReal, fastImag);

        double scale = Math.Max(1.0, Math.Max(MaxAbs(refReal), MaxAbs(refImag)));
        for (int i = 0; i < n; i++)
        {
            Assert.True(Math.Abs(fastReal[i] - refReal[i]) / scale < OracleRelativeTolerance, $"re[{i}] n={n}");
            Assert.True(Math.Abs(fastImag[i] - refImag[i]) / scale < OracleRelativeTolerance, $"im[{i}] n={n}");
        }
    }

    [Fact]
    public void Inverse_OfHermitianSpectrum_ProducesRealSignal()
    {
        // Forward of a real signal is Hermitian-symmetric; its inverse must be real.
        int n = 96;
        double[] signal = MakeRealSignal(n, seed: 314);

        var xr = new double[n];
        var xi = new double[n];
        FourierTransform.Forward(signal, xr, xi);

        var yr = new double[n];
        var yi = new double[n];
        FourierTransform.Inverse(xr, xi, yr, yi);

        for (int i = 0; i < n; i++)
        {
            Assert.True(Math.Abs(yi[i]) < 1e-9, $"imag[{i}] = {yi[i]} should be ~0 for a Hermitian spectrum");
        }
    }

    [Fact]
    public void Inverse_IsLinear()
    {
        int n = 45; // Bluestein path
        var (ar, ai) = MakeComplexSpectrum(n, seed: 11);
        var (br, bi) = MakeComplexSpectrum(n, seed: 12);

        var sumR = new double[n];
        var sumI = new double[n];
        for (int i = 0; i < n; i++)
        {
            sumR[i] = ar[i] + br[i];
            sumI[i] = ai[i] + bi[i];
        }

        var (invAr, invAi) = InverseArray(n, ar, ai);
        var (invBr, invBi) = InverseArray(n, br, bi);
        var (invSr, invSi) = InverseArray(n, sumR, sumI);

        for (int i = 0; i < n; i++)
        {
            Assert.Equal(invAr[i] + invBr[i], invSr[i], precision: 9);
            Assert.Equal(invAi[i] + invBi[i], invSi[i], precision: 9);
        }
    }

    [Fact]
    public void Inverse_HandlesTrivialLengths()
    {
        FourierTransform.Inverse(Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>());

        var re = new double[1];
        var im = new double[1];
        FourierTransform.Inverse(new[] { 7.5 }, new[] { -2.5 }, re, im);
        Assert.Equal(7.5, re[0], precision: 12);
        Assert.Equal(-2.5, im[0], precision: 12);
    }

    [Fact]
    public void Inverse_ThrowsOnInvalidSpans()
    {
        Assert.Throws<ArgumentException>(() =>
            FourierTransform.Inverse(new double[8], new double[7], new double[8], new double[8]));

        Assert.Throws<ArgumentException>(() =>
            FourierTransform.Inverse(new double[8], new double[8], new double[4], new double[8]));
    }

    private static (double[] Real, double[] Imag) InverseArray(int n, double[] xr, double[] xi)
    {
        var re = new double[n];
        var im = new double[n];
        FourierTransform.Inverse(xr, xi, re, im);
        return (re, im);
    }

    private static double[] MakeRealSignal(int n, int seed)
    {
        var rng = new Random(seed);
        var s = new double[n];
        for (int i = 0; i < n; i++)
        {
            s[i] = 100.0
                + 0.05 * i
                + 4.0 * Math.Sin(2.0 * Math.PI * i / 11.0)
                + 2.0 * Math.Cos(2.0 * Math.PI * i / 4.3)
                + (rng.NextDouble() - 0.5);
        }

        return s;
    }

    private static (double[] Real, double[] Imag) MakeComplexSpectrum(int n, int seed)
    {
        var rng = new Random(seed);
        var re = new double[n];
        var im = new double[n];
        for (int i = 0; i < n; i++)
        {
            re[i] = rng.NextDouble() * 20.0 - 10.0;
            im[i] = rng.NextDouble() * 20.0 - 10.0;
        }

        return (re, im);
    }

    private static double MaxAbs(double[] a)
    {
        double m = 0;
        for (int i = 0; i < a.Length; i++)
        {
            m = Math.Max(m, Math.Abs(a[i]));
        }

        return m;
    }
}
