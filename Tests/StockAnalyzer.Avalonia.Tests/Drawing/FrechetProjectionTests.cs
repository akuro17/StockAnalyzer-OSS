using System;
using System.Collections.Generic;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Behaviors;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class FrechetProjectionTests
{
    private static LinearCoordinateTransform MakeTransform()
        => new LinearCoordinateTransform(
            new DateTime(2024, 1, 1), new DateTime(2024, 1, 10),
            0m, 200m, 800, 600);

    private static List<CoreCandleData> MakeSampleCandles(int count, decimal startPrice = 100m, decimal step = 1m)
    {
        var list = new List<CoreCandleData>();
        var baseTime = new DateTime(2024, 1, 1, 0, 0, 0);
        for (int i = 0; i < count; i++)
        {
            var p = startPrice + i * step;
            list.Add(new CoreCandleData(
                baseTime.AddDays(i),
                p,
                p + 2m,
                p - 2m,
                p,
                1000
            ));
        }
        return list;
    }

    [Fact]
    public void FrechetProjectionObject_InitialPlacement_Defaults()
    {
        var obj = new FrechetProjectionObject();

        Assert.Equal(ChartObjectType.FrechetProjection, obj.Type);
        Assert.Equal(-10, obj.ZIndex);
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.Color);
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.FillColor);
        Assert.Equal(10, obj.FillOpacity);
        Assert.Equal(20, obj.FutureSteps);
        Assert.True(obj.ShowConfidenceBand);
        Assert.True(obj.ShowMatchHighlight);
        Assert.False(obj.IsUnmatched);
        Assert.Empty(obj.ProjectedPath);
        Assert.Empty(obj.UpperBandPath);
        Assert.Empty(obj.LowerBandPath);
    }

    [Fact]
    public void DrawingToolBehaviorRegistry_Returns_FrechetProjectionBehavior()
    {
        var behavior = DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.FrechetProjection);
        Assert.NotNull(behavior);
        Assert.IsType<FrechetProjectionBehavior>(behavior);
    }

    [Fact]
    public void FrechetProjectionObject_HitTest_WorksCorrectly()
    {
        var obj = new FrechetProjectionObject();
        obj.Points.Add(new ChartPoint(new DateTime(2024, 1, 2), 100m));
        obj.Points.Add(new ChartPoint(new DateTime(2024, 1, 6), 150m));

        var transform = MakeTransform();
        var p1 = transform.ChartToScreen(obj.Points[0]);
        var p2 = transform.ChartToScreen(obj.Points[1]);

        double midX = (p1.X + p2.X) / 2.0;

        // Inside selection span
        Assert.True(obj.HitTest(new global::Avalonia.Point(midX, 300), transform));

        // Outside selection span
        Assert.False(obj.HitTest(new global::Avalonia.Point(p1.X - 50, 300), transform));
        Assert.False(obj.HitTest(new global::Avalonia.Point(p2.X + 50, 300), transform));
    }

    [Fact]
    public void FrechetProjectionObject_Recalculate_PopulatesProjections()
    {
        var candles = MakeSampleCandles(80);
        var obj = new FrechetProjectionObject
        {
            FutureSteps = 10
        };
        obj.Points.Add(new ChartPoint(candles[50].Timestamp, candles[50].Close));
        obj.Points.Add(new ChartPoint(candles[65].Timestamp, candles[65].Close));

        obj.Recalculate(candles, TimeSpan.FromDays(1));

        Assert.True(obj.HasMatch);
        Assert.NotEmpty(obj.ProjectedPath);
        Assert.NotEmpty(obj.UpperBandPath);
        Assert.NotEmpty(obj.LowerBandPath);
        Assert.NotNull(obj.MatchedStartTime);
        Assert.NotNull(obj.MatchedEndTime);

        var calcValues = obj.GetCalculatedValues(DateTime.Now);
        Assert.NotEmpty(calcValues);
        Assert.Contains(calcValues, v => v.Label.Contains("Fréchet Distance"));
    }

    [Fact]
    public void FrechetProjectionObject_ConfidenceMultiplier_ScalesBandWidth()
    {
        var candles = MakeSampleCandles(80);
        // Inject a slight variation so residual sigma_R is non-zero
        for (int i = 50; i <= 65; i++)
        {
            decimal peak = (i % 2 == 0) ? 2m : -2m;
            var old = candles[i];
            candles[i] = new CoreCandleData(old.Timestamp, old.Open, old.High, old.Low, old.Close + peak, old.Volume);
        }

        var obj1 = new FrechetProjectionObject
        {
            FutureSteps = 10,
            ConfidenceMultiplier = 1.0m
        };
        obj1.Points.Add(new ChartPoint(candles[50].Timestamp, candles[50].Close));
        obj1.Points.Add(new ChartPoint(candles[65].Timestamp, candles[65].Close));
        obj1.Recalculate(candles, TimeSpan.FromDays(1));

        var obj2 = new FrechetProjectionObject
        {
            FutureSteps = 10,
            ConfidenceMultiplier = 2.0m
        };
        obj2.Points.Add(new ChartPoint(candles[50].Timestamp, candles[50].Close));
        obj2.Points.Add(new ChartPoint(candles[65].Timestamp, candles[65].Close));
        obj2.Recalculate(candles, TimeSpan.FromDays(1));

        Assert.NotEmpty(obj1.UpperBandPath);
        Assert.NotEmpty(obj2.UpperBandPath);

        double width1 = obj1.UpperBandPath[1].Y - obj1.ProjectedPath[1].Y;
        double width2 = obj2.UpperBandPath[1].Y - obj2.ProjectedPath[1].Y;

        Assert.True(width2 > width1);
        Assert.Equal(2.0 * width1, width2, precision: 4);
    }

    [Fact]
    public void DeferredComputationRecalculator_Handles_FrechetProjectionObject()
    {
        var candles = MakeSampleCandles(80);
        var obj = new FrechetProjectionObject();
        obj.Points.Add(new ChartPoint(candles[50].Timestamp, candles[50].Close));
        obj.Points.Add(new ChartPoint(candles[65].Timestamp, candles[65].Close));

        bool handled = DeferredComputationRecalculator.TryRecalculate(obj, candles);
        Assert.True(handled);
    }

    [Fact]
    public void FrechetProjectionObject_MaxDistanceThreshold_Triggers_Unmatched_WhenExceeded()
    {
        var candles = MakeSampleCandles(80);
        // Inject a sharp peak into the query pattern so its shape differs from the linear past
        for (int i = 50; i <= 65; i++)
        {
            decimal peak = i <= 57 ? (i - 50) * 5m : (65 - i) * 5m;
            var old = candles[i];
            candles[i] = new CoreCandleData(old.Timestamp, old.Open, old.High, old.Low, old.Close + peak, old.Volume);
        }

        var obj = new FrechetProjectionObject
        {
            FutureSteps = 10,
            MaxDistance = 0.1 // Strict threshold; dissimilar linear past has distance > 1.0
        };
        obj.Points.Add(new ChartPoint(candles[50].Timestamp, candles[50].Close));
        obj.Points.Add(new ChartPoint(candles[65].Timestamp, candles[65].Close));

        obj.Recalculate(candles, TimeSpan.FromDays(1));

        Assert.False(obj.HasMatch);
        Assert.True(obj.IsUnmatched);
        Assert.Empty(obj.ProjectedPath);

        // Relaxing the threshold allows the match
        obj.MaxDistance = 5.0;
        obj.Recalculate(candles, TimeSpan.FromDays(1));
        Assert.True(obj.HasMatch);
        Assert.False(obj.IsUnmatched);
        Assert.NotEmpty(obj.ProjectedPath);
    }
}
