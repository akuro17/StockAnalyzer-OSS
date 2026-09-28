using System;
using System.Collections.Generic;
using Avalonia.Media;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class ClothoidCurveProjectionTests
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
    public void ClothoidCurveProjectionObject_InitialPlacement_Defaults()
    {
        var obj = new ClothoidCurveProjectionObject();

        Assert.Equal(ChartObjectType.ClothoidCurveProjection, obj.Type);
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.Color);
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.FillColor);
        Assert.Equal(10, obj.FillOpacity);
        Assert.Equal(20, obj.FutureSteps);
        Assert.Equal(0.05m, obj.CurvatureRate);
        Assert.True(obj.ShowConfidenceBand);
        Assert.Equal(2.0m, obj.ConfidenceMultiplier);
        Assert.Empty(obj.ProjectedPath);
        Assert.Empty(obj.UpperBandPath);
        Assert.Empty(obj.LowerBandPath);
    }

    [Fact]
    public void ClothoidCurveProjectionObject_Recalculate_CalculatesProjectedPath_And_ConfidenceBands()
    {
        var candles = MakeSampleCandles(10, startPrice: 100m, step: 2m);
        var obj = new ClothoidCurveProjectionObject
        {
            FutureSteps = 15,
            CurvatureRate = 0.05m
        };
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[4].Timestamp, candles[4].Close));

        obj.Recalculate(candles, TimeSpan.FromHours(1));

        Assert.Equal(16, obj.ProjectedPath.Count);
        Assert.Equal(16, obj.UpperBandPath.Count);
        Assert.Equal(16, obj.LowerBandPath.Count);

        for (int i = 1; i <= 15; i++)
        {
            Assert.True(obj.UpperBandPath[i].Y > obj.ProjectedPath[i].Y);
            Assert.True(obj.LowerBandPath[i].Y < obj.ProjectedPath[i].Y);
        }
    }

    [Fact]
    public void ClothoidCurveProjectionObject_Translate_ShiftsPointsAndPaths()
    {
        var candles = MakeSampleCandles(10, startPrice: 100m, step: 2m);
        var obj = new ClothoidCurveProjectionObject { FutureSteps = 5 };
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[3].Timestamp, candles[3].Close));

        obj.Recalculate(candles, TimeSpan.FromHours(1));

        var origPoint0 = obj.Points[0];
        var origProj0 = obj.ProjectedPath[0];

        var timeDelta = TimeSpan.FromHours(2);
        decimal priceDelta = 10m;

        obj.Translate(timeDelta, priceDelta);

        Assert.Equal(origPoint0.Time + timeDelta, obj.Points[0].Time);
        Assert.Equal(origPoint0.Price + priceDelta, obj.Points[0].Price);
        Assert.Equal(origProj0.X + timeDelta.Ticks, obj.ProjectedPath[0].X);
        Assert.Equal(origProj0.Y + (double)priceDelta, obj.ProjectedPath[0].Y, 4);
    }

    [Fact]
    public void ClothoidCurveProjectionObject_ExtremeDownwardTrend_LowerBandRemainsNonNegative()
    {
        var candles = MakeSampleCandles(10, startPrice: 10m, step: -0.8m);
        var obj = new ClothoidCurveProjectionObject
        {
            FutureSteps = 20,
            ConfidenceMultiplier = 5.0m,
            CurvatureRate = -0.1m
        };
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[^1].Timestamp, candles[^1].Close));

        obj.Recalculate(candles, TimeSpan.FromHours(1));

        Assert.NotEmpty(obj.LowerBandPath);
        foreach (var pt in obj.LowerBandPath)
        {
            Assert.True(pt.Y >= 0.0001, $"Lower band price {pt.Y} must be non-negative.");
        }
    }

    [Fact]
    public void ClothoidCurveProjectionObject_AutoPolarity_InvertsCurvatureOnDowntrend()
    {
        // 10 candles in clear downtrend from 100 to 82
        var candles = MakeSampleCandles(10, startPrice: 100m, step: -2m);

        var objAuto = new ClothoidCurveProjectionObject
        {
            AutoPolarity = true,
            CurvatureRate = 0.05m, // Positive setting by default
            FutureSteps = 10
        };
        objAuto.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        objAuto.Points.Add(new ChartPoint(candles[^1].Timestamp, candles[^1].Close));
        objAuto.Recalculate(candles, TimeSpan.FromHours(1));

        var objManual = new ClothoidCurveProjectionObject
        {
            AutoPolarity = false,
            CurvatureRate = 0.05m,
            FutureSteps = 10
        };
        objManual.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        objManual.Points.Add(new ChartPoint(candles[^1].Timestamp, candles[^1].Close));
        objManual.Recalculate(candles, TimeSpan.FromHours(1));

        // AutoPolarity must invert curvature to curve downwards in a downtrend,
        // so its terminal projected price is lower than the manual positive curvature one.
        Assert.True(objAuto.ProjectedPath[^1].Y < objManual.ProjectedPath[^1].Y);
        Assert.True(objAuto.ProjectedPath[^1].Y < (double)candles[^1].Close);
    }
}
