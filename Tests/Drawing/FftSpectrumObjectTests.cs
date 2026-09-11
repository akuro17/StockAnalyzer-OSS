using System;
using System.Collections.Generic;
using Avalonia;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

public class FftSpectrumObjectTests
{
    private static List<CoreCandleData> BuildSineWaveCandles(int count, double period)
    {
        var candles = new List<CoreCandleData>(count);
        var baseDate = new DateTime(2025, 1, 1);
        for (int i = 0; i < count; i++)
        {
            decimal price = (decimal)(100.0 + 10.0 * Math.Sin(2.0 * Math.PI * i / period));
            candles.Add(new CoreCandleData(
                baseDate.AddDays(i),
                price,
                price + 1m,
                price - 1m,
                price,
                1000));
        }
        return candles;
    }

    [Fact]
    public void Constructor_InitializesDefaultStateCorrectly()
    {
        var p1 = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var p2 = new ChartPoint(new DateTime(2025, 1, 20), 120m);
        var obj = new FftSpectrumObject(p1, p2);

        Assert.Equal(ChartObjectType.FftSpectrum, obj.Type);
        Assert.Equal(2, obj.Points.Count);
        Assert.Equal(0, obj.AnchorPointIndex);
        Assert.Equal(DrawingMoveAxisMode.XY, obj.MoveAxisMode);
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.Color);
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.FillColor);
        Assert.Equal(10, obj.FillOpacity);
        Assert.Equal(DrawingThemeContext.DefaultStrokeThickness, obj.Thickness);
        Assert.Empty(obj.SpectrumBins);
        Assert.Equal(0, obj.DominantPeriod);
    }

    [Fact]
    public void HitTest_WithinVerticalXSpan_ReturnsTrueRegardlessOfY()
    {
        var p1 = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var p2 = new ChartPoint(new DateTime(2025, 1, 20), 120m);
        var obj = new FftSpectrumObject(p1, p2);

        var transform = new DummyCoordinateTransform();

        // Inside X range (p1.X=0, p2.X=190, so X=100 is inside), any Y should hit
        Assert.True(obj.HitTest(new global::Avalonia.Point(100, 500), transform));
        Assert.True(obj.HitTest(new global::Avalonia.Point(100, -200), transform));

        // Outside X range
        Assert.False(obj.HitTest(new global::Avalonia.Point(300, 500), transform));
    }

    private sealed class DummyCoordinateTransform : ICoordinateTransform
    {
        public double CanvasWidth => 800;
        public double CanvasHeight => 600;
        public global::Avalonia.Rect ScreenRect => new global::Avalonia.Rect(0, 0, 800, 600);
        public double ViewportX => 0;
        public double ViewportWidth => 800;
        public double ScaleX => 1.0;
        public PriceScaleType PriceScale => PriceScaleType.Linear;
        public TransformMetadata Metadata => new TransformMetadata(false, true, ChartType.Line);
        public IReadOnlyList<DateTime>? TimeMap => null;

        public global::Avalonia.Point ChartToScreen(ChartPoint chartPoint)
        {
            double x = (chartPoint.Time - new DateTime(2025, 1, 1)).TotalDays * 10.0;
            double y = 600.0 - (double)chartPoint.Price;
            return new global::Avalonia.Point(x, y);
        }

        public ChartPoint ScreenToChart(global::Avalonia.Point screenPoint)
        {
            var time = new DateTime(2025, 1, 1).AddDays(screenPoint.X / 10.0);
            var price = (decimal)(600.0 - screenPoint.Y);
            return new ChartPoint(time, price);
        }

        public global::Avalonia.Point NumericToScreen(double x, double y) => new global::Avalonia.Point(x, y);
        public (double x, double y) ScreenToNumeric(global::Avalonia.Point screenPoint) => (screenPoint.X, screenPoint.Y);
        public void UpdateRange(DateTime minTime, DateTime maxTime, decimal minPrice, decimal maxPrice, double? newCanvasWidth = null, double? newCanvasHeight = null) { }
        public void SetTimeMap(IReadOnlyList<DateTime> timeMap) { }
        public double GetXFromIndex(double index) => index;
        public double GetYFromPrice(decimal price) => 600.0 - (double)price;
    }

    [Fact]
    public void Recalculate_WithCandles_ComputesSpectrumAndDominantCycle()
    {
        var candles = BuildSineWaveCandles(100, period: 20.0);
        var obj = new FftSpectrumObject(
            new ChartPoint(new DateTime(2025, 1, 1), 90m),
            new ChartPoint(new DateTime(2025, 1, 1).AddDays(99), 110m));

        obj.Recalculate(candles);

        Assert.NotEmpty(obj.SpectrumBins);
        Assert.Equal(20.0, obj.DominantPeriod, precision: 3);
        Assert.True(obj.DominantPower > 0);

        var calcValues = obj.GetCalculatedValues(DateTime.Now);
        Assert.Equal(3, calcValues.Count);
        Assert.Contains(calcValues, v => v.Label.Contains("Dominant Period"));
    }

    [Fact]
    public void Recalculate_InsufficientCandles_ClearsSpectrum()
    {
        var candles = BuildSineWaveCandles(2, period: 10.0);
        var obj = new FftSpectrumObject(
            new ChartPoint(new DateTime(2025, 1, 1), 90m),
            new ChartPoint(new DateTime(2025, 1, 2), 110m));

        obj.Recalculate(candles);

        Assert.Empty(obj.SpectrumBins);
        Assert.Equal(0, obj.DominantPeriod);
    }

    [Fact]
    public void Translate_ShiftsPointsCorrectly()
    {
        var p1 = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var p2 = new ChartPoint(new DateTime(2025, 1, 10), 120m);
        var obj = new FftSpectrumObject(p1, p2);

        obj.Translate(TimeSpan.FromDays(5), 15m);

        Assert.Equal(new DateTime(2025, 1, 6), obj.Points[0].Time);
        Assert.Equal(115m, obj.Points[0].Price);
        Assert.Equal(new DateTime(2025, 1, 15), obj.Points[1].Time);
        Assert.Equal(135m, obj.Points[1].Price);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var obj = new FftSpectrumObject(
            new ChartPoint(new DateTime(2025, 1, 1), 100m),
            new ChartPoint(new DateTime(2025, 1, 10), 120m));

        var ex = Record.Exception(() => obj.Dispose());
        Assert.Null(ex);
    }
}
