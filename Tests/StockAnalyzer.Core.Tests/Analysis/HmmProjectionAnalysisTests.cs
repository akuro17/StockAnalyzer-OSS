using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

public class HmmProjectionAnalysisTests
{
    [Fact]
    public void CalculateProjection_WithEmptyOrInsufficientSamples_ReturnsEmpty()
    {
        var result1 = HmmProjectionAnalysis.CalculateProjection(
            Array.Empty<double>(),
            Array.Empty<DateTime>());
        Assert.Equal(0, result1.SampleCount);
        Assert.Empty(result1.ProjectedPoints);

        // Less than MinSampleCount (10)
        var result2 = HmmProjectionAnalysis.CalculateProjection(
            new double[] { 10.0, 11.0, 12.0, 13.0, 14.0, 15.0, 16.0, 17.0, 18.0 },
            new DateTime[]
            {
                DateTime.Now, DateTime.Now.AddDays(1), DateTime.Now.AddDays(2),
                DateTime.Now.AddDays(3), DateTime.Now.AddDays(4), DateTime.Now.AddDays(5),
                DateTime.Now.AddDays(6), DateTime.Now.AddDays(7), DateTime.Now.AddDays(8)
            });
        Assert.Equal(0, result2.SampleCount);
        Assert.Empty(result2.ProjectedPoints);
    }

    [Fact]
    public void CalculateProjection_Step0_MatchesLastObservedPrice()
    {
        int n = 30;
        var samples = new List<double>(n);
        var timestamps = new List<DateTime>(n);
        var baseDate = new DateTime(2024, 1, 1);

        double price = 150.0;
        for (int i = 0; i < n; i++)
        {
            samples.Add(price);
            timestamps.Add(baseDate.AddDays(i));
            price += (i % 2 == 0) ? 1.0 : -0.5;
        }

        var result = HmmProjectionAnalysis.CalculateProjection(
            samples,
            timestamps,
            futureSteps: 10,
            timeframeSpan: TimeSpan.FromDays(1));

        Assert.NotEmpty(result.ProjectedPoints);
        double lastObservedPrice = samples[^1];
        long lastTimestampTicks = timestamps[^1].Ticks;

        Assert.Equal((double)lastTimestampTicks, result.ProjectedPoints[0].X);
        Assert.Equal((double)lastTimestampTicks, result.UpperBandPoints[0].X);
        Assert.Equal((double)lastTimestampTicks, result.LowerBandPoints[0].X);

        Assert.Equal(lastObservedPrice, result.ProjectedPoints[0].Y, 6);
        Assert.Equal(lastObservedPrice, result.UpperBandPoints[0].Y, 6);
        Assert.Equal(lastObservedPrice, result.LowerBandPoints[0].Y, 6);
    }

    [Fact]
    public void CalculateProjection_ZeroVolatility_ConeWidthIsZero()
    {
        // 20 identical samples
        int n = 20;
        var samples = new List<double>(n);
        var timestamps = new List<DateTime>(n);
        var baseDate = new DateTime(2024, 1, 1);

        for (int i = 0; i < n; i++)
        {
            samples.Add(100.0);
            timestamps.Add(baseDate.AddDays(i));
        }

        var result = HmmProjectionAnalysis.CalculateProjection(
            samples,
            timestamps,
            futureSteps: 10,
            timeframeSpan: TimeSpan.FromDays(1),
            confidenceMultiplier: 0.0m);

        Assert.NotEmpty(result.ProjectedPoints);
        for (int i = 0; i < result.ProjectedPoints.Count; i++)
        {
            Assert.Equal(result.ProjectedPoints[i].Y, result.UpperBandPoints[i].Y, 4);
            Assert.Equal(result.ProjectedPoints[i].Y, result.LowerBandPoints[i].Y, 4);
        }
    }

    [Fact]
    public void CalculateProjection_ExtremeMultiplier_ClampedWithoutOverflow()
    {
        int n = 30;
        var samples = new List<double>(n);
        var timestamps = new List<DateTime>(n);
        var baseDate = new DateTime(2024, 1, 1);

        double price = 100.0;
        for (int i = 0; i < n; i++)
        {
            samples.Add(price);
            timestamps.Add(baseDate.AddDays(i));
            price *= (i % 2 == 0) ? 1.15 : 0.85; // High volatility
        }

        var result = HmmProjectionAnalysis.CalculateProjection(
            samples,
            timestamps,
            futureSteps: 20,
            timeframeSpan: TimeSpan.FromDays(1),
            confidenceMultiplier: 10.0m); // Maximum multiplier

        Assert.NotEmpty(result.ProjectedPoints);
        foreach (var pt in result.ProjectedPoints)
        {
            Assert.False(double.IsInfinity(pt.Y));
            Assert.False(double.IsNaN(pt.Y));
            Assert.True(pt.Y > 0.0);
        }
        foreach (var pt in result.UpperBandPoints)
        {
            Assert.False(double.IsInfinity(pt.Y));
            Assert.False(double.IsNaN(pt.Y));
            Assert.True(pt.Y > 0.0);
        }
        foreach (var pt in result.LowerBandPoints)
        {
            Assert.False(double.IsInfinity(pt.Y));
            Assert.False(double.IsNaN(pt.Y));
            Assert.True(pt.Y > 0.0);
        }
    }

    [Fact]
    public void CalculateProjection_RegimeShiftToBull_ProjectsUpwardTrajectory_And_HighBullProbability()
    {
        // 50 samples: first 25 bars Bear (-1.5%), last 25 bars Bull (+1.5%)
        int n = 50;
        var samples = new List<double>(n);
        var timestamps = new List<DateTime>(n);
        var baseDate = new DateTime(2024, 1, 1);

        double price = 100.0;
        for (int i = 0; i < n; i++)
        {
            samples.Add(price);
            timestamps.Add(baseDate.AddDays(i));
            if (i < 25)
            {
                price *= 0.985; // Bear: -1.5%
            }
            else
            {
                price *= 1.015; // Bull: +1.5%
            }
        }

        var result = HmmProjectionAnalysis.CalculateProjection(
            samples,
            timestamps,
            futureSteps: 20,
            timeframeSpan: TimeSpan.FromDays(1),
            states: 2,
            maxIterations: 30,
            showConfidenceBand: true,
            confidenceMultiplier: 2.0m);

        Assert.Equal(n, result.SampleCount);
        Assert.Equal(20 + 1, result.ProjectedPoints.Count);
        Assert.Equal(20 + 1, result.UpperBandPoints.Count);
        Assert.Equal(20 + 1, result.LowerBandPoints.Count);

        // State 0 is Bear (< 0), State 1 is Bull (> 0)
        Assert.True(result.StateMeans[0] < 0, $"Expected Bear state mean < 0, got {result.StateMeans[0]}");
        Assert.True(result.StateMeans[1] > 0, $"Expected Bull state mean > 0, got {result.StateMeans[1]}");

        // Since the window ends in 25 consecutive Bull bars, Bull state probability at the end should be high (> 0.7)
        Assert.True(result.BullStateProbability >= 0.7, $"Expected Bull probability >= 0.7, got {result.BullStateProbability}");
        Assert.Equal(1, result.CurrentRegimeIndex);

        // Projected points should trend upward
        var firstPt = result.ProjectedPoints[0];
        var lastPt = result.ProjectedPoints[^1];
        Assert.True(lastPt.Y > firstPt.Y, $"Expected upward projection: last ({lastPt.Y}) > first ({firstPt.Y})");

        // Upper > Center > Lower for future steps
        for (int i = 1; i < result.ProjectedPoints.Count; i++)
        {
            Assert.True(result.UpperBandPoints[i].Y >= result.ProjectedPoints[i].Y, $"Upper band should be >= center at step {i}");
            Assert.True(result.LowerBandPoints[i].Y <= result.ProjectedPoints[i].Y, $"Lower band should be <= center at step {i}");
        }

        // Confidence band should widen as future steps increase
        double marginStep1 = result.UpperBandPoints[1].Y - result.ProjectedPoints[1].Y;
        double marginLast = result.UpperBandPoints[^1].Y - result.ProjectedPoints[^1].Y;
        Assert.True(marginLast > marginStep1, $"Confidence cone should widen: marginLast ({marginLast}) > marginStep1 ({marginStep1})");
    }

    [Fact]
    public void CalculateProjection_RegimeShiftToBear_ProjectsDownwardTrajectory_And_LowBullProbability()
    {
        // 50 samples: first 25 bars Bull (+1.5%), last 25 bars Bear (-1.5%)
        int n = 50;
        var samples = new List<double>(n);
        var timestamps = new List<DateTime>(n);
        var baseDate = new DateTime(2024, 1, 1);

        double price = 100.0;
        for (int i = 0; i < n; i++)
        {
            samples.Add(price);
            timestamps.Add(baseDate.AddDays(i));
            if (i < 25)
            {
                price *= 1.015; // Bull: +1.5%
            }
            else
            {
                price *= 0.985; // Bear: -1.5%
            }
        }

        var result = HmmProjectionAnalysis.CalculateProjection(
            samples,
            timestamps,
            futureSteps: 15,
            timeframeSpan: TimeSpan.FromDays(1),
            states: 2,
            maxIterations: 30);

        Assert.Equal(n, result.SampleCount);
        Assert.Equal(15 + 1, result.ProjectedPoints.Count);

        // Since it ends in Bear regime, Bull state probability should be low (< 0.3)
        Assert.True(result.BullStateProbability <= 0.3, $"Expected Bull probability <= 0.3, got {result.BullStateProbability}");
        Assert.Equal(0, result.CurrentRegimeIndex);

        // Projected points should trend downward
        var firstPt = result.ProjectedPoints[0];
        var lastPt = result.ProjectedPoints[^1];
        Assert.True(lastPt.Y < firstPt.Y, $"Expected downward projection: last ({lastPt.Y}) < first ({firstPt.Y})");
    }

    [Fact]
    public void CalculateProjection_ThreeStates_CalculatesThreeRegimesAndTransitionMatrix()
    {
        // 60 samples alternating between Bull, Neutral, Bear
        int n = 60;
        var samples = new List<double>(n);
        var timestamps = new List<DateTime>(n);
        var baseDate = new DateTime(2024, 1, 1);

        double price = 100.0;
        for (int i = 0; i < n; i++)
        {
            samples.Add(price);
            timestamps.Add(baseDate.AddDays(i));
            if (i < 20) price *= 1.015;       // Bull
            else if (i < 40) price += 0.1;    // Neutral
            else price *= 0.985;              // Bear
        }

        var result = HmmProjectionAnalysis.CalculateProjection(
            samples,
            timestamps,
            futureSteps: 10,
            timeframeSpan: TimeSpan.FromDays(1),
            states: 3,
            maxIterations: 30);

        Assert.Equal(n, result.SampleCount);
        Assert.Equal(3, result.FilteredStateProbabilities.Count);
        Assert.Equal(3, result.StateMeans.Count);
        Assert.Equal(3, result.StateStdDevs.Count);
        Assert.Equal(3, result.TransitionMatrix.GetLength(0));
        Assert.Equal(3, result.TransitionMatrix.GetLength(1));

        // State means should be in ascending order (canonical ordering: State 0 < State 1 < State 2)
        Assert.True(result.StateMeans[0] <= result.StateMeans[1]);
        Assert.True(result.StateMeans[1] <= result.StateMeans[2]);
    }
}
