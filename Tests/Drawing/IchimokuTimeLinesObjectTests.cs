using System;
using System.Collections.Generic;
using Avalonia;
using StockAnalyzer.Avalonia.Drawing;
using Xunit;
using AvPoint = Avalonia.Point;

namespace StockAnalyzer.Tests.Drawing;

public class IchimokuTimeLinesObjectTests
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
        public double CanvasWidth => 3000;
        public double CanvasHeight => 500;
        public Rect ScreenRect => new Rect(0, 0, CanvasWidth, CanvasHeight);
        public double ViewportX => 0;
        public double ViewportWidth => 3000;
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
        var obj = new IchimokuTimeLinesObject(p1);

        Assert.Single(obj.Points);
        Assert.True(obj.ShowLabels);

        var numbers = obj.GetNumbers();
        Assert.Contains(9, numbers);
        Assert.Contains(17, numbers);
        Assert.Contains(26, numbers);
        Assert.Contains(76, numbers);
        Assert.Contains(226, numbers);
        Assert.Contains(676, numbers);
    }

    [Fact]
    public void AutomaticCalculation_GeneratesNumbersBeyond676()
    {
        var numbers = IchimokuTimeLinesObject.GetNumbersUpTo(2100);

        // Standard canonical numbers up to 676
        Assert.Contains(9, numbers);
        Assert.Contains(26, numbers);
        Assert.Contains(76, numbers);
        Assert.Contains(226, numbers);
        Assert.Contains(676, numbers);

        // Extended numbers beyond 676 using T = A + B - 1 compound formula from webai.txt
        // 676 + 9 - 1 = 684
        Assert.Contains(684, numbers);
        // 676 + 17 - 1 = 692
        Assert.Contains(692, numbers);
        // 676 + 26 - 1 = 701
        Assert.Contains(701, numbers);
        // 676 + 676 - 1 = 1351 (2 Grand Cycles)
        Assert.Contains(1351, numbers);
        // 1351 + 676 - 1 = 2026 (3 Grand Cycles = 676 * 3 - 2)
        Assert.Contains(2026, numbers);
    }

    [Fact]
    public void HitTest_InclusiveCounting_ProjectsForwardDaysFromSingleAnchor()
    {
        var transform = new MockCoordinateTransform();
        var p1 = new ChartPoint(new DateTime(2026, 1, 1), 100m); // x = 0 (Single anchor point)
        var obj = new IchimokuTimeLinesObject(p1);

        // Day 1 (origin, offset = 0) -> x = 0
        Assert.True(obj.HitTest(new AvPoint(0, 50), transform, 5.0));

        // Day 9 (offset = 9 - 1 = 8 days) -> x = 800
        Assert.True(obj.HitTest(new AvPoint(800, 50), transform, 5.0));

        // Day 17 (offset = 17 - 1 = 16 days) -> x = 1600
        Assert.True(obj.HitTest(new AvPoint(1600, 50), transform, 5.0));

        // Day 26 (offset = 26 - 1 = 25 days) -> x = 2500
        Assert.True(obj.HitTest(new AvPoint(2500, 50), transform, 5.0));

        // Day 676 (offset = 676 - 1 = 675 days) -> x = 67500
        Assert.True(obj.HitTest(new AvPoint(67500, 50), transform, 5.0));

        // Beyond 676: Day 684 (offset = 684 - 1 = 683 days) -> x = 68300
        Assert.True(obj.HitTest(new AvPoint(68300, 50), transform, 5.0));

        // Non-matching positions
        Assert.False(obj.HitTest(new AvPoint(100, 50), transform, 5.0));
        Assert.False(obj.HitTest(new AvPoint(900, 50), transform, 5.0));
    }

    [Fact]
    public void GetCalculatedValues_ReturnsExpectedValues()
    {
        var p1 = new ChartPoint(new DateTime(2026, 1, 1), 100m);
        var obj = new IchimokuTimeLinesObject(p1);

        var values = obj.GetCalculatedValues(new DateTime(2026, 1, 5));
        Assert.NotEmpty(values);
        // OriginDate = 2026-01-01, Elapsed = 4.0 days, Next Target (9) at 2026-01-09
        Assert.Contains(values, v => v.Key == "OriginDate");
        Assert.Contains(values, v => v.Key == "Elapsed");
        Assert.Contains(values, v => v.Key == "NextCycle" && v.FormattedText.Contains("2026-01-09"));
    }
}
