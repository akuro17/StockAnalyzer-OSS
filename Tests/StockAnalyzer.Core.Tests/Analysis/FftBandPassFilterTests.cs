#nullable enable
using System;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

/// <summary>
/// Verifies <see cref="FftBandPassFilter.RollingCausalTrend"/> against an inline naive
/// peak-detect + band-mask + irfft reference (built on <see cref="DiscreteFourierTransformReference"/>)
/// plus the defining behavioural properties of the filter.
/// </summary>
public class FftBandPassFilterTests
{
    private const double RelativeTolerance = 1e-6;

    [Theory]
    [InlineData(16, 0)]
    [InlineData(16, 2)]
    [InlineData(17, 0)]   // odd window: no exact Nyquist bin
    [InlineData(17, 3)]
    [InlineData(32, 1)]
    [InlineData(32, 5)]
    [InlineData(33, 2)]   // odd window
    [InlineData(45, 4)]   // odd, non-power-of-2 -> Bluestein path inside FourierTransform
    [InlineData(64, 8)]
    public void RollingCausalTrend_MatchesNaiveReference(int windowSize, int bandWidthBins)
    {
        double[] samples = MakeSeries(200, seed: 900 + windowSize * 31 + bandWidthBins);

        var actual = new double[samples.Length];
        FftBandPassFilter.RollingCausalTrend(samples, windowSize, bandWidthBins, actual);

        double[] expected = NaiveRollingCausalBandPass(samples, windowSize, bandWidthBins);

        double scale = Math.Max(1.0, MaxAbsFinite(expected));
        for (int i = 0; i < samples.Length; i++)
        {
            if (double.IsNaN(expected[i]))
            {
                Assert.True(double.IsNaN(actual[i]), $"index {i}: expected NaN");
                continue;
            }

            Assert.True(
                Math.Abs(actual[i] - expected[i]) / scale < RelativeTolerance,
                $"w={windowSize} bw={bandWidthBins} i={i}: {actual[i]} vs {expected[i]}");
        }
    }

    [Fact]
    public void RollingCausalTrend_WarmupAndInsufficientData_AreNaN()
    {
        int w = 16;
        double[] samples = MakeSeries(40, seed: 33);

        var trend = new double[samples.Length];
        FftBandPassFilter.RollingCausalTrend(samples, w, bandWidthBins: 2, trend);

        for (int i = 0; i < w - 1; i++)
        {
            Assert.True(double.IsNaN(trend[i]), $"warmup index {i} should be NaN");
        }

        Assert.False(double.IsNaN(trend[w - 1]));

        var tooShort = new double[w - 1];
        var shortTrend = new double[w - 1];
        FftBandPassFilter.RollingCausalTrend(tooShort, w, bandWidthBins: 2, shortTrend);
        Assert.All(shortTrend, v => Assert.True(double.IsNaN(v)));
    }

    [Fact]
    public void RollingCausalTrend_IsCausal_FutureSamplesDoNotAffectEarlierOutput()
    {
        int w = 24;
        double[] baseSamples = MakeSeries(150, seed: 44);

        var baseTrend = new double[baseSamples.Length];
        FftBandPassFilter.RollingCausalTrend(baseSamples, w, bandWidthBins: 3, baseTrend);

        int mutateAt = 100;
        double[] mutated = (double[])baseSamples.Clone();
        mutated[mutateAt] += 25.0;

        var mutatedTrend = new double[mutated.Length];
        FftBandPassFilter.RollingCausalTrend(mutated, w, bandWidthBins: 3, mutatedTrend);

        for (int i = w - 1; i < mutateAt; i++)
        {
            Assert.Equal(baseTrend[i], mutatedTrend[i], precision: 10);
        }
    }

    [Fact]
    public void RollingCausalTrend_PureSine_ZeroBandWidth_IsImmuneToLargeBaseline()
    {
        // A pure sine at an exact integer bin (period divides the window) with bandWidthBins=0
        // keeps only that single bin (+ mirror); DC (bin 0) is structurally excluded regardless
        // of how large a baseline is added, so the reconstruction should match the zero-baseline
        // sine shape almost exactly even when a large baseline is present. This differs from
        // FftAnalyticSignal (Hilbert phase), which is sensitive to a DC-dominated input; the
        // band-pass mask here never touches bin 0 at all.
        int w = 32;
        double period = 8.0; // bin k = w/period = 4, an exact integer -> no spectral leakage
        int n = 120;

        double[] zeroBaseline = new double[n];
        double[] largeBaseline = new double[n];
        for (int i = 0; i < n; i++)
        {
            double s = 5.0 * Math.Sin(2.0 * Math.PI * i / period);
            zeroBaseline[i] = s;
            largeBaseline[i] = s + 500.0; // 100x the oscillation amplitude
        }

        var trendZero = new double[n];
        var trendLarge = new double[n];
        FftBandPassFilter.RollingCausalTrend(zeroBaseline, w, bandWidthBins: 0, trendZero);
        FftBandPassFilter.RollingCausalTrend(largeBaseline, w, bandWidthBins: 0, trendLarge);

        for (int i = w - 1; i < n; i++)
        {
            Assert.True(Math.Abs(trendLarge[i] - trendZero[i]) < 1e-6,
                $"i={i}: baseline should not leak into the band-pass output ({trendLarge[i]} vs {trendZero[i]})");
        }
    }

    [Fact]
    public void RollingCausalTrend_ThrowsWhenOutputTooShort()
    {
        var samples = new double[50];
        Assert.Throws<ArgumentException>(() => FftBandPassFilter.RollingCausalTrend(samples, 16, 2, new double[49]));
    }

    [Fact]
    public void RollingCausalTrend_BandWidthBeyondHalf_ClampsWithoutThrowing()
    {
        int w = 16;
        double[] samples = MakeSeries(60, seed: 77);

        var trend = new double[samples.Length];
        // bandWidthBins far larger than w/2 must clamp internally, not throw or read out of range.
        FftBandPassFilter.RollingCausalTrend(samples, w, bandWidthBins: 999, trend);

        Assert.False(double.IsNaN(trend[w - 1]));
    }

    // --- inline naive peak-detect + band-mask + irfft reference ---

    private static double[] NaiveRollingCausalBandPass(double[] samples, int windowSize, int bandWidthBins)
    {
        int n = samples.Length;
        int w = Math.Max(4, windowSize);
        int half = w / 2;
        int bandWidth = Math.Clamp(bandWidthBins, 0, half);

        var trend = new double[n];
        for (int i = 0; i < n; i++) trend[i] = double.NaN;
        if (n < w) return trend;

        for (int i = w - 1; i < n; i++)
        {
            var seg = new double[w];
            Array.Copy(samples, i - w + 1, seg, 0, w);

            var (sr, si) = DiscreteFourierTransformReference.Forward(seg);

            int peakBin = 1;
            double peakMagSq = (sr[1] * sr[1]) + (si[1] * si[1]);
            for (int k = 2; k <= half; k++)
            {
                double magSq = (sr[k] * sr[k]) + (si[k] * si[k]);
                if (magSq > peakMagSq)
                {
                    peakMagSq = magSq;
                    peakBin = k;
                }
            }

            int lowBin = Math.Max(1, peakBin - bandWidth);
            int highBin = Math.Min(half, peakBin + bandWidth);

            var fr = new double[w];
            var fi = new double[w];
            for (int k = lowBin; k <= highBin; k++)
            {
                int mirror = w - k;
                if (mirror == k)
                {
                    fr[k] = sr[k];
                    fi[k] = 0.0;
                }
                else
                {
                    fr[k] = sr[k];
                    fi[k] = si[k];
                    fr[mirror] = sr[k];
                    fi[mirror] = -si[k];
                }
            }

            var (rr, _) = DiscreteFourierTransformReference.Inverse(fr, fi);
            trend[i] = rr[w - 1];
        }

        return trend;
    }

    private static double[] MakeSeries(int n, int seed)
    {
        var rng = new Random(seed);
        var s = new double[n];
        for (int i = 0; i < n; i++)
        {
            s[i] = 100.0
                + 0.08 * i
                + 6.0 * Math.Sin(2.0 * Math.PI * i / 19.0)
                + 3.0 * Math.Cos(2.0 * Math.PI * i / 7.0)
                + (rng.NextDouble() - 0.5) * 2.0;
        }

        return s;
    }

    private static double MaxAbsFinite(double[] a)
    {
        double m = 0;
        foreach (double v in a)
        {
            if (!double.IsNaN(v))
            {
                m = Math.Max(m, Math.Abs(v));
            }
        }

        return m;
    }
}
