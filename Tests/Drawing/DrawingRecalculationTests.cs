using System;
using System.Collections.Generic;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

/// <summary>
/// Characterization of <see cref="DrawingRecalculation"/>, the SSoT extracted from the chart drag code:
/// the per-frame pass must skip heavy bar-locked recomputation, and the release pass must run exactly what
/// <see cref="DeferredComputationRecalculator"/> would run directly.
/// </summary>
public class DrawingRecalculationTests
{
    private static List<CoreCandleData> BuildCandles()
    {
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 30; i++)
        {
            decimal close = 100m + i * 4m;
            candles.Add(new CoreCandleData(new DateTime(2025, 1, 1).AddDays(i), close, close + 5, close - 5, close, 1000 + i));
        }
        return candles;
    }

    private static FixedRangeVolumeProfileObject NewProfile()
        => new(new ChartPoint(new DateTime(2025, 1, 3), 100m), new ChartPoint(new DateTime(2025, 1, 20), 220m));

    [Fact]
    public void AfterMoveCompleted_WithCandles_RunsTheSameRecalculationAsTheDirectDispatcher()
    {
        var candles = BuildCandles();
        var viaHelper = NewProfile();
        var direct = NewProfile();
        Assert.Empty(viaHelper.ProfileData);

        DrawingRecalculation.AfterMoveCompleted(viaHelper, new DrawingCalculationContext(candles));
        DeferredComputationRecalculator.TryRecalculate(direct, new DrawingCalculationContext(candles));

        Assert.NotEmpty(viaHelper.ProfileData);
        Assert.Equal(direct.ProfileData.Count, viaHelper.ProfileData.Count);
    }

    [Fact]
    public void AfterMoveCompleted_WithoutCandles_DoesNothing()
    {
        var profile = NewProfile();

        DrawingRecalculation.AfterMoveCompleted(profile, DrawingCalculationContext.Empty);

        Assert.Empty(profile.ProfileData);
    }

    [Fact]
    public void AfterMoveFrame_SkipsBarLockedHeavyRecomputation()
    {
        var profile = NewProfile();

        DrawingRecalculation.AfterMoveFrame(profile, BuildCandles());

        Assert.Empty(profile.ProfileData);
    }

    [Fact]
    public void AfterMoveFrame_AndCompleted_IgnoreObjectsWithNothingDeferred()
    {
        var line = new TrendLineObject(new ChartPoint(new DateTime(2025, 1, 3), 100m), new ChartPoint(new DateTime(2025, 1, 9), 120m));
        var before = (line.Points[0], line.Points[1]);

        DrawingRecalculation.AfterMoveFrame(line, BuildCandles());
        DrawingRecalculation.AfterMoveCompleted(line, new DrawingCalculationContext(BuildCandles()));

        Assert.Equal(before, (line.Points[0], line.Points[1]));
    }
}
