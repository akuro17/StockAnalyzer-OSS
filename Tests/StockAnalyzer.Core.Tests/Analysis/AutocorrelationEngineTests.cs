using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

public class AutocorrelationEngineTests
{
    [Fact]
    public void CalculateDominantPeriod_SineWavePeriod20_DetectsPeriodAccurately()
    {
        // 120 bars of a pure sine wave with period 20: P_t = 100 + 10 * sin(2 * pi * t / 20)
        int n = 120;
        double[] prices = new double[n];
        for (int t = 0; t < n; t++)
        {
            prices[t] = 100.0 + 10.0 * Math.Sin(2.0 * Math.PI * t / 20.0);
        }

        int windowSize = 30;
        int minLag = 5;
        int maxLag = 40;
        double threshold = 0.30;
        int smoothingPeriod = 3;
        int defaultPeriod = 20;

        double?[] domPeriod = new double?[n];
        double?[] rawPeriod = new double?[n];
        double?[] peakCorr = new double?[n];

        AutocorrelationEngine.CalculateDominantPeriod(
            prices,
            windowSize,
            minLag,
            maxLag,
            threshold,
            smoothingPeriod,
            defaultPeriod,
            AutocorrelationPeakSelectionMode.MaxCorrelation,
            AutocorrelationFallbackPolicy.HoldPrevious,
            5,
            domPeriod,
            rawPeriod,
            peakCorr);

        int warmup = windowSize + maxLag; // 30 + 40 = 70
        for (int t = 0; t < warmup; t++)
        {
            Assert.Null(domPeriod[t]);
            Assert.Null(rawPeriod[t]);
            Assert.Null(peakCorr[t]);
        }

        // After warmup, dominant period should be close to 20 (within +- 1.0 bar due to discretization/smoothing)
        for (int t = warmup + 5; t < n; t++)
        {
            Assert.NotNull(domPeriod[t]);
            Assert.InRange(domPeriod[t]!.Value, 19.0, 21.0);

            Assert.NotNull(peakCorr[t]);
            Assert.True(peakCorr[t]!.Value > 0.8, $"Expected strong correlation at t={t}, got {peakCorr[t]}");
        }
    }

    [Fact]
    public void CalculateDominantPeriod_FlatPrices_ZeroVariance_HandlesGracefullyWithoutExceptions()
    {
        int n = 100;
        double[] prices = new double[n];
        Array.Fill(prices, 100.0);

        double?[] domPeriod = new double?[n];
        double?[] rawPeriod = new double?[n];
        double?[] peakCorr = new double?[n];

        AutocorrelationEngine.CalculateDominantPeriod(
            prices,
            windowSize: 20,
            minLag: 5,
            maxLag: 30,
            threshold: 0.30,
            smoothingPeriod: 5,
            defaultPeriod: 14,
            AutocorrelationPeakSelectionMode.MaxCorrelation,
            AutocorrelationFallbackPolicy.StaticDefault,
            5,
            domPeriod,
            rawPeriod,
            peakCorr);

        int warmup = 20 + 30; // 50
        for (int t = warmup; t < n; t++)
        {
            Assert.NotNull(domPeriod[t]);
            Assert.Equal(14.0, domPeriod[t]!.Value);
            Assert.Null(rawPeriod[t]);
            Assert.Null(peakCorr[t]);
        }
    }

    [Fact]
    public void CalculateDominantPeriod_AdapterOverload_ReturnsMatchingListCounts()
    {
        var series = new List<decimal?>();
        for (int i = 0; i < 80; i++)
        {
            series.Add(100m + (decimal)(5 * Math.Sin(2 * Math.PI * i / 15.0)));
        }

        var (dom, raw, peak) = AutocorrelationEngine.CalculateDominantPeriod(
            series,
            windowSize: 20,
            minLag: 5,
            maxLag: 25,
            threshold: 0.25,
            smoothingPeriod: 3,
            defaultPeriod: 15,
            AutocorrelationPeakSelectionMode.MaxCorrelation,
            AutocorrelationFallbackPolicy.HoldPrevious,
            5);

        Assert.Equal(80, dom.Count);
        Assert.Equal(80, raw.Count);
        Assert.Equal(80, peak.Count);

        // Warmup: 20 + 25 = 45
        Assert.Null(dom[44]);
        Assert.NotNull(dom[45]);
        Assert.InRange(dom[60]!.Value, 14.0m, 16.0m);
    }

    [Fact]
    public void CalculateDominantPeriod_LagSideZeroVariance_OutputsNullCorrelation()
    {
        // First 30 bars are completely flat (sumVV == 0 for early lag), next 50 bars oscillate
        int n = 80;
        double[] prices = new double[n];
        for (int i = 0; i < 30; i++) prices[i] = 100.0;
        for (int i = 30; i < n; i++) prices[i] = 100.0 + 5.0 * Math.Sin(i);

        double?[] domPeriod = new double?[n];
        double?[] rawPeriod = new double?[n];
        double?[] peakCorr = new double?[n];

        // Fixed lag = 15
        AutocorrelationEngine.CalculateDominantPeriod(
            prices,
            windowSize: 15,
            minLag: 5,
            maxLag: 20,
            threshold: 0.30,
            smoothingPeriod: 3,
            defaultPeriod: 14,
            AutocorrelationPeakSelectionMode.MaxCorrelation,
            AutocorrelationFallbackPolicy.StaticDefault,
            5,
            domPeriod,
            rawPeriod,
            peakCorr,
            lag: 15);

        // At t = 35, u is prices[21..35] (some variance), but lag 15 window v is prices[6..20] (flat, variance == 0)
        // Therefore, correlation at lag 15 must be null
        Assert.Null(peakCorr[35]);
    }

    [Fact]
    public void CalculateDominantPeriod_FlatNoiseWithProminence_RejectsTrivialPeaks()
    {
        // Construct a synthetic signal with a tiny harmonic ripple where local prominence is very small
        // prices[t] has a slight wave, but window differences yield correlation peaks with prominence < 0.02
        int n = 80;
        double[] prices = new double[n];
        for (int i = 0; i < n; i++)
        {
            // Linear drift + very tiny ripple
            prices[i] = 100.0 + i * 0.5 + 0.001 * Math.Sin(2.0 * Math.PI * i / 10.0);
        }

        double?[] domPeriodHighProm = new double?[n];
        double?[] rawPeriodHighProm = new double?[n];
        double?[] peakCorrHighProm = new double?[n];

        // Strict prominence = 0.05: the tiny ripple will not qualify as a prominent peak
        AutocorrelationEngine.CalculateDominantPeriod(
            prices,
            windowSize: 20,
            minLag: 5,
            maxLag: 20,
            threshold: 0.20,
            smoothingPeriod: 3,
            defaultPeriod: 12,
            AutocorrelationPeakSelectionMode.MaxCorrelation,
            AutocorrelationFallbackPolicy.StaticDefault,
            5,
            domPeriodHighProm,
            rawPeriodHighProm,
            peakCorrHighProm,
            lag: 0,
            minProminence: 0.05);

        int warmup = 20 + 20;
        // All bars after warmup should fallback to defaultPeriod (12) because prominence condition fails
        for (int t = warmup; t < n; t++)
        {
            Assert.NotNull(domPeriodHighProm[t]);
            Assert.Equal(12.0, domPeriodHighProm[t]!.Value);
        }
    }
}
