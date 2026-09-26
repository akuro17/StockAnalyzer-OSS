using System;
using System.Collections.Generic;
using Avalonia;
using StockAnalyzer.Avalonia.Drawing;
using Xunit;
using AvPoint = Avalonia.Point;

namespace StockAnalyzer.Tests.Drawing;

public class CyclicLinesObjectTests
{
    private sealed class MockCoordinateTransform : ICoordinateTransform
    {
        public AvPoint ChartToScreen(ChartPoint chartPoint)
        {
            // Simple mapping: 1 day = 100 pixels, price = price
            double x = (chartPoint.Time - new DateTime(2026, 1, 1)).TotalDays * 100.0;
            return new AvPoint(x, (double)chartPoint.Price);
        }

        public ChartPoint ScreenToChart(AvPoint screenPoint)
        {
            DateTime time = new DateTime(2026, 1, 1).AddDays(screenPoint.X / 100.0);
            return new ChartPoint(time, (decimal)screenPoint.Y);
        }

        public AvPoint NumericToScreen(double x, double y) => new AvPoint(x, y);
        public (double x, double y) ScreenToNumeric(AvPoint screenPoint) => (screenPoint.X, screenPoint.Y);
        public void UpdateRange(DateTime minTime, DateTime maxTime, decimal minPrice, decimal maxPrice, double? newCanvasWidth = null, double? newCanvasHeight = null) { }
        public void SetTimeMap(IReadOnlyList<DateTime> timeMap) { }
        public IReadOnlyList<DateTime>? TimeMap => null;
        public double CanvasWidth => 2000;
        public double CanvasHeight => 500;
        public Rect ScreenRect => new Rect(0, 0, CanvasWidth, CanvasHeight);
        public double ViewportX => 0;
        public double ViewportWidth => 2000;
        public double ScaleX => 1.0;
        public double GetXFromIndex(double index) => index;
        public double GetYFromPrice(decimal price) => (double)price;
        public PriceScaleType PriceScale => PriceScaleType.Linear;
        public TransformMetadata Metadata => new TransformMetadata(false, true, StockAnalyzer.Core.Models.ChartType.Line);
    }

    [Fact]
    public void DefaultProperties_AreCorrect()
    {
        var p1 = new ChartPoint(new DateTime(2026, 1, 1), 100m);
        var p2 = new ChartPoint(new DateTime(2026, 1, 2), 100m);
        var obj = new CyclicLinesObject(p1, p2);

        Assert.False(obj.IsCustomRangeEnabled);
        Assert.Equal(CyclicLinesCustomMode.AnchorForwardDays, obj.CustomMode);
        Assert.Equal(CyclicLinesObject.DefaultCustomRangeText, obj.CustomRangeText);

        var multipliers = obj.GetCustomIntervalMultipliers();
        Assert.Equal(CyclicLinesObject.DefaultSquares, multipliers);
        Assert.Contains(1, multipliers);
        Assert.Contains(4, multipliers);
        Assert.Contains(400, multipliers);
    }

    [Fact]
    public void GetCustomIntervalMultipliers_CustomString_ParsesCorrectly()
    {
        var p1 = new ChartPoint(new DateTime(2026, 1, 1), 100m);
        var p2 = new ChartPoint(new DateTime(2026, 1, 2), 100m);
        var obj = new CyclicLinesObject(p1, p2)
        {
            CustomRangeText = " 1, 4 , 9 , 16 , invalid , 25 "
        };

        var multipliers = obj.GetCustomIntervalMultipliers();
        Assert.Equal(new double[] { 1, 4, 9, 16, 25 }, multipliers);
    }

    [Fact]
    public void GetCustomIntervalMultipliers_EmptyString_ReturnsDefault()
    {
        var p1 = new ChartPoint(new DateTime(2026, 1, 1), 100m);
        var p2 = new ChartPoint(new DateTime(2026, 1, 2), 100m);
        var obj = new CyclicLinesObject(p1, p2)
        {
            CustomRangeText = "   "
        };

        var multipliers = obj.GetCustomIntervalMultipliers();
        Assert.Equal(CyclicLinesObject.DefaultSquares, multipliers);
    }

    [Fact]
    public void HitTest_WhenCustomRangeDisabled_UsesStandardEqualIntervals()
    {
        var transform = new MockCoordinateTransform();
        var p1 = new ChartPoint(new DateTime(2026, 1, 1), 100m); // x = 0
        var p2 = new ChartPoint(new DateTime(2026, 1, 2), 100m); // x = 100, interval = 100
        var obj = new CyclicLinesObject(p1, p2)
        {
            IsCustomRangeEnabled = false
        };

        // Standard: 0, 100, 200, 300, 400 ...
        Assert.True(obj.HitTest(new AvPoint(0, 50), transform, 5.0));
        Assert.True(obj.HitTest(new AvPoint(100, 50), transform, 5.0));
        Assert.True(obj.HitTest(new AvPoint(200, 50), transform, 5.0));
        Assert.True(obj.HitTest(new AvPoint(300, 50), transform, 5.0));
        Assert.False(obj.HitTest(new AvPoint(150, 50), transform, 5.0));
    }

    [Fact]
    public void HitTest_WhenCustomRangeEnabled_AnchorForwardDays_ProjectsDaysFromAnchor()
    {
        var transform = new MockCoordinateTransform();
        var p1 = new ChartPoint(new DateTime(2026, 1, 1), 100m); // x = 0 (Anchor point)
        var p2 = new ChartPoint(new DateTime(2026, 1, 10), 100m); // second point is arbitrary in this mode
        var obj = new CyclicLinesObject(p1, p2)
        {
            IsCustomRangeEnabled = true,
            CustomMode = CyclicLinesCustomMode.AnchorForwardDays,
            CustomRangeText = "2, 5"
        };

        // Anchor line (0 days) matches at x = 0
        Assert.True(obj.HitTest(new AvPoint(0, 50), transform, 5.0));
        // 1 day (100) is NOT in custom range (2, 5) -> False
        Assert.False(obj.HitTest(new AvPoint(100, 50), transform, 5.0));
        // 2 days (200) matches
        Assert.True(obj.HitTest(new AvPoint(200, 50), transform, 5.0));
        // 3 days (300) is NOT in custom range -> False
        Assert.False(obj.HitTest(new AvPoint(300, 50), transform, 5.0));
        // 5 days (500) matches
        Assert.True(obj.HitTest(new AvPoint(500, 50), transform, 5.0));
    }

    [Fact]
    public void HitTest_WhenCustomRangeEnabled_TwoPointRange_ProjectsMultiplesOfInterval()
    {
        var transform = new MockCoordinateTransform();
        var p1 = new ChartPoint(new DateTime(2026, 1, 1), 100m); // x = 0
        var p2 = new ChartPoint(new DateTime(2026, 1, 3), 100m); // x = 200, interval = 200
        var obj = new CyclicLinesObject(p1, p2)
        {
            IsCustomRangeEnabled = true,
            CustomMode = CyclicLinesCustomMode.TwoPointRange,
            CustomRangeText = "1, 4"
        };

        // Anchor line matches at x = 0
        Assert.True(obj.HitTest(new AvPoint(0, 50), transform, 5.0));
        // Multiplier 1 -> x = 0 + 1 * 200 = 200
        Assert.True(obj.HitTest(new AvPoint(200, 50), transform, 5.0));
        // Multiplier 4 -> x = 0 + 4 * 200 = 800
        Assert.True(obj.HitTest(new AvPoint(800, 50), transform, 5.0));
        // Multiplier 2 is NOT in custom range -> False
        Assert.False(obj.HitTest(new AvPoint(400, 50), transform, 5.0));
    }

    [Fact]
    public void GetCalculatedValues_WhenCustomRangeEnabled_AnchorForwardDays_ReturnsTargetDays()
    {
        var p1 = new ChartPoint(new DateTime(2026, 1, 1), 100m);
        var p2 = new ChartPoint(new DateTime(2026, 1, 10), 100m);
        var obj = new CyclicLinesObject(p1, p2)
        {
            IsCustomRangeEnabled = true,
            CustomMode = CyclicLinesCustomMode.AnchorForwardDays,
            CustomRangeText = "4, 9"
        };

        var values = obj.GetCalculatedValues(new DateTime(2026, 1, 3));
        Assert.NotEmpty(values);
        Assert.Contains(values, v => v.Key == "OriginDate");
        Assert.Contains(values, v => v.Key == "Elapsed");
        Assert.Contains(values, v => v.Key == "NextTarget" && v.Label.Contains("+4"));
    }

    [Fact]
    public void GetCalculatedValues_WhenCustomRangeEnabled_TwoPointRange_ReturnsTargetMultiples()
    {
        var p1 = new ChartPoint(new DateTime(2026, 1, 1), 100m);
        var p2 = new ChartPoint(new DateTime(2026, 1, 3), 100m);
        var obj = new CyclicLinesObject(p1, p2)
        {
            IsCustomRangeEnabled = true,
            CustomMode = CyclicLinesCustomMode.TwoPointRange,
            CustomRangeText = "2, 4"
        };

        var values = obj.GetCalculatedValues(new DateTime(2026, 1, 2));
        Assert.NotEmpty(values);
        Assert.Contains(values, v => v.Key == "Interval");
        Assert.Contains(values, v => v.Key == "Elapsed");
        Assert.Contains(values, v => v.Key == "NextTarget" && v.Label.Contains("x2"));
    }
}
