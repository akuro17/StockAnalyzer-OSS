using System;
using System.Collections.Generic;
using Avalonia.Media;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class KalmanFilterProjectionTests
{
    private static LinearCoordinateTransform MakeTransform()
        => new LinearCoordinateTransform(
            new DateTime(2024, 1, 1), new DateTime(2024, 1, 2),
            0m, 100m, 800, 600);

    private static List<CoreCandleData> MakeSampleCandles(int count, decimal startPrice = 100m, decimal step = 2m)
    {
        var list = new List<CoreCandleData>();
        var baseTime = new DateTime(2024, 1, 1, 0, 0, 0);
        for (int i = 0; i < count; i++)
        {
            var p = startPrice + i * step;
            list.Add(new CoreCandleData(
                baseTime.AddHours(i),
                p - 1m,
                p + 2m,
                p - 2m,
                p,
                1000
            ));
        }
        return list;
    }

    [Fact]
    public void KalmanFilterProjectionObject_InitialPlacement_Defaults()
    {
        var obj = new KalmanFilterProjectionObject();

        Assert.Equal(ChartObjectType.KalmanFilterProjection, obj.Type);
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.Color);
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.FillColor);
        Assert.Equal(10, obj.FillOpacity);
        Assert.Equal(20, obj.FutureSteps);
        Assert.Equal(0.01m, obj.Q);
        Assert.Equal(0.1m, obj.R);
        Assert.True(obj.ShowConfidenceBand);
        Assert.Equal(2.0m, obj.ConfidenceMultiplier);
        Assert.Empty(obj.ProjectedPath);
        Assert.Empty(obj.UpperBandPath);
        Assert.Empty(obj.LowerBandPath);
    }

    [Fact]
    public void KalmanFilterProjectionObject_Recalculate_CalculatesProjectedPath_And_ConfidenceBands()
    {
        var candles = MakeSampleCandles(10, startPrice: 100m, step: 2m);
        var obj = new KalmanFilterProjectionObject
        {
            FutureSteps = 10,
            Q = 0.01m,
            R = 0.1m,
            ShowConfidenceBand = true,
            ConfidenceMultiplier = 2.0m
        };

        // Select candles from index 0 to 4 (hours 0 to 4)
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[4].Timestamp, candles[4].Close));

        obj.Recalculate(candles, TimeSpan.FromHours(1));

        Assert.NotEmpty(obj.ProjectedPath);
        Assert.Equal(11, obj.ProjectedPath.Count); // initial connection point + 10 steps
        Assert.Equal(11, obj.UpperBandPath.Count);
        Assert.Equal(11, obj.LowerBandPath.Count);

        // Verify that the projected path trends upward with positive velocity
        var first = obj.ProjectedPath[0];
        var last = obj.ProjectedPath[^1];
        Assert.True(last.Y > first.Y, $"Expected upward projection: last ({last.Y}) > first ({first.Y})");

        // Verify that upper band > center > lower band for future projected steps
        for (int i = 1; i < obj.ProjectedPath.Count; i++)
        {
            Assert.True(obj.UpperBandPath[i].Y > obj.ProjectedPath[i].Y, $"Upper band ({obj.UpperBandPath[i].Y}) should exceed center ({obj.ProjectedPath[i].Y}) at step {i}");
            Assert.True(obj.LowerBandPath[i].Y < obj.ProjectedPath[i].Y, $"Lower band ({obj.LowerBandPath[i].Y}) should be below center ({obj.ProjectedPath[i].Y}) at step {i}");
        }

        // Verify that confidence margin widens as future steps increase (diffusion cone)
        double margin1 = obj.UpperBandPath[1].Y - obj.ProjectedPath[1].Y;
        double marginLast = obj.UpperBandPath[^1].Y - obj.ProjectedPath[^1].Y;
        Assert.True(marginLast > margin1, $"Expected widening confidence cone: marginLast ({marginLast}) > margin1 ({margin1})");
    }

    [Fact]
    public void KalmanFilterProjectionObject_Recalculate_WithEmptyCandles_ClearsPath()
    {
        var obj = new KalmanFilterProjectionObject();
        obj.Points.Add(new ChartPoint(DateTime.Now, 100m));
        obj.Points.Add(new ChartPoint(DateTime.Now.AddDays(1), 110m));

        obj.Recalculate(new List<CoreCandleData>());

        Assert.Empty(obj.ProjectedPath);
        Assert.Empty(obj.UpperBandPath);
        Assert.Empty(obj.LowerBandPath);
    }

    [Fact]
    public void KalmanFilterProjectionObject_Translate_UpdatesPointsAndProjectedPath()
    {
        var candles = MakeSampleCandles(5, startPrice: 50m, step: 1m);
        var obj = new KalmanFilterProjectionObject();
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[3].Timestamp, candles[3].Close));
        obj.Recalculate(candles, TimeSpan.FromHours(1));

        var origPoint0 = obj.Points[0];
        var origFirstPath = obj.ProjectedPath[0];
        var origFirstUpper = obj.UpperBandPath[0];
        var origFirstLower = obj.LowerBandPath[0];

        var timeShift = TimeSpan.FromHours(2);
        var priceShift = 10m;

        obj.Translate(timeShift, priceShift);

        Assert.Equal(origPoint0.Time + timeShift, obj.Points[0].Time);
        Assert.Equal(origPoint0.Price + priceShift, obj.Points[0].Price);
        Assert.Equal(origFirstPath.X + timeShift.Ticks, obj.ProjectedPath[0].X);
        Assert.Equal(origFirstPath.Y + (double)priceShift, obj.ProjectedPath[0].Y);
        Assert.Equal(origFirstUpper.X + timeShift.Ticks, obj.UpperBandPath[0].X);
        Assert.Equal(origFirstUpper.Y + (double)priceShift, obj.UpperBandPath[0].Y);
        Assert.Equal(origFirstLower.X + timeShift.Ticks, obj.LowerBandPath[0].X);
        Assert.Equal(origFirstLower.Y + (double)priceShift, obj.LowerBandPath[0].Y);
    }

    [Fact]
    public void KalmanFilterProjectionObject_VerticalRange_HitTest_CoversFullHeight()
    {
        var t = MakeTransform();
        var start = new ChartPoint(new DateTime(2024, 1, 1, 4, 0, 0), 20m);
        var end = new ChartPoint(new DateTime(2024, 1, 1, 8, 0, 0), 80m);
        var obj = new KalmanFilterProjectionObject();
        obj.Points.Add(start);
        obj.Points.Add(end);

        var pStart = t.ChartToScreen(start);
        var pEnd = t.ChartToScreen(end);
        double midX = (pStart.X + pEnd.X) / 2;

        // Test hit at the very top (Y=0)
        Assert.True(obj.HitTest(new global::Avalonia.Point(midX, 0), t));
        // Test hit at the very bottom (Y=600)
        Assert.True(obj.HitTest(new global::Avalonia.Point(midX, 600), t));
        // Test hit near vertical line 1 (start)
        Assert.True(obj.HitTest(new global::Avalonia.Point(pStart.X, 300), t));
        // Test hit near vertical line 2 (end)
        Assert.True(obj.HitTest(new global::Avalonia.Point(pEnd.X, 300), t));
        // Test miss outside range
        Assert.False(obj.HitTest(new global::Avalonia.Point(pStart.X - 50, 300), t));
        Assert.False(obj.HitTest(new global::Avalonia.Point(pEnd.X + 50, 300), t));
    }

    [Fact]
    public void KalmanFilterProjectionObject_Recalculate_WithVolatileCandles_ScalesConfidenceBandToPriceVolatility()
    {
        // 3000 yen base price with ~30 yen fluctuations
        var baseTime = new DateTime(2024, 1, 1);
        var candles = new List<CoreCandleData>();
        decimal[] prices = [3000m, 3030m, 2980m, 3020m, 2990m, 3040m, 3010m];
        for (int i = 0; i < prices.Length; i++)
        {
            candles.Add(new CoreCandleData(baseTime.AddDays(i), prices[i] - 10m, prices[i] + 15m, prices[i] - 15m, prices[i], 1000));
        }

        var obj = new KalmanFilterProjectionObject
        {
            FutureSteps = 10,
            ShowConfidenceBand = true,
            ConfidenceMultiplier = 2.0m
        };

        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[^1].Timestamp, candles[^1].Close));

        obj.Recalculate(candles, TimeSpan.FromDays(1));

        // Margin at step 1 should be substantial (in tens of yen, not fractions of a yen)
        double margin1 = obj.UpperBandPath[1].Y - obj.ProjectedPath[1].Y;
        Assert.True(margin1 > 5.0, $"Expected realistic price margin (> 5.0 yen), but got {margin1}");
    }
}
