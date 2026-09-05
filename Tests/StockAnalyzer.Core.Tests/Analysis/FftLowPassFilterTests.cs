#nullable enable
using System;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

/// <summary>
/// Verifies <see cref="FftLowPassFilter.RollingCausalTrend"/> against an inline naive
/// <c>rfft</c>/<c>irfft</c> reference (built on <see cref="DiscreteFourierTransformReference"/>)
/// plus the defining behavioural properties of the filter.
/// </summary>
public class FftLowPassFilterTests
{
    private const double RelativeTolerance = 1e-6;

    [Theory]
    [InlineData(8, 1)]
    [InlineData(8, 2)]
    [InlineData(8, 5)]      // w/2 + 1
    [InlineData(16, 1)]
    [InlineData(16, 4)]
    [InlineData(16, 9)]
    [InlineData(24, 3)]
    [InlineData(24, 13)]
    [InlineData(32, 4)]
    [InlineData(32, 17)]
    [InlineData(50, 4)]
    [InlineData(50, 26)]
    [InlineData(64, 4)]
    [InlineData(64, 33)]
    public void RollingCausalTrend_MatchesNaiveRfftIrfftReference(int windowSize, int numHarmonics)
    {
        double[] samples = MakeSeries(220, seed: 700 + windowSize * 31 + numHarmonics);

        var actual = new double[samples.Length];
        FftLowPassFilter.RollingCausalTrend(samples, windowSize, numHarmonics, actual);

        double[] expected = NaiveRollingCausalTrend(samples, windowSize, numHarmonics);

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
                $"w={windowSize} h={numHarmonics} i={i}: {actual[i]} vs {expected[i]}");
        }
    }

    [Fact]
    public void RollingCausalTrend_SingleHarmonic_EqualsRollingWindowMean()
    {
        int w = 20;
        double[] samples = MakeSeries(120, seed: 11);

        var trend = new double[samples.Length];
        FftLowPassFilter.RollingCausalTrend(samples, w, numHarmonics: 1, trend);

        for (int i = w - 1; i < samples.Length; i++)
        {
            double mean = 0;
            for (int j = i - w + 1; j <= i; j++) mean += samples[j];
            mean /= w;

            Assert.True(Math.Abs(trend[i] - mean) / Math.Max(1.0, Math.Abs(mean)) < RelativeTolerance,
                $"i={i}: {trend[i]} vs mean {mean}");
        }
    }

    [Fact]
    public void RollingCausalTrend_AllHarmonics_IsPassThrough()
    {
        int w = 32;
        double[] samples = MakeSeries(160, seed: 22);

        var trend = new double[samples.Length];
        FftLowPassFilter.RollingCausalTrend(samples, w, numHarmonics: w / 2 + 1, trend);

        for (int i = w - 1; i < samples.Length; i++)
        {
            Assert.True(Math.Abs(trend[i] - samples[i]) / Math.Max(1.0, Math.Abs(samples[i])) < RelativeTolerance,
                $"i={i}: {trend[i]} vs sample {samples[i]}");
        }
    }

    [Fact]
    public void RollingCausalTrend_WarmupAndInsufficientData_AreNaN()
    {
        int w = 16;
        double[] samples = MakeSeries(40, seed: 33);

        var trend = new double[samples.Length];
        FftLowPassFilter.RollingCausalTrend(samples, w, numHarmonics: 4, trend);

        for (int i = 0; i < w - 1; i++)
        {
            Assert.True(double.IsNaN(trend[i]), $"warmup index {i} should be NaN");
        }

        Assert.False(double.IsNaN(trend[w - 1]));

        var tooShort = new double[w - 1];
        var shortTrend = new double[w - 1];
        FftLowPassFilter.RollingCausalTrend(tooShort, w, numHarmonics: 4, shortTrend);
        Assert.All(shortTrend, v => Assert.True(double.IsNaN(v)));
    }

    [Fact]
    public void RollingCausalTrend_IsCausal_FutureSamplesDoNotAffectEarlierOutput()
    {
        int w = 24;
        double[] baseSamples = MakeSeries(150, seed: 44);

        var baseTrend = new double[baseSamples.Length];
        FftLowPassFilter.RollingCausalTrend(baseSamples, w, numHarmonics: 5, baseTrend);

        int mutateAt = 100;
        double[] mutated = (double[])baseSamples.Clone();
        mutated[mutateAt] += 25.0;

        var mutatedTrend = new double[mutated.Length];
        FftLowPassFilter.RollingCausalTrend(mutated, w, numHarmonics: 5, mutatedTrend);

        for (int i = w - 1; i < mutateAt; i++)
        {
            Assert.Equal(baseTrend[i], mutatedTrend[i], precision: 10);
        }
    }

    [Fact]
    public void RollingCausalTrend_NonPowerOfTwoWindow_MatchesReference()
    {
        double[] samples = MakeSeries(180, seed: 55);
        int windowSize = 45; // Bluestein path inside FourierTransform

        var actual = new double[samples.Length];
        FftLowPassFilter.RollingCausalTrend(samples, windowSize, numHarmonics: 6, actual);

        double[] expected = NaiveRollingCausalTrend(samples, windowSize, 6);

        double scale = Math.Max(1.0, MaxAbsFinite(expected));
        for (int i = 0; i < samples.Length; i++)
        {
            if (double.IsNaN(expected[i]))
            {
                Assert.True(double.IsNaN(actual[i]));
                continue;
            }

            Assert.True(Math.Abs(actual[i] - expected[i]) / scale < RelativeTolerance, $"i={i}");
        }
    }

    [Fact]
    public void RollingCausalTrend_ThrowsWhenOutputTooShort()
    {
        var samples = new double[50];
        Assert.Throws<ArgumentException>(() => FftLowPassFilter.RollingCausalTrend(samples, 16, 4, new double[49]));
    }

    // --- inline naive rfft/irfft reference ---

    private static double[] NaiveRollingCausalTrend(double[] samples, int windowSize, int numHarmonics)
    {
        int n = samples.Length;
        int w = Math.Max(4, windowSize);
        int h = Math.Clamp(numHarmonics, 1, (w / 2) + 1);

        var trend = new double[n];
        for (int i = 0; i < n; i++) trend[i] = double.NaN;
        if (n < w) return trend;

        for (int i = w - 1; i < n; i++)
        {
            var seg = new double[w];
            Array.Copy(samples, i - w + 1, seg, 0, w);

            var (sr, si) = DiscreteFourierTransformReference.Forward(seg);

            var fr = new double[w];
            var fi = new double[w];
            fr[0] = sr[0];
            fi[0] = 0.0;
            for (int k = 1; k < h; k++)
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
