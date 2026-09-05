using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

public class FftProjectionAnalysisTests
{
    [Fact]
    public void CalculateProjection_WithEmptyOrInsufficientSamples_ReturnsEmpty()
    {
        var result1 = FftProjectionAnalysis.CalculateProjection(
            Array.Empty<double>(),
            Array.Empty<DateTime>());
        Assert.Equal(0, result1.SampleCount);
        Assert.Empty(result1.ProjectedPoints);

        var result2 = FftProjectionAnalysis.CalculateProjection(
            new double[] { 10.0, 11.0, 12.0 },
            new DateTime[] { DateTime.Now, DateTime.Now.AddDays(1), DateTime.Now.AddDays(2) });
        Assert.Equal(0, result2.SampleCount);
        Assert.Empty(result2.ProjectedPoints);
    }

    [Fact]
    public void CalculateProjection_PureSineWave_ReconstructsAndExtrapolatesHarmonics()
    {
        // 64 samples of pure sine wave with period = 16 bars (4 complete cycles), amplitude = 5.0, mean = 100.0
        int n = 64;
        var samples = new List<double>(n);
        var timestamps = new List<DateTime>(n);
        var baseDate = new DateTime(2024, 1, 1);

        double period = 16.0;
        double amplitude = 5.0;
        double meanPrice = 100.0;

        for (int i = 0; i < n; i++)
        {
            double val = meanPrice + amplitude * Math.Sin(2.0 * Math.PI * i / period);
            samples.Add(val);
            timestamps.Add(baseDate.AddDays(i));
        }

        var result = FftProjectionAnalysis.CalculateProjection(
            samples,
            timestamps,
            futureSteps: 16,
            timeframeSpan: TimeSpan.FromDays(1),
            harmonicCount: 1,
            applyDetrend: false,
            minPeriod: 4.0,
            maxPeriod: 32.0,
            showConfidenceBand: true,
            confidenceMultiplier: 2.0m);

        Assert.Equal(n, result.SampleCount);
        Assert.NotEmpty(result.DominantHarmonics);
        Assert.Equal(16 + 1, result.ProjectedPoints.Count); // 1 connection point + 16 future steps

        // Dominant harmonic should detect period ~ 16 bars with amplitude ~ 5.0
        var topHarmonic = result.DominantHarmonics[0];
        Assert.Equal(16.0, topHarmonic.Period, precision: 1);
        Assert.Equal(5.0, topHarmonic.Magnitude, precision: 1);

        // Verify extrapolated values follow the sine wave
        for (int m = 1; m <= 16; m++)
        {
            int futureIndex = (n - 1) + m;
            double expectedVal = meanPrice + amplitude * Math.Sin(2.0 * Math.PI * futureIndex / period);
            double actualVal = result.ProjectedPoints[m].Y;
            Assert.Equal(expectedVal, actualVal, precision: 1);
        }
    }

    [Fact]
    public void CalculateProjection_LinearTrendPlusSine_ExtrapolatesSlopeAndCycle()
    {
        // 60 samples with linear trend (slope = 0.5) + sine wave (period = 20)
        int n = 60;
        var samples = new List<double>(n);
        var timestamps = new List<DateTime>(n);
        var baseDate = new DateTime(2024, 1, 1);

        for (int i = 0; i < n; i++)
        {
            double val = 50.0 + 0.5 * i + 3.0 * Math.Cos(2.0 * Math.PI * i / 20.0);
            samples.Add(val);
            timestamps.Add(baseDate.AddDays(i));
        }

        var result = FftProjectionAnalysis.CalculateProjection(
            samples,
            timestamps,
            futureSteps: 20,
            timeframeSpan: TimeSpan.FromDays(1),
            harmonicCount: 3,
            applyDetrend: true);

        Assert.Equal(0.5, result.Slope, precision: 1);
        Assert.True(result.ProjectedPoints[^1].Y > result.ProjectedPoints[0].Y, "Projected path should follow upward trend");
        Assert.NotEmpty(result.UpperBandPoints);
        Assert.NotEmpty(result.LowerBandPoints);

        // Upper > Center > Lower for future steps
        for (int i = 1; i < result.ProjectedPoints.Count; i++)
        {
            Assert.True(result.UpperBandPoints[i].Y > result.ProjectedPoints[i].Y);
            Assert.True(result.LowerBandPoints[i].Y < result.ProjectedPoints[i].Y);
        }
    }

    [Fact]
    public void CalculateProjection_NyquistFrequency_CalculatesCorrectAmplitude()
    {
        // 32 samples alternating +3 and -3 (period = 2 bars, Nyquist frequency k = 16)
        int n = 32;
        var samples = new List<double>(n);
        var timestamps = new List<DateTime>(n);
        var baseDate = new DateTime(2024, 1, 1);

        for (int i = 0; i < n; i++)
        {
            double val = 100.0 + (i % 2 == 0 ? 3.0 : -3.0);
            samples.Add(val);
            timestamps.Add(baseDate.AddDays(i));
        }

        var result = FftProjectionAnalysis.CalculateProjection(
            samples,
            timestamps,
            futureSteps: 10,
            harmonicCount: 1,
            applyDetrend: false,
            minPeriod: 2.0,
            maxPeriod: 10.0);

        Assert.NotEmpty(result.DominantHarmonics);
        var top = result.DominantHarmonics[0];
        Assert.Equal(2.0, top.Period, precision: 1);
        // Correct one-sided Nyquist amplitude should be 3.0 (with 1/N scaling), not 6.0 (which would occur with 2/N)
        Assert.Equal(3.0, top.Magnitude, precision: 1);
    }

    [Fact]
    public void CalculateProjection_ContinuityBlending_SmoothlyConnectsFromLastSample()
    {
        // 50 samples with arbitrary data creating an in-sample residual at the endpoint
        int n = 50;
        var samples = new List<double>(n);
        var timestamps = new List<DateTime>(n);
        var baseDate = new DateTime(2024, 1, 1);

        for (int i = 0; i < n; i++)
        {
            double val = 100.0 + 2.0 * Math.Sin(2.0 * Math.PI * i / 10.0) + (i == n - 1 ? 5.0 : 0.0); // spike on last sample
            samples.Add(val);
            timestamps.Add(baseDate.AddDays(i));
        }

        var result = FftProjectionAnalysis.CalculateProjection(
            samples,
            timestamps,
            futureSteps: 10,
            harmonicCount: 2,
            applyDetrend: true);

        // Step 0 is the exact last sample
        Assert.Equal(samples[^1], result.ProjectedPoints[0].Y, precision: 2);

        // Step 1 should be smoothly close to the spike due to continuity blending (decay 0.85)
        double diff0to1 = Math.Abs(result.ProjectedPoints[1].Y - result.ProjectedPoints[0].Y);
        Assert.True(diff0to1 < 3.0, "Continuity blending should prevent abrupt disconnection from endpoint spike");
    }

    [Fact]
    public void CalculateProjection_UncertaintyCone_ExpandsWithSquareRootOfTime()
    {
        int n = 50;
        var samples = new List<double>(n);
        var timestamps = new List<DateTime>(n);
        var baseDate = new DateTime(2024, 1, 1);

        for (int i = 0; i < n; i++)
        {
            samples.Add(100.0 + 2.0 * Math.Sin(2.0 * Math.PI * i / 12.0));
            timestamps.Add(baseDate.AddDays(i));
        }

        var result = FftProjectionAnalysis.CalculateProjection(
            samples,
            timestamps,
            futureSteps: 20,
            showConfidenceBand: true,
            confidenceMultiplier: 2.0m);

        // Verify that band width (Upper - Center) expands monotonically over future steps
        double prevMargin = 0.0;
        for (int m = 1; m < result.ProjectedPoints.Count; m++)
        {
            double margin = result.UpperBandPoints[m].Y - result.ProjectedPoints[m].Y;
            Assert.True(margin > prevMargin, $"Uncertainty band margin at step {m} ({margin}) must be strictly greater than step {m-1} ({prevMargin})");
            prevMargin = margin;
        }
    }

    [Fact]
    public void CalculateProjection_NonMaximumSuppression_PreventsAdjacentDuplicateFrequencies()
    {
        // 60 samples with strong 15-bar cycle
        int n = 60;
        var samples = new List<double>(n);
        var timestamps = new List<DateTime>(n);
        var baseDate = new DateTime(2024, 1, 1);

        for (int i = 0; i < n; i++)
        {
            samples.Add(100.0 + 5.0 * Math.Cos(2.0 * Math.PI * i / 15.0) + 3.0 * Math.Sin(2.0 * Math.PI * i / 5.0));
            timestamps.Add(baseDate.AddDays(i));
        }

        var result = FftProjectionAnalysis.CalculateProjection(
            samples,
            timestamps,
            futureSteps: 10,
            harmonicCount: 2,
            applyDetrend: false,
            minPeriod: 3.0,
            maxPeriod: 30.0);

        Assert.Equal(2, result.DominantHarmonics.Count);
        double f1 = result.DominantHarmonics[0].Frequency;
        double f2 = result.DominantHarmonics[1].Frequency;

        // Frequencies should be distinctly separated by NMS
        double ratio = f1 / f2;
        Assert.True(ratio < 0.85 || ratio > 1.15, $"Harmonics should not be adjacent leakage bins (f1={f1}, f2={f2}, ratio={ratio})");
    }
}