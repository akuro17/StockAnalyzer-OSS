using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

public class PearsonProjectionAnalysisTests
{
    [Fact]
    public void CalculateProjection_WithInvalidInputs_ReturnsEmpty()
    {
        var result1 = PearsonProjectionAnalysis.CalculateProjection(
            Array.Empty<double>(),
            Array.Empty<DateTime>(),
            0, 0);
        Assert.Equal(0, result1.SampleCount);
        Assert.Empty(result1.ProjectedPoints);

        var result2 = PearsonProjectionAnalysis.CalculateProjection(
            new double[] { 10.0, 11.0 },
            new DateTime[] { DateTime.Now, DateTime.Now.AddDays(1) },
            0, 1);
        Assert.Equal(0, result2.SampleCount);
        Assert.Empty(result2.ProjectedPoints);

        // Flat series (0 variance in query)
        var result3 = PearsonProjectionAnalysis.CalculateProjection(
            new double[] { 10.0, 10.0, 10.0, 10.0, 10.0, 10.0 },
            new DateTime[] { DateTime.Now, DateTime.Now.AddDays(1), DateTime.Now.AddDays(2), DateTime.Now.AddDays(3), DateTime.Now.AddDays(4), DateTime.Now.AddDays(5) },
            0, 4);
        Assert.Equal(0, result3.SampleCount);
        Assert.Empty(result3.ProjectedPoints);
    }

    [Fact]
    public void CalculateProjection_ExactHistoricalPatternMatch_FindsMatchWithCorrelationNearOne()
    {
        int totalBars = 120;
        var samples = new List<double>(totalBars);
        var timestamps = new List<DateTime>(totalBars);
        var baseDate = new DateTime(2024, 1, 1);

        for (int i = 0; i < totalBars; i++)
        {
            samples.Add(100.0 + i * 0.1);
            timestamps.Add(baseDate.AddDays(i));
        }

        // Inject distinct "V-shape" pattern in past: index 20 to 30 (11 bars)
        // Followed by strong upward rally: index 31 to 45 (+10% gain)
        for (int i = 0; i <= 10; i++)
        {
            double shapeVal = i <= 5 ? -i * 2.0 : -(10 - i) * 2.0;
            samples[20 + i] = 100.0 + shapeVal;
        }
        for (int k = 1; k <= 15; k++)
        {
            samples[30 + k] = samples[30] + k * 1.5; // Rally after V-shape
        }

        // Replicate the exact same V-shape pattern at current query: index 80 to 90 (11 bars)
        for (int i = 0; i <= 10; i++)
        {
            double shapeVal = i <= 5 ? -i * 2.0 : -(10 - i) * 2.0;
            samples[80 + i] = 150.0 + shapeVal * 1.5; // Different price level and scale, same shape
        }

        int queryStart = 80;
        int queryEnd = 90;
        int futureSteps = 10;

        var result = PearsonProjectionAnalysis.CalculateProjection(
            samples,
            timestamps,
            queryStartIndex: queryStart,
            queryEndIndex: queryEnd,
            futureSteps: futureSteps,
            minCorrelation: 0.80,
            topK: 1,
            timeframeSpan: TimeSpan.FromDays(1));

        Assert.True(result.HasMatch);
        Assert.True(result.BestCorrelation > 0.95);
        Assert.Equal(timestamps[20], result.MatchedStartTime);
        Assert.Equal(timestamps[30], result.MatchedEndTime);
        Assert.Equal(futureSteps + 1, result.ProjectedPoints.Count);

        // Verify that projected future points follow the upward rally trend from historical pattern
        var startProj = result.ProjectedPoints[0].Y;
        var endProj = result.ProjectedPoints[^1].Y;
        Assert.True(endProj > startProj, "Projected path should follow historical post-pattern upward rally");
    }

    [Fact]
    public void CalculateProjection_NoCorrelationAboveThreshold_ReturnsEmpty()
    {
        int totalBars = 80;
        var samples = new List<double>(totalBars);
        var timestamps = new List<DateTime>(totalBars);
        var baseDate = new DateTime(2024, 1, 1);

        for (int i = 0; i < totalBars; i++)
        {
            samples.Add(100.0 + Math.Sin(i * 0.5) * 5.0);
            timestamps.Add(baseDate.AddDays(i));
        }

        // Query is an inverted pattern that doesn't match any historical window with r >= 0.99
        int queryStart = 65;
        int queryEnd = 75;

        var result = PearsonProjectionAnalysis.CalculateProjection(
            samples,
            timestamps,
            queryStartIndex: queryStart,
            queryEndIndex: queryEnd,
            futureSteps: 10,
            minCorrelation: 0.999, // Unreachable threshold
            topK: 1);

        Assert.False(result.HasMatch);
        Assert.Empty(result.ProjectedPoints);
    }

    [Fact]
    public void CalculateProjection_TopKMatches_CombinesMultipleHistoricalMatches()
    {
        int totalBars = 150;
        var samples = new List<double>(totalBars);
        var timestamps = new List<DateTime>(totalBars);
        var baseDate = new DateTime(2024, 1, 1);

        for (int i = 0; i < totalBars; i++)
        {
            samples.Add(100.0);
            timestamps.Add(baseDate.AddDays(i));
        }

        // Inject 2 matching patterns in history
        // Match 1 at 20..30
        for (int i = 0; i <= 10; i++) samples[20 + i] = 100.0 + i * 2.0;
        for (int k = 1; k <= 10; k++) samples[30 + k] = samples[30] + k * 1.0;

        // Match 2 at 60..70
        for (int i = 0; i <= 10; i++) samples[60 + i] = 120.0 + i * 1.8;
        for (int k = 1; k <= 10; k++) samples[70 + k] = samples[70] + k * 2.0;

        // Query at 120..130
        for (int i = 0; i <= 10; i++) samples[120 + i] = 150.0 + i * 2.5;

        var result = PearsonProjectionAnalysis.CalculateProjection(
            samples,
            timestamps,
            queryStartIndex: 120,
            queryEndIndex: 130,
            futureSteps: 10,
            minCorrelation: 0.80,
            topK: 2);

        Assert.True(result.HasMatch);
        Assert.Equal(2, result.MatchedPatterns.Count);
        Assert.Equal(11, result.ProjectedPoints.Count);
        Assert.Equal(11, result.UpperBandPoints.Count);
        Assert.Equal(11, result.LowerBandPoints.Count);

        // Verify upper band is above lower band
        for (int i = 1; i < result.ProjectedPoints.Count; i++)
        {
            Assert.True(result.UpperBandPoints[i].Y >= result.ProjectedPoints[i].Y);
            Assert.True(result.LowerBandPoints[i].Y <= result.ProjectedPoints[i].Y);
        }
    }

    [Fact]
    public void CalculateProjection_VolatilityScaling_ScalesProjectedReturnMagnitude()
    {
        int totalBars = 100;
        var samples = new List<double>(totalBars);
        var timestamps = new List<DateTime>(totalBars);
        var baseDate = new DateTime(2024, 1, 1);

        for (int i = 0; i < totalBars; i++)
        {
            samples.Add(100.0);
            timestamps.Add(baseDate.AddDays(i));
        }

        // High volatility historical pattern at 10..20 (V-shape, amplitude = 20)
        for (int i = 0; i <= 10; i++)
        {
            double shape = i <= 5 ? -i * 4.0 : -(10 - i) * 4.0;
            samples[10 + i] = 120.0 + shape;
        }
        // Followed by strong upward rally at 21..30 (+20% gain from samples[20])
        for (int k = 1; k <= 10; k++) samples[20 + k] = samples[20] + k * 2.5;

        // Low volatility query pattern at 70..80 (same V-shape, amplitude = 2, 10x smaller)
        for (int i = 0; i <= 10; i++)
        {
            double shape = i <= 5 ? -i * 0.4 : -(10 - i) * 0.4;
            samples[70 + i] = 100.0 + shape;
        }

        // 1. Without Volatility Scaling: raw past return (+20%) is applied directly
        var unscaledResult = PearsonProjectionAnalysis.CalculateProjection(
            samples, timestamps,
            queryStartIndex: 70, queryEndIndex: 80,
            futureSteps: 10, minCorrelation: 0.8, topK: 1,
            applyVolatilityScaling: false);

        // 2. With Volatility Scaling: past return is scaled down by ~10x
        var scaledResult = PearsonProjectionAnalysis.CalculateProjection(
            samples, timestamps,
            queryStartIndex: 70, queryEndIndex: 80,
            futureSteps: 10, minCorrelation: 0.8, topK: 1,
            applyVolatilityScaling: true);

        Assert.True(unscaledResult.HasMatch);
        Assert.True(scaledResult.HasMatch);

        double unscaledDelta = unscaledResult.ProjectedPoints[^1].Y - unscaledResult.ProjectedPoints[0].Y;
        double scaledDelta = scaledResult.ProjectedPoints[^1].Y - scaledResult.ProjectedPoints[0].Y;

        Assert.True(unscaledDelta > 0, "Unscaled delta should be positive");
        Assert.True(scaledDelta > 0, "Scaled delta should be positive");
        Assert.True(scaledDelta < unscaledDelta, "Volatility scaling should compress return amplitude for low-volatility query");
    }

    [Fact]
    public void CalculateProjection_Detrending_MatchesDetrendedOscillations()
    {
        int totalBars = 100;
        var samples = new List<double>(totalBars);
        var timestamps = new List<DateTime>(totalBars);
        var baseDate = new DateTime(2024, 1, 1);

        for (int i = 0; i < totalBars; i++)
        {
            samples.Add(100.0);
            timestamps.Add(baseDate.AddDays(i));
        }

        // Pattern 1 (index 10..20): Flat base trend with sinusoidal oscillation
        for (int i = 0; i <= 10; i++)
        {
            samples[10 + i] = 100.0 + Math.Sin(i * 0.6) * 3.0;
        }
        for (int k = 1; k <= 10; k++) samples[20 + k] = 100.0 + k * 1.0;

        // Pattern 2 (index 60..70): Strong upward slope (i * 5.0) with same sinusoidal oscillation superimposed
        for (int i = 0; i <= 10; i++)
        {
            samples[60 + i] = 100.0 + (i * 5.0) + Math.Sin(i * 0.6) * 3.0;
        }

        // With detrending = true, the underlying sinusoidal shape matches with high correlation
        var detrendResult = PearsonProjectionAnalysis.CalculateProjection(
            samples, timestamps,
            queryStartIndex: 60, queryEndIndex: 70,
            futureSteps: 10, minCorrelation: 0.80, topK: 1,
            applyDetrend: true);

        Assert.True(detrendResult.HasMatch);
        Assert.True(detrendResult.BestCorrelation > 0.85);
        Assert.Equal(timestamps[10], detrendResult.MatchedStartTime);
    }
}
