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
}
