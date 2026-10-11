using System;
using System.Collections.Generic;
using System.Diagnostics;
using StockAnalyzer.Core.Models.Training;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models.Training;

/// <summary>
/// <see cref="WalkForwardDataRequirement.MinimumRawBarsForTraining"/> skips whole blocks of sample counts while it searches. This test does not use that skip:
/// it evaluates the feasibility of every sample count around the result one by one and checks that the result is the smallest count from which every
/// larger count is feasible.
/// </summary>
public sealed class WalkForwardDataRequirementEquivalenceTests
{
    private static readonly int[] SplitCounts = { 1, 3, 10 };
    private static readonly int[] Windows = { 1, 3, 20, 128 };
    private static readonly int[] Horizons = { 1, 5, 64 };
    private static readonly int[] Lags = { 0, 8 };
    private static readonly bool[] Calibrations = { false, true };

    /// <summary>The whole grid must finish within this budget on the development machine (the upper sweep limit is halved if it does not).</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    /// <summary>Factor of the upper sweep limit <c>U = SweepFactor * (K + 1) * (gap + depth + 2)</c>.</summary>
    private const int SweepFactor = 4;

    [Fact]
    public void TheResult_IsTheSmallestCountFromWhichEveryLargerCountIsFeasible_ForTheWholeGrid()
    {
        var clock = Stopwatch.StartNew();
        int tuples = 0;
        foreach (int k in SplitCounts)
        foreach (int w in Windows)
        foreach (int h in Horizons)
        foreach (int lag in Lags)
        foreach (int? gap in new int?[] { null, 0, k + 1 })
        foreach (bool calibration in Calibrations)
        {
            tuples++;
            long windowOffset = (long)w + h - 1;
            long resolvedGap = gap ?? windowOffset;
            long depth = windowOffset + lag;
            string label = $"K={k} W={w} H={h} L={lag} gap={(gap?.ToString() ?? "null")} calibration={calibration}";

            int minimum = WalkForwardDataRequirement.MinimumRawBarsForTraining(w, h, k, gap, lag, calibration);
            long smin = minimum - windowOffset - lag;   // the windowed-sample count at the minimum raw count

            Assert.True(WalkForwardDataRequirement.TrainingRunFeasible(smin, k, resolvedGap, depth, calibration), $"{label}: smin={smin} must be feasible");
            Assert.True(smin >= 1, $"{label}: smin={smin}");
            Assert.False(WalkForwardDataRequirement.TrainingRunFeasible(smin - 1, k, resolvedGap, depth, calibration), $"{label}: smin-1 must be infeasible");

            long upper = (long)SweepFactor * (k + 1) * (resolvedGap + depth + 2);
            for (long t = smin; t <= upper; t++)
                Assert.True(WalkForwardDataRequirement.TrainingRunFeasible(t, k, resolvedGap, depth, calibration), $"{label}: t={t} must be feasible");
        }

        Assert.Equal(SplitCounts.Length * Windows.Length * Horizons.Length * Lags.Length * 3 * Calibrations.Length, tuples);
        Assert.True(clock.Elapsed < Budget, $"The grid took {clock.Elapsed}; reduce {nameof(SweepFactor)} and record it in the step log.");
    }

    [Fact]
    public void ZeroSamples_AreNeverFeasible()
    {
        Assert.False(WalkForwardDataRequirement.TrainingRunFeasible(0, 3, 0, 1, calibrationSegment: false));
    }
}
