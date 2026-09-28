using System;
using System.Collections.Generic;
using Avalonia.Media;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Behaviors;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class PearsonProjectionTests
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
    public void PearsonProjectionObject_InitialPlacement_Defaults()
    {
        var obj = new PearsonProjectionObject();

        Assert.Equal(ChartObjectType.PearsonProjection, obj.Type);
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.Color);
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.FillColor);
        Assert.Equal(10, obj.FillOpacity);
        Assert.Equal(20, obj.FutureSteps);
        Assert.Equal(0.70, obj.MinCorrelation);
        Assert.Equal(1, obj.TopK);
        Assert.True(obj.ApplyVolatilityScaling);
        Assert.False(obj.ApplyDetrend);
        Assert.True(obj.ShowConfidenceBand);
        Assert.Equal(2.0m, obj.ConfidenceMultiplier);
        Assert.True(obj.ShowMatchHighlight);
        Assert.False(obj.IsUnmatched);
        Assert.Empty(obj.ProjectedPath);
        Assert.Empty(obj.UpperBandPath);
        Assert.Empty(obj.LowerBandPath);
    }

    [Fact]
    public void PearsonProjectionObject_Recalculate_CalculatesProjectedPath_And_MatchedRegion()
    {
        int totalBars = 100;
        var candles = MakeSampleCandles(totalBars, startPrice: 100m, step: 0.1m);

        // Inject V-shape in past (index 20..30)
        for (int i = 0; i <= 10; i++)
        {
            decimal shapeVal = i <= 5 ? -i * 2m : -(10 - i) * 2m;
            var old = candles[20 + i];
            candles[20 + i] = new CoreCandleData(old.Timestamp, old.Open, old.High, old.Low, 100m + shapeVal, old.Volume);
        }
        for (int k = 1; k <= 15; k++)
        {
            var old = candles[30 + k];
            candles[30 + k] = new CoreCandleData(old.Timestamp, old.Open, old.High, old.Low, candles[30].Close + k * 1.5m, old.Volume);
        }

        // Replicate same V-shape in current query (index 70..80)
        for (int i = 0; i <= 10; i++)
        {
            decimal shapeVal = i <= 5 ? -i * 2m : -(10 - i) * 2m;
            var old = candles[70 + i];
            candles[70 + i] = new CoreCandleData(old.Timestamp, old.Open, old.High, old.Low, 150m + shapeVal * 1.2m, old.Volume);
        }

        var obj = new PearsonProjectionObject
        {
            FutureSteps = 10,
            MinCorrelation = 0.80,
            TopK = 1
        };

        obj.Points.Add(new ChartPoint(candles[70].Timestamp, candles[70].Close));
        obj.Points.Add(new ChartPoint(candles[80].Timestamp, candles[80].Close));

        obj.Recalculate(candles, TimeSpan.FromDays(1));

        Assert.True(obj.HasMatch);
        Assert.False(obj.IsUnmatched);
        Assert.True(obj.BestCorrelation > 0.90);
        Assert.Equal(candles[20].Timestamp, obj.MatchedStartTime);
        Assert.Equal(candles[30].Timestamp, obj.MatchedEndTime);
        Assert.Equal(11, obj.ProjectedPath.Count);
        Assert.Equal(11, obj.UpperBandPath.Count);
        Assert.Equal(11, obj.LowerBandPath.Count);

        // Calculated values provider inspection
        var calculatedValues = obj.GetCalculatedValues(DateTime.Now);
        Assert.NotEmpty(calculatedValues);
        Assert.Contains(calculatedValues, v => v.Label.Contains("Pearson Correlation"));
    }

    [Fact]
    public void PearsonProjectionObject_Translate_UpdatesPoints_And_ProjectedPath()
    {
        var obj = new PearsonProjectionObject();
        var t0 = new DateTime(2024, 1, 1);
        var t1 = new DateTime(2024, 1, 5);

        obj.Points.Add(new ChartPoint(t0, 100m));
        obj.Points.Add(new ChartPoint(t1, 105m));

        obj.ProjectedPath = new List<StockAnalyzer.Core.Models.Point>
        {
            new StockAnalyzer.Core.Models.Point((double)t1.Ticks, 105.0),
            new StockAnalyzer.Core.Models.Point((double)t1.AddDays(1).Ticks, 110.0)
        };

        var delta = TimeSpan.FromDays(2);
        var priceDelta = 10m;

        obj.Translate(delta, priceDelta);

        Assert.Equal(t0 + delta, obj.Points[0].Time);
        Assert.Equal(110m, obj.Points[0].Price);
        Assert.Equal(t1 + delta, obj.Points[1].Time);
        Assert.Equal(115m, obj.Points[1].Price);

        Assert.Equal((double)(t1 + delta).Ticks, obj.ProjectedPath[0].X);
        Assert.Equal(115.0, obj.ProjectedPath[0].Y);
    }

    [Fact]
    public void PearsonProjectionObject_HitTest_WorksAccurately()
    {
        var transform = MakeTransform();
        var obj = new PearsonProjectionObject();

        var t0 = new DateTime(2024, 1, 3);
        var t1 = new DateTime(2024, 1, 7);

        obj.Points.Add(new ChartPoint(t0, 100m));
        obj.Points.Add(new ChartPoint(t1, 120m));

        var screen0 = transform.ChartToScreen(obj.Points[0]);
        var screen1 = transform.ChartToScreen(obj.Points[1]);
        var midX = (screen0.X + screen1.X) / 2.0;

        Assert.True(obj.HitTest(new global::Avalonia.Point(midX, 300), transform));
        Assert.False(obj.HitTest(new global::Avalonia.Point(screen0.X - 50, 300), transform));
        Assert.False(obj.HitTest(new global::Avalonia.Point(screen1.X + 50, 300), transform));
    }

    [Fact]
    public void DeferredComputationRecalculator_Handles_PearsonProjectionObject()
    {
        var candles = MakeSampleCandles(50);
        var obj = new PearsonProjectionObject();
        obj.Points.Add(new ChartPoint(candles[10].Timestamp, candles[10].Close));
        obj.Points.Add(new ChartPoint(candles[20].Timestamp, candles[20].Close));

        bool handled = DeferredComputationRecalculator.TryRecalculate(obj, candles);
        Assert.True(handled);
    }

    [Fact]
    public void DrawingToolBehaviorRegistry_Returns_PearsonProjectionBehavior()
    {
        var behavior = DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.PearsonProjection);
        Assert.NotNull(behavior);
        Assert.IsType<PearsonProjectionBehavior>(behavior);
    }
}
