using System;
using System.Collections.Generic;
using Avalonia.Media;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class SsaProjectionTests
{
    private static LinearCoordinateTransform MakeTransform()
        => new LinearCoordinateTransform(
            new DateTime(2024, 1, 1), new DateTime(2024, 1, 2),
            0m, 100m, 800, 600);

    private static List<CoreCandleData> MakeSampleCandles(int count, decimal startPrice = 100m, decimal step = 1m)
    {
        var list = new List<CoreCandleData>();
        var baseTime = new DateTime(2024, 1, 1, 0, 0, 0);
        for (int i = 0; i < count; i++)
        {
            var p = startPrice + i * step + (decimal)(Math.Sin(i * 0.5) * 3.0);
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
    public void SsaProjectionObject_InitialPlacement_Defaults()
    {
        var obj = new SsaProjectionObject();

        Assert.Equal(ChartObjectType.SsaProjection, obj.Type);
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.Color);
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.FillColor);
        Assert.Equal(10, obj.FillOpacity);
        Assert.Equal(20, obj.FutureSteps);
        Assert.Equal(10, obj.EmbeddingDimension);
        Assert.Equal(2, obj.NumComponents);
        Assert.Equal(SsaDetrendMode.LeastSquaresLinear, obj.DetrendMethod);
        Assert.True(obj.ApplyDetrend);
        Assert.True(obj.ShowReconstructedPath);
        Assert.True(obj.ShowConfidenceBand);
        Assert.Equal(2.0m, obj.ConfidenceMultiplier);
        Assert.Empty(obj.ProjectedPath);
        Assert.Empty(obj.UpperBandPath);
        Assert.Empty(obj.LowerBandPath);
        Assert.Empty(obj.ReconstructedPath);
    }

    [Fact]
    public void SsaProjectionObject_Recalculate_CalculatesProjectedPath_And_ConfidenceBands_And_Reconstruction()
    {
        var candles = MakeSampleCandles(30, startPrice: 100m, step: 2m);
        var obj = new SsaProjectionObject
        {
            FutureSteps = 10,
            EmbeddingDimension = 8,
            NumComponents = 2,
            DetrendMethod = SsaDetrendMode.LeastSquaresLinear,
            ShowReconstructedPath = true,
            ShowConfidenceBand = true,
            ConfidenceMultiplier = 2.0m
        };

        // Select candles from index 0 to 19
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[19].Timestamp, candles[19].Close));

        obj.Recalculate(candles, TimeSpan.FromHours(1));

        Assert.NotEmpty(obj.ProjectedPath);
        Assert.Equal(11, obj.ProjectedPath.Count); // initial connection point + 10 steps
        Assert.Equal(11, obj.UpperBandPath.Count);
        Assert.Equal(11, obj.LowerBandPath.Count);
        Assert.Equal(20, obj.ReconstructedPath.Count); // 20 in-sample candles
        Assert.NotEmpty(obj.Components);
        Assert.True(obj.CumulativeVarianceRatio > 0.5);
        Assert.True(obj.IsStable);

        // Verify that the projected path trends upward with positive velocity
        var first = obj.ProjectedPath[0];
        var last = obj.ProjectedPath[^1];
        Assert.True(last.Y > first.Y, $"Expected upward projection: last ({last.Y}) > first ({first.Y})");

        // Verify that upper band > center > lower band for future projected steps
        for (int i = 1; i < obj.ProjectedPath.Count; i++)
        {
            Assert.True(obj.UpperBandPath[i].Y >= obj.ProjectedPath[i].Y, $"Upper band should exceed center at step {i}");
            Assert.True(obj.LowerBandPath[i].Y <= obj.ProjectedPath[i].Y, $"Lower band should be below center at step {i}");
        }
    }

    [Fact]
    public void SsaProjectionObject_Recalculate_WithEmptyCandles_ClearsPath()
    {
        var obj = new SsaProjectionObject();
        obj.Points.Add(new ChartPoint(DateTime.Now, 100m));
        obj.Points.Add(new ChartPoint(DateTime.Now.AddDays(1), 110m));

        obj.Recalculate(null);
        Assert.Empty(obj.ProjectedPath);
        Assert.Empty(obj.UpperBandPath);
        Assert.Empty(obj.LowerBandPath);
        Assert.Empty(obj.ReconstructedPath);

        obj.Recalculate(Array.Empty<CoreCandleData>());
        Assert.Empty(obj.ProjectedPath);
    }

    [Fact]
    public void SsaProjectionObject_Translate_ShiftsPointsAndPath()
    {
        var candles = MakeSampleCandles(20, startPrice: 100m, step: 1m);
        var obj = new SsaProjectionObject { FutureSteps = 5, EmbeddingDimension = 6 };
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[10].Timestamp, candles[10].Close));
        obj.Recalculate(candles, TimeSpan.FromHours(1));

        var origP0 = obj.Points[0];
        var origProjectedFirst = obj.ProjectedPath[0];
        var origReconFirst = obj.ReconstructedPath[0];

        var timeShift = TimeSpan.FromHours(3);
        decimal priceShift = 15m;

        obj.Translate(timeShift, priceShift);

        Assert.Equal(origP0.Time + timeShift, obj.Points[0].Time);
        Assert.Equal(origP0.Price + priceShift, obj.Points[0].Price);
        Assert.Equal(origProjectedFirst.X + timeShift.Ticks, obj.ProjectedPath[0].X);
        Assert.Equal(origProjectedFirst.Y + (double)priceShift, obj.ProjectedPath[0].Y);
        Assert.Equal(origReconFirst.X + timeShift.Ticks, obj.ReconstructedPath[0].X);
        Assert.Equal(origReconFirst.Y + (double)priceShift, obj.ReconstructedPath[0].Y);
    }

    [Fact]
    public void SsaProjectionObject_HitTest_WorksCorrectly()
    {
        var transform = MakeTransform();
        var obj = new SsaProjectionObject();
        obj.Points.Add(new ChartPoint(new DateTime(2024, 1, 1, 6, 0, 0), 20m));
        obj.Points.Add(new ChartPoint(new DateTime(2024, 1, 1, 18, 0, 0), 80m));

        var pMid = transform.ChartToScreen(new ChartPoint(new DateTime(2024, 1, 1, 12, 0, 0), 50m));
        Assert.True(obj.HitTest(pMid, transform));

        var pOutside = transform.ChartToScreen(new ChartPoint(new DateTime(2024, 1, 1, 23, 0, 0), 50m));
        Assert.False(obj.HitTest(pOutside, transform));
    }

    [Fact]
    public void SsaProjectionObject_GetCalculatedValues_ReturnsDiagnosticsAndMetrics()
    {
        var candles = MakeSampleCandles(25, startPrice: 100m, step: 1m);
        var obj = new SsaProjectionObject { FutureSteps = 5, EmbeddingDimension = 6, NumComponents = 2 };
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[15].Timestamp, candles[15].Close));
        obj.Recalculate(candles, TimeSpan.FromHours(1));

        var vals = obj.GetCalculatedValues(DateTime.Now);
        Assert.NotEmpty(vals);
        Assert.Contains(vals, v => v.Label.Contains("SSA Comp #1 Var"));
        Assert.Contains(vals, v => v.Label.Contains("SSA Cumulative Var"));
        Assert.Contains(vals, v => v.Label.Contains("SSA Stability (ν²)"));
        Assert.Contains(vals, v => v.Label.Contains("SSA Horizon (H/N)"));
        Assert.Contains(vals, v => v.Label.Contains("SSA Residual StdDev"));
        Assert.Contains(vals, v => v.Label.Contains("SSA Target Price"));
    }
}
