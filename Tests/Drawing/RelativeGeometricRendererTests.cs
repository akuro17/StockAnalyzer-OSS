using System;
using System.Collections.Generic;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

public class RelativeGeometricRendererTests
{
    private class TestRenderer : RelativeGeometricRenderer
    {
        public override ChartObjectType Type => (ChartObjectType)999; 
        protected override void DrawGeometry(SKCanvas canvas, ICoordinateTransform transform) { }
        public override bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = 5) => false;
        
        public ChartPoint TestCalculateTrueAnglePoint(ChartPoint start, double angleDegrees, double distanceInBars, double pricePerBar, TimeSpan timeframeInterval)
        {
            return CalculateTrueAnglePoint(start, angleDegrees, distanceInBars, pricePerBar, timeframeInterval);
        }

        public ChartPoint TestSnapToLogicalPoint(global::Avalonia.Point screenPoint, ICoordinateTransform transform, IReadOnlyList<CoreCandleData> candles, StockAnalyzer.Avalonia.Services.Drawing.IMagnetSnapService magnetSnapService)
        {
            return SnapToLogicalPoint(screenPoint, transform, candles, magnetSnapService);
        }
    }

    [Fact]
    public void CalculateTrueAnglePoint_45Degrees_ReturnsExactRatio()
    {
        using var renderer = new TestRenderer();
        var start = new ChartPoint(new DateTime(2026, 1, 1), 100m);
        double angle = 45.0; // 45 degrees
        double distance = 10.0;
        double pricePerBar = 2.0;
        TimeSpan interval = TimeSpan.FromDays(1);

        var result = renderer.TestCalculateTrueAnglePoint(start, angle, distance, pricePerBar, interval);

        double expectedDx = 10.0 * Math.Cos(Math.PI / 4.0);
        long expectedTicks = (long)(expectedDx * interval.Ticks);
        decimal expectedDy = (decimal)(10.0 * Math.Sin(Math.PI / 4.0) * pricePerBar);

        Assert.Equal(start.Time.AddTicks(expectedTicks), result.Time);
        Assert.Equal(start.Price + expectedDy, result.Price);
    }

    [Fact]
    public void SnapToLogicalPoint_IndexMode_SnapsToGridCorner()
    {
        using var renderer = new TestRenderer();
        var transform = new GenericCoordinateTransform(ChartAxisMode.Index, 1000, 500);
        transform.SetIndexRange(0, 100);
        transform.SetPriceRange(0m, 100m);
        transform.FixedPricePerIndex = 1.0m;

        var screenPoint = new global::Avalonia.Point(500, 250);
        var rawPoint = transform.ScreenToChart(screenPoint);
        
        var snapped = renderer.TestSnapToLogicalPoint(screenPoint, transform, null!, null!);

        Assert.Equal((long)Math.Round((double)rawPoint.Time.Ticks), snapped.Time.Ticks);
        Assert.Equal(Math.Round(rawPoint.Price / 1.0m) * 1.0m, snapped.Price);
    }
}
