#nullable enable
using System;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

/// <summary>
/// Verifies <see cref="FftAnalyticSignal.RollingCausalAnalyticSignal"/> against an inline naive
/// analytic-signal reference (built on <see cref="DiscreteFourierTransformReference"/>) plus the
/// defining behavioural properties of the filter (causality, warm-up, pure-tone response).
/// </summary>
public class FftAnalyticSignalTests
{
    private const double RelativeTolerance = 1e-6;

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(50)]
    [InlineData(64)]
    public void RollingCausalAnalyticSignal_MatchesNaiveReference_EvenWindows(int windowSize)
    {
        AssertMatchesReference(windowSize, seed: 300 + windowSize);
    }

    [Fact]
    public void RollingCausalAnalyticSignal_MatchesNaiveReference_OddWindow_BluesteinPath()
    {
        AssertMatchesReference(windowSize: 45, seed: 999);
    }

    [Fact]
    public void RollingCausalAnalyticSignal_PureSine_PhaseAdvancesLinearly_EnvelopeConstant()
    {
        int w = 32;
        const double period = 16.0;
        int n = 200;
        var samples = new double[n];
        for (int i = 0; i < n; i++)
        {
            samples[i] = Math.Sin(2.0 * Math.PI * i / period);
        }

        var phase = new double[n];
        var env = new double[n];
        FftAnalyticSignal.RollingCausalAnalyticSignal(samples, w, phase, env);

        // Envelope of a unit sine's analytic signal is ~1 and nearly flat after warm-up.
        double envMin = double.MaxValue;
        double envMax = double.MinValue;
        for (int i = w - 1; i < n; i++)
        {
            envMin = Math.Min(envMin, env[i]);
            envMax = Math.Max(envMax, env[i]);
        }

        Assert.True(envMax - envMin < 0.05, $"envelope should be flat, spread was {envMax - envMin}");
        Assert.True(Math.Abs(((envMin + envMax) / 2.0) - 1.0) < 0.05, $"envelope should sit near 1, was {(envMin + envMax) / 2.0}");

        // Per-bar phase advance equals one step of 2π/period once unwrapped.
        double expectedStep = 2.0 * Math.PI / period;
        int checkedSteps = 0;
        for (int i = w; i < n; i++)
        {
            double d = phase[i] - phase[i - 1];
            while (d <= -Math.PI) d += 2.0 * Math.PI;
            while (d > Math.PI) d -= 2.0 * Math.PI;
            Assert.True(Math.Abs(d - expectedStep) < 1e-3, $"i={i}: phase step {d} vs {expectedStep}");
            checkedSteps++;
        }

        Assert.True(checkedSteps > 100);
    }

    [Fact]
    public void RollingCausalAnalyticSignal_OddWindow_TopmostPositiveBinIsNotDropped()
    {
        // For an odd window w, the topmost positive-frequency bin is k = w/2 (integer division,
        // e.g. 4 for w=9) -- there is no exact Nyquist bin for odd w, so this bin must be doubled
        // like every other positive bin. A pure tone whose frequency IS exactly that bin (global,
        // continuous sinusoid, not windowed/tapered) has ALL of its spectral energy there: every
        // w-length window captures exactly one full DFT-bin's worth of cycles regardless of the
        // window's start offset, so the reconstructed envelope must sit near the input amplitude
        // (1.0) at every position once warmed up, and must NOT collapse toward 0. This check does
        // not reuse the production masking loop (unlike NaiveRollingCausalAnalyticSignal below),
        // so it independently exercises the real physical behaviour of the filter.
        int w = 9;
        int topmostPositiveBin = w / 2; // 4
        int n = 200;
        var samples = new double[n];
        for (int i = 0; i < n; i++)
        {
            samples[i] = Math.Sin(2.0 * Math.PI * topmostPositiveBin * i / w);
        }

        var phase = new double[n];
        var env = new double[n];
        FftAnalyticSignal.RollingCausalAnalyticSignal(samples, w, phase, env);

        for (int i = w - 1; i < n; i++)
        {
            Assert.True(Math.Abs(env[i] - 1.0) < 0.05,
                $"i={i}: envelope should sit near 1.0 (topmost odd-window bin must not be dropped), was {env[i]}");
        }
    }

    [Fact]
    public void RollingCausalAnalyticSignal_RealPartOfReconstruction_EqualsInputSample()
    {
        // The analytic signal's real part is the original signal; the emitted envelope/phase
        // therefore satisfy env*cos(phase) ≈ samples[i] at every filled position.
        int w = 32;
        double[] samples = MakeSeries(160, seed: 77);

        var phase = new double[samples.Length];
        var env = new double[samples.Length];
        FftAnalyticSignal.RollingCausalAnalyticSignal(samples, w, phase, env);

        for (int i = w - 1; i < samples.Length; i++)
        {
            double reconstructedReal = env[i] * Math.Cos(phase[i]);
            double scale = Math.Max(1.0, Math.Abs(samples[i]));
            Assert.True(Math.Abs(reconstructedReal - samples[i]) / scale < 1e-6,
                $"i={i}: {reconstructedReal} vs {samples[i]}");
        }
    }

    [Fact]
    public void RollingCausalAnalyticSignal_IsCausal_FutureSamplesDoNotAffectEarlierOutput()
    {
        int w = 24;
        double[] baseSamples = MakeSeries(150, seed: 44);

        var basePhase = new double[baseSamples.Length];
        var baseEnv = new double[baseSamples.Length];
        FftAnalyticSignal.RollingCausalAnalyticSignal(baseSamples, w, basePhase, baseEnv);

        int mutateAt = 100;
        double[] mutated = (double[])baseSamples.Clone();
        mutated[mutateAt] += 25.0;

        var mutatedPhase = new double[mutated.Length];
        var mutatedEnv = new double[mutated.Length];
        FftAnalyticSignal.RollingCausalAnalyticSignal(mutated, w, mutatedPhase, mutatedEnv);

        for (int i = w - 1; i < mutateAt; i++)
        {
            Assert.Equal(basePhase[i], mutatedPhase[i], precision: 10);
            Assert.Equal(baseEnv[i], mutatedEnv[i], precision: 10);
        }
    }

    [Fact]
    public void RollingCausalAnalyticSignal_WarmupAndInsufficientData_AreNaN()
    {
        int w = 16;
        double[] samples = MakeSeries(40, seed: 33);

        var phase = new double[samples.Length];
        var env = new double[samples.Length];
        FftAnalyticSignal.RollingCausalAnalyticSignal(samples, w, phase, env);

        for (int i = 0; i < w - 1; i++)
        {
            Assert.True(double.IsNaN(phase[i]), $"warmup phase index {i} should be NaN");
            Assert.True(double.IsNaN(env[i]), $"warmup envelope index {i} should be NaN");
        }

        Assert.False(double.IsNaN(phase[w - 1]));
        Assert.False(double.IsNaN(env[w - 1]));

        var tooShort = new double[w - 1];
        var shortPhase = new double[w - 1];
        var shortEnv = new double[w - 1];
        FftAnalyticSignal.RollingCausalAnalyticSignal(tooShort, w, shortPhase, shortEnv);
        Assert.All(shortPhase, v => Assert.True(double.IsNaN(v)));
        Assert.All(shortEnv, v => Assert.True(double.IsNaN(v)));
    }

    [Fact]
    public void RollingCausalAnalyticSignal_ThrowsWhenOutputTooShort()
    {
        var samples = new double[50];
        Assert.Throws<ArgumentException>(() =>
            FftAnalyticSignal.RollingCausalAnalyticSignal(samples, 16, new double[49], new double[50]));
        Assert.Throws<ArgumentException>(() =>
            FftAnalyticSignal.RollingCausalAnalyticSignal(samples, 16, new double[50], new double[49]));
    }

    // --- helpers ---

    private static void AssertMatchesReference(int windowSize, int seed)
    {
        double[] samples = MakeSeries(220, seed);

        var actualPhase = new double[samples.Length];
        var actualEnv = new double[samples.Length];
        FftAnalyticSignal.RollingCausalAnalyticSignal(samples, windowSize, actualPhase, actualEnv);

        var (expectedPhase, expectedEnv) = NaiveRollingCausalAnalyticSignal(samples, windowSize);

        double envScale = Math.Max(1.0, MaxAbsFinite(expectedEnv));
        for (int i = 0; i < samples.Length; i++)
        {
            if (double.IsNaN(expectedPhase[i]))
            {
                Assert.True(double.IsNaN(actualPhase[i]), $"i={i}: expected NaN phase");
                Assert.True(double.IsNaN(actualEnv[i]), $"i={i}: expected NaN envelope");
                continue;
            }

            Assert.True(Math.Abs(actualEnv[i] - expectedEnv[i]) / envScale < RelativeTolerance,
                $"w={windowSize} i={i}: env {actualEnv[i]} vs {expectedEnv[i]}");

            // Compare phase as a wrapped angular difference to avoid ±2π artefacts.
            double d = actualPhase[i] - expectedPhase[i];
            while (d <= -Math.PI) d += 2.0 * Math.PI;
            while (d > Math.PI) d -= 2.0 * Math.PI;
            Assert.True(Math.Abs(d) < 1e-6, $"w={windowSize} i={i}: phase {actualPhase[i]} vs {expectedPhase[i]}");
        }
    }

    private static (double[] Phase, double[] Envelope) NaiveRollingCausalAnalyticSignal(double[] samples, int windowSize)
    {
        int n = samples.Length;
        int w = Math.Max(4, windowSize);

        var phase = new double[n];
        var env = new double[n];
        for (int i = 0; i < n; i++)
        {
            phase[i] = double.NaN;
            env[i] = double.NaN;
        }

        if (n < w)
        {
            return (phase, env);
        }

        int half = w / 2;
        bool evenWindow = (w & 1) == 0;

        for (int i = w - 1; i < n; i++)
        {
            var seg = new double[w];
            Array.Copy(samples, i - w + 1, seg, 0, w);

            var (sr, si) = DiscreteFourierTransformReference.Forward(seg);

            var hr = new double[w];
            var hi = new double[w];
            hr[0] = sr[0];
            hi[0] = si[0];
            // Mirrors the fix in FftAnalyticSignal.RollingCausalAnalyticSignal: odd w has no
            // exact Nyquist bin, so its topmost positive-frequency bin (`half`) must be doubled
            // too, not left at the loop's exclusive bound.
            int lastDoubledBin = evenWindow ? half - 1 : half;
            for (int k = 1; k <= lastDoubledBin; k++)
            {
                hr[k] = 2.0 * sr[k];
                hi[k] = 2.0 * si[k];
            }

            if (evenWindow)
            {
                hr[half] = sr[half];
                hi[half] = si[half];
            }

            var (rr, ri) = DiscreteFourierTransformReference.Inverse(hr, hi);
            double re = rr[w - 1];
            double im = ri[w - 1];
            phase[i] = Math.Atan2(im, re);
            env[i] = Math.Sqrt((re * re) + (im * im));
        }

        return (phase, env);
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
