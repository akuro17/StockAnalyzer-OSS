#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

/// <summary>
/// Characterization tests that pin the exact numeric output of <see cref="FftSpectrumAnalysis"/>
/// and <see cref="FftProjectionAnalysis"/> so the DFT→FFT engine swap is provably behaviour-preserving.
///
/// The expected values are recomputed independently inside the test from
/// <see cref="DiscreteFourierTransformReference"/> plus a byte-for-byte copy of each engine's
/// pre-transform processing (detrend / Hanning window / magnitude scaling / bin filter / NMS).
/// If a future change alters the transform result, these assertions fail with a concrete bin diff.
/// </summary>
public class FftEngineCharacterizationTests
{
    private const int Precision = 9;

    // ---------------------------------------------------------------- Spectrum

    [Fact]
    public void Spectrum_MultiFrequencyInput_EveryBinMatchesReferenceRecompute()
    {
        var samples = MultiFrequencySamples(120, seed: 41);
        const bool detrend = true;
        const bool window = true;
        const double minPeriod = 2.0;
        const double maxPeriod = 100.0;

        var result = FftSpectrumAnalysis.CalculateSpectrum(samples, detrend, window, minPeriod, maxPeriod);
        var expected = ExpectedSpectrumBins(samples, detrend, window, minPeriod, maxPeriod);

        Assert.Equal(expected.Count, result.Bins.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].BinIndex, result.Bins[i].BinIndex);
            Assert.Equal(expected[i].Period, result.Bins[i].Period, Precision);
            Assert.Equal(expected[i].Magnitude, result.Bins[i].Magnitude, Precision);
            Assert.Equal(expected[i].Power, result.Bins[i].Power, Precision);
            Assert.Equal(expected[i].Phase, result.Bins[i].Phase, Precision);
        }

        int dominantExpected = ArgMaxPower(expected);
        Assert.NotNull(result.DominantBin);
        Assert.Equal(expected[dominantExpected].BinIndex, result.DominantBin!.BinIndex);
        Assert.Equal(expected[dominantExpected].Period, result.DominantPeriod, Precision);
    }

    [Fact]
    public void Spectrum_PureSine_DominantBinIsAnalytic()
    {
        int n = 100;
        double periodBars = 20.0; // k = 5 exactly
        double amplitude = 12.0;
        var samples = new double[n];
        for (int i = 0; i < n; i++)
        {
            samples[i] = 250.0 + amplitude * Math.Sin(2.0 * Math.PI * i / periodBars);
        }

        // No detrend / window: bin k=5 magnitude is analytically exact ((2/N)·|X[5]| = amplitude).
        var result = FftSpectrumAnalysis.CalculateSpectrum(samples, applyDetrend: false, applyHanningWindow: false);

        Assert.NotNull(result.DominantBin);
        Assert.Equal(5, result.DominantBin!.BinIndex);
        Assert.Equal(20.0, result.DominantPeriod, Precision);
        Assert.Equal(amplitude, result.DominantBin.Magnitude, 6);
        Assert.Equal(1.0, result.DominantBin.NormalizedPower, Precision);
    }

    // -------------------------------------------------------------- Projection

    [Fact]
    public void Projection_MultiFrequencyInput_DominantHarmonicsMatchReferenceRecompute()
    {
        var samples = MultiFrequencySamples(96, seed: 71);
        var timestamps = Timestamps(samples.Length);
        const int harmonicCount = 3;
        const bool detrend = true;
        const double minPeriod = 3.0;
        const double maxPeriod = double.MaxValue;

        var result = FftProjectionAnalysis.CalculateProjection(
            samples, timestamps, futureSteps: 10, timeframeSpan: TimeSpan.FromDays(1),
            harmonicCount: harmonicCount, applyDetrend: detrend, minPeriod: minPeriod, maxPeriod: maxPeriod);

        var expected = ExpectedDominantHarmonics(samples, harmonicCount, detrend, minPeriod, maxPeriod);

        Assert.Equal(expected.Count, result.DominantHarmonics.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].BinIndex, result.DominantHarmonics[i].BinIndex);
            Assert.Equal(expected[i].Period, result.DominantHarmonics[i].Period, Precision);
            Assert.Equal(expected[i].Magnitude, result.DominantHarmonics[i].Magnitude, Precision);
            Assert.Equal(expected[i].Power, result.DominantHarmonics[i].Power, Precision);
            Assert.Equal(expected[i].Phase, result.DominantHarmonics[i].Phase, Precision);
        }
    }

    [Fact]
    public void Projection_NyquistBin_UsesOneOverNScaling()
    {
        int n = 32;
        double amplitude = 3.0;
        var samples = new double[n];
        for (int i = 0; i < n; i++)
        {
            samples[i] = 100.0 + (i % 2 == 0 ? amplitude : -amplitude);
        }

        var result = FftProjectionAnalysis.CalculateProjection(
            samples, Timestamps(n), futureSteps: 4, harmonicCount: 1,
            applyDetrend: false, minPeriod: 2.0, maxPeriod: 10.0);

        Assert.Single(result.DominantHarmonics);
        Assert.Equal(16, result.DominantHarmonics[0].BinIndex);
        Assert.Equal(2.0, result.DominantHarmonics[0].Period, Precision);
        Assert.Equal(amplitude, result.DominantHarmonics[0].Magnitude, 6);
    }

    // --------------------------------------------------- reference recompute

    private static IReadOnlyList<BinSnapshot> ExpectedSpectrumBins(
        IReadOnlyList<double> samples, bool detrend, bool window, double minPeriod, double maxPeriod)
    {
        int n = samples.Count;
        var processed = LinearDetrend(samples, detrend);
        if (window && n > 1)
        {
            double factor = 2.0 * Math.PI / (n - 1);
            for (int i = 0; i < n; i++)
            {
                processed[i] *= 0.5 * (1.0 - Math.Cos(factor * i));
            }
        }

        var (re, im) = DiscreteFourierTransformReference.Forward(processed);
        var bins = new List<BinSnapshot>();
        for (int k = 1; k <= n / 2; k++)
        {
            double period = (double)n / k;
            if (period < minPeriod || period > maxPeriod)
            {
                continue;
            }

            double magnitude = (2.0 / n) * Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
            double phase = Math.Atan2(im[k], re[k]);
            bins.Add(new BinSnapshot(k, period, magnitude, magnitude * magnitude, phase));
        }

        return bins;
    }

    private static IReadOnlyList<BinSnapshot> ExpectedDominantHarmonics(
        IReadOnlyList<double> samples, int harmonicCount, bool detrend, double minPeriod, double maxPeriod)
    {
        int n = samples.Count;
        double[] processed;
        if (detrend)
        {
            double slope = (n > 1) ? (samples[n - 1] - samples[0]) / (n - 1) : 0.0;
            double intercept = samples[0];
            processed = new double[n];
            for (int i = 0; i < n; i++)
            {
                processed[i] = samples[i] - (slope * i + intercept);
            }
        }
        else
        {
            double mean = samples.Average();
            processed = samples.Select(v => v - mean).ToArray();
        }

        var (re, im) = DiscreteFourierTransformReference.Forward(processed);

        var raw = new List<BinSnapshot>();
        int halfN = n / 2;
        for (int k = 1; k <= halfN; k++)
        {
            double period = (double)n / k;
            if (period < minPeriod || period > maxPeriod)
            {
                continue;
            }

            double scaleFactor = (n % 2 == 0 && k == halfN) ? (1.0 / n) : (2.0 / n);
            double magnitude = scaleFactor * Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
            double phase = Math.Atan2(im[k], re[k]);
            raw.Add(new BinSnapshot(k, period, magnitude, magnitude * magnitude, phase));
        }

        // Non-maximum suppression — verbatim copy of FftProjectionAnalysis selection.
        int kCount = Math.Max(1, Math.Min(harmonicCount, 10));
        var sortedByPower = raw.OrderByDescending(c => c.Power).ToList();
        var selected = new List<BinSnapshot>(kCount);
        foreach (var candidate in sortedByPower)
        {
            if (selected.Count >= kCount)
            {
                break;
            }

            double candFreq = (double)candidate.BinIndex / n;
            bool tooClose = selected.Any(existing =>
            {
                double ratio = candFreq / ((double)existing.BinIndex / n);
                return ratio >= 0.85 && ratio <= 1.15;
            });

            if (!tooClose)
            {
                selected.Add(candidate);
            }
        }

        if (selected.Count < kCount)
        {
            foreach (var candidate in sortedByPower)
            {
                if (selected.Count >= kCount)
                {
                    break;
                }

                if (!selected.Contains(candidate))
                {
                    selected.Add(candidate);
                }
            }
        }

        return selected;
    }

    private static double[] LinearDetrend(IReadOnlyList<double> samples, bool apply)
    {
        int n = samples.Count;
        var processed = new double[n];
        if (!apply)
        {
            for (int i = 0; i < n; i++)
            {
                processed[i] = samples[i];
            }

            return processed;
        }

        double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
        for (int i = 0; i < n; i++)
        {
            sumX += i;
            sumY += samples[i];
            sumXY += (double)i * samples[i];
            sumXX += (double)i * i;
        }

        double denominator = (n * sumXX) - (sumX * sumX);
        double slope = (Math.Abs(denominator) > 1e-12) ? ((n * sumXY) - (sumX * sumY)) / denominator : 0.0;
        double intercept = (sumY - slope * sumX) / n;
        for (int i = 0; i < n; i++)
        {
            processed[i] = samples[i] - (slope * i + intercept);
        }

        return processed;
    }

    private static int ArgMaxPower(IReadOnlyList<BinSnapshot> bins)
    {
        int idx = 0;
        double max = double.NegativeInfinity;
        for (int i = 0; i < bins.Count; i++)
        {
            if (bins[i].Power > max)
            {
                max = bins[i].Power;
                idx = i;
            }
        }

        return idx;
    }

    private static double[] MultiFrequencySamples(int n, int seed)
    {
        var rng = new Random(seed);
        var s = new double[n];
        for (int i = 0; i < n; i++)
        {
            s[i] = 180.0
                + 0.08 * i
                + 6.0 * Math.Sin(2.0 * Math.PI * i / 13.0)
                + 3.5 * Math.Cos(2.0 * Math.PI * i / 5.0)
                + 1.5 * Math.Sin(2.0 * Math.PI * i / 27.0)
                + (rng.NextDouble() - 0.5);
        }

        return s;
    }

    private static List<DateTime> Timestamps(int n)
    {
        var baseDate = new DateTime(2024, 1, 1);
        var list = new List<DateTime>(n);
        for (int i = 0; i < n; i++)
        {
            list.Add(baseDate.AddDays(i));
        }

        return list;
    }

    private readonly record struct BinSnapshot(int BinIndex, double Period, double Magnitude, double Power, double Phase);
}
