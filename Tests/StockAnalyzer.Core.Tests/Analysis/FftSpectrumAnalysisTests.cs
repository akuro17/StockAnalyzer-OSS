#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

public class FftSpectrumAnalysisTests
{
    [Fact]
    public void CalculateSpectrum_NullOrTooFewSamples_ReturnsEmpty()
    {
        var nullResult = FftSpectrumAnalysis.CalculateSpectrum((IReadOnlyList<double>?)null);
        Assert.Same(FftSpectrumResult.Empty, nullResult);
        Assert.Empty(nullResult.Bins);
        Assert.Null(nullResult.DominantBin);

        var tooFew = new double[] { 10.0, 12.0, 11.0 };
        var fewResult = FftSpectrumAnalysis.CalculateSpectrum(tooFew);
        Assert.Same(FftSpectrumResult.Empty, fewResult);
    }

    [Fact]
    public void CalculateSpectrum_PureSineWave_DetectsExpectedDominantPeriod()
    {
        // 100 samples with a pure sine wave of period = 20 bars (frequency = 0.05, k = 5)
        int n = 100;
        double targetPeriod = 20.0;
        var samples = new double[n];
        for (int i = 0; i < n; i++)
        {
            samples[i] = 100.0 + 15.0 * Math.Sin(2.0 * Math.PI * i / targetPeriod);
        }

        var result = FftSpectrumAnalysis.CalculateSpectrum(samples, applyDetrend: true, applyHanningWindow: false);

        Assert.NotNull(result);
        Assert.NotEmpty(result.Bins);
        Assert.NotNull(result.DominantBin);
        Assert.Equal(targetPeriod, result.DominantPeriod, precision: 4);
        Assert.Equal(5, result.DominantBin!.BinIndex);
        Assert.Equal(1.0, result.DominantBin.NormalizedPower, precision: 4);
        Assert.True(result.DominantBin.IsDominant);
    }

    [Fact]
    public void CalculateSpectrum_SineWaveWithLinearTrend_DetrendingExtractsCycle()
    {
        // Period = 25 bars (k = 4 in N=100) with a strong linear upward slope
        int n = 100;
        double targetPeriod = 25.0;
        var samples = new double[n];
        for (int i = 0; i < n; i++)
        {
            samples[i] = 50.0 + 3.0 * i + 10.0 * Math.Sin(2.0 * Math.PI * i / targetPeriod);
        }

        var result = FftSpectrumAnalysis.CalculateSpectrum(samples, applyDetrend: true, applyHanningWindow: true);

        Assert.NotNull(result.DominantBin);
        Assert.Equal(targetPeriod, result.DominantPeriod, precision: 4);
        Assert.Equal(4, result.DominantBin!.BinIndex);
    }

    [Fact]
    public void CalculateSpectrum_PeriodFilter_RestrictsBinRange()
    {
        int n = 100;
        var samples = new double[n];
        for (int i = 0; i < n; i++)
        {
            samples[i] = 100.0 + Math.Sin(2.0 * Math.PI * i / 10.0);
        }

        // Only allow periods between 5 and 15
        var result = FftSpectrumAnalysis.CalculateSpectrum(samples, minPeriod: 5.0, maxPeriod: 15.0);

        Assert.NotEmpty(result.Bins);
        Assert.All(result.Bins, b =>
        {
            Assert.True(b.Period >= 5.0);
            Assert.True(b.Period <= 15.0);
        });
    }

    [Fact]
    public void CalculateSpectrum_CandleDataOverload_CalculatesCorrectly()
    {
        var candles = new List<CoreCandleData>();
        var baseDate = new DateTime(2025, 1, 1);
        int n = 60;
        double targetPeriod = 15.0; // k = 4 in N=60

        for (int i = 0; i < n; i++)
        {
            decimal price = (decimal)(100.0 + 10.0 * Math.Sin(2.0 * Math.PI * i / targetPeriod));
            candles.Add(new CoreCandleData(
                baseDate.AddDays(i),
                price,
                price + 1m,
                price - 1m,
                price,
                1000));
        }

        var result = FftSpectrumAnalysis.CalculateSpectrum(
            candles,
            c => (double)((c.High + c.Low) / 2m),
            applyDetrend: true,
            applyHanningWindow: true);

        Assert.NotNull(result.DominantBin);
        Assert.Equal(targetPeriod, result.DominantPeriod, precision: 4);
        Assert.Equal(60, result.SampleCount);
    }

    [Fact]
    public void CalculateSpectrum_EvenSampleCount_NyquistBinHasTheTrueAmplitude()
    {
        // x_i = +-10 alternating is a pure Nyquist wave (period 2 bars, k = n/2) of amplitude 10. The single-sided spectrum scale is 2/n for every bin
        // except this one, which has no mirrored partner and uses 1/n (as FftProjectionAnalysis does); the old 2/n gave 20.
        int n = 100;
        var samples = Enumerable.Range(0, n).Select(i => i % 2 == 0 ? 10.0 : -10.0).ToArray();

        var result = FftSpectrumAnalysis.CalculateSpectrum(samples, applyDetrend: false, applyHanningWindow: false);

        Assert.NotNull(result.DominantBin);
        Assert.Equal(n / 2, result.DominantBin!.BinIndex);
        Assert.Equal(2.0, result.DominantPeriod, 9);
        Assert.Equal(10.0, result.DominantBin.Magnitude, 9);
        Assert.Equal(100.0, result.DominantBin.Power, 9);
    }

    [Fact]
    public void CalculateSpectrum_ANyquistWaveSmallerThanARealCycle_IsNotTheDominantPeriod()
    {
        // A cycle of 10 bars with amplitude 1.5 and a Nyquist wave with amplitude 1.0: with the old 2/n the Nyquist bin read 2.0 and won.
        int n = 100;
        var samples = Enumerable.Range(0, n)
            .Select(i => 1.5 * Math.Sin(2.0 * Math.PI * i / 10.0) + (i % 2 == 0 ? 1.0 : -1.0))
            .ToArray();

        var result = FftSpectrumAnalysis.CalculateSpectrum(samples, applyDetrend: false, applyHanningWindow: false);

        Assert.Equal(10.0, result.DominantPeriod, 9);
        Assert.Equal(1.5, result.DominantBin!.Magnitude, 9);
        Assert.Equal(1.0, result.Bins.Single(b => b.BinIndex == n / 2).Magnitude, 9);
    }

    [Fact]
    public void CalculateSpectrum_OddSampleCount_KeepsTheTwoOverNScale()
    {
        // Odd n has no Nyquist bin: every bin keeps 2/n, so a pure cosine on bin 5 of 101 samples keeps its amplitude exactly.
        int n = 101;
        var samples = Enumerable.Range(0, n).Select(i => 7.0 * Math.Cos(2.0 * Math.PI * 5 * i / n)).ToArray();

        var result = FftSpectrumAnalysis.CalculateSpectrum(samples, applyDetrend: false, applyHanningWindow: false);

        Assert.Equal(5, result.DominantBin!.BinIndex);
        Assert.Equal(7.0, result.DominantBin.Magnitude, 9);
    }
}
