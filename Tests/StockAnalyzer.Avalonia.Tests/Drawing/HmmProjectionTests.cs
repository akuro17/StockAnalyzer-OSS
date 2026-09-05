using System;
using System.Collections.Generic;
using Avalonia.Media;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class HmmProjectionTests
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
    public void HmmProjectionObject_InitialPlacement_Defaults()
    {
        var obj = new HmmProjectionObject();

        Assert.Equal(ChartObjectType.HmmProjection, obj.Type);
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.Color);
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.FillColor);
        Assert.Equal(10, obj.FillOpacity);
        Assert.Equal(20, obj.FutureSteps);
        Assert.Equal(2, obj.States);
        Assert.Equal(30, obj.MaxIterations);
        Assert.Equal(1e-4, obj.Tolerance);
        Assert.Equal(PriceType.Median, obj.PriceSource);
        Assert.True(obj.ShowConfidenceBand);
        Assert.Equal(2.0m, obj.ConfidenceMultiplier);
        Assert.Empty(obj.ProjectedPath);
        Assert.Empty(obj.UpperBandPath);
        Assert.Empty(obj.LowerBandPath);
    }

    [Fact]
    public void HmmProjectionObject_Recalculate_CalculatesProjectedPath_And_ConfidenceBands()
    {
        var candles = MakeSampleCandles(20, startPrice: 100m, step: 2m);
        var obj = new HmmProjectionObject
        {
            FutureSteps = 10,
            States = 2,
            ShowConfidenceBand = true,
            ConfidenceMultiplier = 2.0m
        };

        // Select candles from index 0 to 14 (15 candles >= MinSampleCount 10)
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[14].Timestamp, candles[14].Close));

        obj.Recalculate(candles, TimeSpan.FromHours(1));

        Assert.NotEmpty(obj.ProjectedPath);
        Assert.Equal(11, obj.ProjectedPath.Count); // initial connection point + 10 steps
        Assert.Equal(11, obj.UpperBandPath.Count);
        Assert.Equal(11, obj.LowerBandPath.Count);

        // Verify that the projected path trends upward
        var first = obj.ProjectedPath[0];
        var last = obj.ProjectedPath[^1];
        Assert.True(last.Y > first.Y, $"Expected upward projection: last ({last.Y}) > first ({first.Y})");

        // Verify that upper band >= center >= lower band for future projected steps
        for (int i = 1; i < obj.ProjectedPath.Count; i++)
        {
            Assert.True(obj.UpperBandPath[i].Y >= obj.ProjectedPath[i].Y, $"Upper band should be >= center at step {i}");
            Assert.True(obj.LowerBandPath[i].Y <= obj.ProjectedPath[i].Y, $"Lower band should be <= center at step {i}");
        }
    }

    [Fact]
    public void HmmProjectionObject_Recalculate_WithEmptyCandles_ClearsPath()
    {
        var obj = new HmmProjectionObject();
        obj.Points.Add(new ChartPoint(DateTime.Now, 100m));
        obj.Points.Add(new ChartPoint(DateTime.Now.AddDays(1), 110m));

        obj.Recalculate(new List<CoreCandleData>());

        Assert.Empty(obj.ProjectedPath);
        Assert.Empty(obj.UpperBandPath);
        Assert.Empty(obj.LowerBandPath);
    }

    [Fact]
    public void HmmProjectionObject_Translate_UpdatesPointsAndProjectedPath()
    {
        var candles = MakeSampleCandles(20, startPrice: 50m, step: 2m);
        var obj = new HmmProjectionObject();
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[14].Timestamp, candles[14].Close));
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
    public void HmmProjectionObject_VerticalRange_HitTest_CoversFullHeight()
    {
        var t = MakeTransform();
        var start = new ChartPoint(new DateTime(2024, 1, 1, 4, 0, 0), 20m);
        var end = new ChartPoint(new DateTime(2024, 1, 1, 8, 0, 0), 80m);
        var obj = new HmmProjectionObject();
        obj.Points.Add(start);
        obj.Points.Add(end);

        var pStart = t.ChartToScreen(start);
        var pEnd = t.ChartToScreen(end);
        double midX = (pStart.X + pEnd.X) / 2;

        Assert.True(obj.HitTest(new global::Avalonia.Point(midX, 0), t));
        Assert.True(obj.HitTest(new global::Avalonia.Point(midX, 600), t));
        Assert.True(obj.HitTest(new global::Avalonia.Point(pStart.X, 300), t));
        Assert.True(obj.HitTest(new global::Avalonia.Point(pEnd.X, 300), t));
        Assert.False(obj.HitTest(new global::Avalonia.Point(pStart.X - 100, 300), t));
    }

    [Fact]
    public void HmmProjectionObject_GetCalculatedValues_ReturnsRegimeDiagnostics()
    {
        var candles = MakeSampleCandles(20, startPrice: 100m, step: 2m);
        var obj = new HmmProjectionObject();
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[14].Timestamp, candles[14].Close));
        obj.Recalculate(candles, TimeSpan.FromHours(1));

        var values = obj.GetCalculatedValues(candles[14].Timestamp, candles[14].Close);

        Assert.NotEmpty(values);
        Assert.Contains(values, v => v.Label == "HMM Current Regime");
        Assert.Contains(values, v => v.Label == "HMM Bull Probability");
        Assert.Contains(values, v => v.Label.Contains("Mean Return"));
    }

    [Fact]
    public void HmmProjectionObject_ThreeStates_DataWindow_OutputsNeutralRegime()
    {
        // 30 candles alternating Bull / Neutral / Bear
        var candles = new List<CoreCandleData>();
        var baseTime = new DateTime(2024, 1, 1);
        decimal p = 100m;
        for (int i = 0; i < 30; i++)
        {
            if (i < 10) p *= 1.015m;
            else if (i < 20) p += 0.05m;
            else p *= 0.985m;

            candles.Add(new CoreCandleData(baseTime.AddDays(i), p - 1m, p + 1m, p - 1m, p, 1000));
        }

        var obj = new HmmProjectionObject
        {
            States = 3
        };
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[29].Timestamp, candles[29].Close));
        obj.Recalculate(candles, TimeSpan.FromDays(1));

        var values = obj.GetCalculatedValues(candles[29].Timestamp, candles[29].Close);
        Assert.NotEmpty(values);
        var regimeValue = Assert.Single(values, v => v.Label == "HMM Current Regime");
        Assert.NotNull(regimeValue.FormattedText);
    }
}
