using System;
using System.Collections.Generic;
using Avalonia.Media;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class ArimaProjectionTests
{
    private static LinearCoordinateTransform MakeTransform()
        => new LinearCoordinateTransform(
            new DateTime(2024, 1, 1), new DateTime(2024, 1, 2),
            0m, 100m, 800, 600);

    private static List<CoreCandleData> MakeSampleCandles(int count, decimal startPrice = 100m, decimal step = 1.5m)
    {
        var list = new List<CoreCandleData>();
        var baseTime = new DateTime(2024, 1, 1, 0, 0, 0);
        for (int i = 0; i < count; i++)
        {
            var p = startPrice + i * step + (decimal)Math.Sin(i * 0.5) * 2m;
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
    public void ArimaProjectionObject_InitialPlacement_Defaults()
    {
        var obj = new ArimaProjectionObject();

        Assert.Equal(ChartObjectType.ArimaProjection, obj.Type);
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.Color);
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.FillColor);
        Assert.Equal(10, obj.FillOpacity);
        Assert.Equal(20, obj.FutureSteps);
        Assert.Equal(1, obj.P);
        Assert.Equal(1, obj.D);
        Assert.Equal(1, obj.Q);
        Assert.Equal(PriceType.Close, obj.PriceSource);
        Assert.True(obj.ShowConfidenceBand);
        Assert.Equal(2.0m, obj.ConfidenceMultiplier);
        Assert.Empty(obj.ProjectedPath);
        Assert.Empty(obj.UpperBandPath);
        Assert.Empty(obj.LowerBandPath);
    }

    [Fact]
    public void ArimaProjectionObject_Recalculate_CalculatesProjectedPath_And_ConfidenceBands()
    {
        var candles = MakeSampleCandles(40, startPrice: 100m, step: 1.0m);
        var obj = new ArimaProjectionObject
        {
            FutureSteps = 10,
            P = 1,
            D = 1,
            Q = 1,
            ShowConfidenceBand = true,
            ConfidenceMultiplier = 2.0m
        };

        // Select candles from index 5 to 30
        obj.Points.Add(new ChartPoint(candles[5].Timestamp, candles[5].Close));
        obj.Points.Add(new ChartPoint(candles[30].Timestamp, candles[30].Close));

        obj.Recalculate(candles, TimeSpan.FromHours(1));

        Assert.NotEmpty(obj.ProjectedPath);
        Assert.Equal(11, obj.ProjectedPath.Count); // initial connection point + 10 steps
        Assert.Equal(11, obj.UpperBandPath.Count);
        Assert.Equal(11, obj.LowerBandPath.Count);

        // Verify that upper band > center > lower band for future projected steps
        for (int i = 1; i < obj.ProjectedPath.Count; i++)
        {
            Assert.True(obj.UpperBandPath[i].Y >= obj.ProjectedPath[i].Y, $"Upper band ({obj.UpperBandPath[i].Y}) should >= center ({obj.ProjectedPath[i].Y}) at step {i}");
            Assert.True(obj.LowerBandPath[i].Y <= obj.ProjectedPath[i].Y, $"Lower band ({obj.LowerBandPath[i].Y}) should <= center ({obj.ProjectedPath[i].Y}) at step {i}");
        }

        // Verify that confidence cone width is positive
        double marginLast = obj.UpperBandPath[^1].Y - obj.ProjectedPath[^1].Y;
        Assert.True(marginLast >= 0.0);
    }

    [Fact]
    public void ArimaProjectionObject_Recalculate_WithEmptyCandles_ClearsPath()
    {
        var candles = MakeSampleCandles(40);
        var obj = new ArimaProjectionObject { FutureSteps = 10 };
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[30].Timestamp, candles[30].Close));
        obj.Recalculate(candles, TimeSpan.FromHours(1));
        Assert.NotEmpty(obj.ProjectedPath);

        obj.Recalculate(new List<CoreCandleData>());
        Assert.Empty(obj.ProjectedPath);
        Assert.Empty(obj.UpperBandPath);
        Assert.Empty(obj.LowerBandPath);
    }

    [Fact]
    public void ArimaProjectionObject_HitTest_ReturnsTrueBetweenHandles()
    {
        var transform = MakeTransform();
        var obj = new ArimaProjectionObject();
        obj.Points.Add(new ChartPoint(new DateTime(2024, 1, 1, 4, 0, 0), 20m));
        obj.Points.Add(new ChartPoint(new DateTime(2024, 1, 1, 20, 0, 0), 80m));

        var p0 = transform.ChartToScreen(obj.Points[0]);
        var p1 = transform.ChartToScreen(obj.Points[1]);
        double midX = (p0.X + p1.X) / 2.0;

        Assert.True(obj.HitTest(new global::Avalonia.Point(midX, 300), transform));
        Assert.False(obj.HitTest(new global::Avalonia.Point(p0.X - 50, 300), transform));
        Assert.False(obj.HitTest(new global::Avalonia.Point(p1.X + 50, 300), transform));
    }

    [Fact]
    public void ArimaProjectionObject_Translate_ShiftsPointsAndPaths()
    {
        var candles = MakeSampleCandles(25);
        var obj = new ArimaProjectionObject { FutureSteps = 5 };
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[20].Timestamp, candles[20].Close));
        obj.Recalculate(candles, TimeSpan.FromHours(1));

        var origPoint0 = obj.Points[0];
        var origProjectedFirst = obj.ProjectedPath[0];

        var timeDelta = TimeSpan.FromHours(2);
        decimal priceDelta = 10m;

        obj.Translate(timeDelta, priceDelta);

        Assert.Equal(origPoint0.Time + timeDelta, obj.Points[0].Time);
        Assert.Equal(origPoint0.Price + priceDelta, obj.Points[0].Price);
        Assert.Equal(origProjectedFirst.X + timeDelta.Ticks, obj.ProjectedPath[0].X);
        Assert.Equal(origProjectedFirst.Y + (double)priceDelta, obj.ProjectedPath[0].Y, 2);
    }

    [Fact]
    public void ArimaProjectionObject_GetCalculatedValues_ReturnsFiveExpectedItems()
    {
        var candles = MakeSampleCandles(30);
        var obj = new ArimaProjectionObject { FutureSteps = 10, P = 1, D = 1, Q = 1 };
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[25].Timestamp, candles[25].Close));
        obj.Recalculate(candles, TimeSpan.FromHours(1));

        var values = obj.GetCalculatedValues(DateTime.UtcNow);
        Assert.Equal(5, values.Count);
        Assert.Equal("ARIMA Order", values[0].Key);
        Assert.Equal("(1, 1, 1)", values[0].FormattedText);
        Assert.Equal("ARIMA Innovation Variance", values[1].Key);
        Assert.Equal("ARIMA Residual StdDev", values[2].Key);
        Assert.Equal("ARIMA Forecast Horizon", values[3].Key);
        Assert.Equal("10 bars", values[3].FormattedText);
        Assert.Equal("ARIMA Target Price", values[4].Key);
    }

    [Fact]
    public void ArimaProjectionSettingsPanelDefinition_CanHandle()
    {
        var def = new ArimaProjectionSettingsPanelDefinition();
        Assert.True(def.CanHandle(new ArimaProjectionObject()));
        Assert.False(def.CanHandle(new KalmanFilterProjectionObject()));
    }
}
