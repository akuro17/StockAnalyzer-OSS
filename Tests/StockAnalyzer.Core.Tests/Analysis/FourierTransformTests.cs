#nullable enable
using System;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

/// <summary>
/// Cross-checks the fast <see cref="FourierTransform"/> engine against the frozen
/// O(N²) <see cref="DiscreteFourierTransformReference"/> oracle for both the radix-2
/// (power-of-two N) and Bluestein (arbitrary N) code paths.
/// </summary>
public class FourierTransformTests
{
    private const double RelativeTolerance = 1e-8;

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
    public void Forward_MatchesReferenceDft_ForArbitraryLength(int n)
    {
        var signal = MakeSignal(n, seed: 1234 + n);

        var (refReal, refImag) = DiscreteFourierTransformReference.Forward(signal);

        var fastReal = new double[n];
        var fastImag = new double[n];
        FourierTransform.Forward(signal, fastReal, fastImag);

        double scale = Math.Max(1.0, MaxAbs(refReal, refImag));
        for (int k = 0; k < n; k++)
        {
            Assert.True(
                Math.Abs(fastReal[k] - refReal[k]) / scale < RelativeTolerance,
                $"real[{k}] n={n}: fast={fastReal[k]} ref={refReal[k]}");
            Assert.True(
                Math.Abs(fastImag[k] - refImag[k]) / scale < RelativeTolerance,
                $"imag[{k}] n={n}: fast={fastImag[k]} ref={refImag[k]}");
        }
    }

    [Fact]
    public void Forward_DcBin_EqualsSumOfSamples()
    {
        var signal = MakeSignal(48, seed: 99);
        double sum = 0;
        foreach (var v in signal) sum += v;

        var re = new double[signal.Length];
        var im = new double[signal.Length];
        FourierTransform.Forward(signal, re, im);

        Assert.Equal(sum, re[0], precision: 9);
        Assert.Equal(0.0, im[0], precision: 9);
    }

    [Fact]
    public void Forward_AlternatingSignal_ConcentratesAtNyquistBin()
    {
        // x[i] = (-1)^i has all energy at k = n/2 (Nyquist), where X[n/2] = n.
        int n = 64;
        var signal = new double[n];
        for (int i = 0; i < n; i++) signal[i] = (i % 2 == 0) ? 1.0 : -1.0;

        var re = new double[n];
        var im = new double[n];
        FourierTransform.Forward(signal, re, im);

        Assert.Equal((double)n, re[n / 2], precision: 8);
        for (int k = 1; k < n; k++)
        {
            if (k == n / 2) continue;
            Assert.True(Math.Sqrt(re[k] * re[k] + im[k] * im[k]) < 1e-8, $"bin {k} should be ~0");
        }
    }

    [Fact]
    public void Forward_PureSine_PeaksAtExpectedBin()
    {
        int n = 100;
        double periodBars = 20.0; // k = n / periodBars = 5
        var signal = new double[n];
        for (int i = 0; i < n; i++)
        {
            signal[i] = 3.0 * Math.Sin(2.0 * Math.PI * i / periodBars);
        }

        var re = new double[n];
        var im = new double[n];
        FourierTransform.Forward(signal, re, im);

        int peak = 1;
        double peakMag = 0;
        for (int k = 1; k <= n / 2; k++)
        {
            double mag = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
            if (mag > peakMag)
            {
                peakMag = mag;
                peak = k;
            }
        }

        Assert.Equal(5, peak);
        // One-sided amplitude 2/n * |X[k]| ≈ 3.0
        Assert.Equal(3.0, (2.0 / n) * peakMag, precision: 6);
    }

    [Fact]
    public void Forward_IsLinear()
    {
        int n = 45; // Bluestein path
        var a = MakeSignal(n, seed: 7);
        var b = MakeSignal(n, seed: 8);
        var sum = new double[n];
        for (int i = 0; i < n; i++) sum[i] = a[i] + b[i];

        var (ar, ai) = ForwardArray(a);
        var (br, bi) = ForwardArray(b);
        var (sr, si) = ForwardArray(sum);

        for (int k = 0; k < n; k++)
        {
            Assert.Equal(ar[k] + br[k], sr[k], precision: 9);
            Assert.Equal(ai[k] + bi[k], si[k], precision: 9);
        }
    }

    [Fact]
    public void Forward_ThrowsWhenOutputTooSmall()
    {
        var signal = new double[8];
        Assert.Throws<ArgumentException>(() => FourierTransform.Forward(signal, new double[4], new double[8]));
    }

    [Fact]
    public void Forward_HandlesTrivialLengths()
    {
        var one = new double[] { 42.0 };
        var re = new double[1];
        var im = new double[1];
        FourierTransform.Forward(one, re, im);
        Assert.Equal(42.0, re[0], precision: 12);
        Assert.Equal(0.0, im[0], precision: 12);

        // n == 0 is a no-op.
        FourierTransform.Forward(Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>());
    }

    private static (double[] Real, double[] Imag) ForwardArray(double[] signal)
    {
        var re = new double[signal.Length];
        var im = new double[signal.Length];
        FourierTransform.Forward(signal, re, im);
        return (re, im);
    }

    private static double[] MakeSignal(int n, int seed)
    {
        var rng = new Random(seed);
        var signal = new double[n];
        for (int i = 0; i < n; i++)
        {
            // Deterministic mix: trend + two cycles + noise, in a price-like range.
            signal[i] = 100.0
                + 0.05 * i
                + 4.0 * Math.Sin(2.0 * Math.PI * i / 11.0)
                + 2.0 * Math.Cos(2.0 * Math.PI * i / 4.3)
                + (rng.NextDouble() - 0.5);
        }

        return signal;
    }

    private static double MaxAbs(double[] a, double[] b)
    {
        double m = 0;
        for (int i = 0; i < a.Length; i++)
        {
            m = Math.Max(m, Math.Abs(a[i]));
            m = Math.Max(m, Math.Abs(b[i]));
        }

        return m;
    }
}
