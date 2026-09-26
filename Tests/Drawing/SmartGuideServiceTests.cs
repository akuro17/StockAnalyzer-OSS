using System;
using System.Collections.Generic;
using Avalonia;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Core.Models;
using Xunit;
using Point = global::Avalonia.Point;
using Rect = global::Avalonia.Rect;

namespace StockAnalyzer.Tests.Drawing;

public class SmartGuideServiceTests
{
    private class TestCoordinateTransform : ICoordinateTransform
    {
        public double CanvasWidth => 1000;
        public double CanvasHeight => 800;
        public Rect ScreenRect => new Rect(0, 0, 1000, 800);
        public double ViewportX => 0;
        public double ViewportWidth => 1000;
        public double ScaleX => 1.0;
        public PriceScaleType PriceScale => PriceScaleType.Linear;
        public TransformMetadata Metadata => new TransformMetadata(false, true, ChartType.Line);
        public IReadOnlyList<DateTime>? TimeMap => null;

        public Point ChartToScreen(ChartPoint chartPoint)
        {
            // 1 Day = 10px, Base = 2025-01-01
            double x = (chartPoint.Time - new DateTime(2025, 1, 1)).TotalDays * 10.0;
            // Price 1.0 = 1px, Inverted (Screen 0 = Price 800)
            double y = 800.0 - (double)chartPoint.Price;
            return new Point(x, y);
        }

        public ChartPoint ScreenToChart(Point screenPoint)
        {
            var time = new DateTime(2025, 1, 1).AddDays(screenPoint.X / 10.0);
            var price = (decimal)(800.0 - screenPoint.Y);
            return new ChartPoint(time, price);
        }

        public Point NumericToScreen(double x, double y) => new Point(x, y);
        public (double x, double y) ScreenToNumeric(Point screenPoint) => (screenPoint.X, screenPoint.Y);
        public void UpdateRange(DateTime minTime, DateTime maxTime, decimal minPrice, decimal maxPrice, double? newCanvasWidth = null, double? newCanvasHeight = null) { }
        public void SetTimeMap(IReadOnlyList<DateTime> timeMap) { }
        public double GetXFromIndex(double index) => index;
        public double GetYFromPrice(decimal price) => 800.0 - (double)price;
    }

    private readonly TestCoordinateTransform _transform = new();
    private readonly SmartGuideService _service = new();
    private readonly Rect _chartArea = new Rect(0, 0, 1000, 800);
    private const double SnapThreshold = 5.0;

    [Fact]
    public void SnapObjectMove_EmptyOrSingleObject_ReturnsNoSnap()
    {
        var dragged = new RectangleObject(
            new ChartPoint(new DateTime(2025, 1, 10), 100),
            new ChartPoint(new DateTime(2025, 1, 20), 200));

        var allObjects = new List<IChartObject> { dragged };
        var guideLines = new List<SmartGuideLine>();
        var proposedBounds = new Rect(100, 100, 100, 100);

        var result = _service.SnapObjectMove(dragged, proposedBounds, allObjects, _transform, _chartArea, SnapThreshold, guideLines);

        Assert.False(result.IsSnapped);
        Assert.False(result.IsSnappedX);
        Assert.False(result.IsSnappedY);
        Assert.Equal(0, result.CorrectionX);
        Assert.Equal(0, result.CorrectionY);
        Assert.Empty(guideLines);
    }

    [Fact]
    public void SnapObjectMove_ExcludesSelfAndInvisibleAndLocked()
    {
        var dragged = new RectangleObject(
            new ChartPoint(new DateTime(2025, 1, 10), 100),
            new ChartPoint(new DateTime(2025, 1, 20), 200));

        var hiddenObj = new RectangleObject(
            new ChartPoint(new DateTime(2025, 1, 10), 100),
            new ChartPoint(new DateTime(2025, 1, 20), 200))
        {
            IsVisible = false
        };

        var lockedObj = new RectangleObject(
            new ChartPoint(new DateTime(2025, 1, 10), 100),
            new ChartPoint(new DateTime(2025, 1, 20), 200))
        {
            IsLocked = true
        };

        var allObjects = new List<IChartObject> { dragged, hiddenObj, lockedObj };
        var guideLines = new List<SmartGuideLine>();
        // Target would be at (90, 600) to (190, 700)
        var proposedBounds = new Rect(92, 602, 100, 100);

        var result = _service.SnapObjectMove(dragged, proposedBounds, allObjects, _transform, _chartArea, SnapThreshold, guideLines);

        Assert.False(result.IsSnapped);
        Assert.Empty(guideLines);
    }

    [Fact]
    public void SnapObjectMove_SnapsToTargetLeftEdge_WithinThreshold()
    {
        // Target Rect: Time [Jan 11, Jan 21] -> Screen X [100, 200], Price [500, 600] -> Screen Y [200, 300]
        var target = new RectangleObject(
            new ChartPoint(new DateTime(2025, 1, 11), 600),
            new ChartPoint(new DateTime(2025, 1, 21), 500));

        // Dragged Rect: Proposed Screen X: [103, 153] (Left is 103, distance to target Left 100 is 3px <= 5px)
        // Proposed Screen Y: [400, 450] (Far from Y targets)
        var dragged = new RectangleObject(
            new ChartPoint(new DateTime(2025, 1, 1), 100),
            new ChartPoint(new DateTime(2025, 1, 5), 200));

        var allObjects = new List<IChartObject> { target, dragged };
        var guideLines = new List<SmartGuideLine>();
        var proposedBounds = new Rect(103, 400, 50, 50);

        var result = _service.SnapObjectMove(dragged, proposedBounds, allObjects, _transform, _chartArea, SnapThreshold, guideLines);

        Assert.True(result.IsSnappedX);
        Assert.False(result.IsSnappedY);
        Assert.Equal(-3.0, result.CorrectionX, precision: 3);
        Assert.Equal(0.0, result.CorrectionY, precision: 3);

        // Verify Guide line
        Assert.Single(guideLines);
        var guide = guideLines[0];
        Assert.Equal(SmartGuideAxis.Vertical, guide.Axis);
        Assert.Equal(100f, guide.Position, precision: 1);
        Assert.Equal((float)_chartArea.Top, guide.SpanStart);
        Assert.Equal((float)_chartArea.Bottom, guide.SpanEnd);
        Assert.Equal(target.Id, guide.TargetObjectId);
    }

    [Fact]
    public void SnapObjectMove_SnapsBothXAndY_Simultaneously()
    {
        // Target Rect: X [100, 200], Y [200, 300]
        var target = new RectangleObject(
            new ChartPoint(new DateTime(2025, 1, 11), 600),
            new ChartPoint(new DateTime(2025, 1, 21), 500));

        var dragged = new RectangleObject(
            new ChartPoint(new DateTime(2025, 1, 1), 100),
            new ChartPoint(new DateTime(2025, 1, 5), 200));

        var allObjects = new List<IChartObject> { target, dragged };
        var guideLines = new List<SmartGuideLine>();
        // Proposed bounds: Left=198 (2px to Target Right 200), Top=197 (3px to Target Top 200)
        var proposedBounds = new Rect(198, 197, 80, 80);

        var result = _service.SnapObjectMove(dragged, proposedBounds, allObjects, _transform, _chartArea, SnapThreshold, guideLines);

        Assert.True(result.IsSnappedX);
        Assert.True(result.IsSnappedY);
        Assert.Equal(2.0, result.CorrectionX, precision: 3);
        Assert.Equal(3.0, result.CorrectionY, precision: 3);

        Assert.Equal(2, guideLines.Count);
        Assert.Contains(guideLines, g => g.Axis == SmartGuideAxis.Vertical && Math.Abs(g.Position - 200f) < 0.1f);
        Assert.Contains(guideLines, g => g.Axis == SmartGuideAxis.Horizontal && Math.Abs(g.Position - 200f) < 0.1f);
    }

    [Fact]
    public void SnapObjectMove_TieBreaking_PicksClosestTarget()
    {
        // Target 1: Left at 100
        var target1 = new RectangleObject(
            new ChartPoint(new DateTime(2025, 1, 11), 600),
            new ChartPoint(new DateTime(2025, 1, 15), 500));

        // Target 2: Left at 103
        var target2 = new RectangleObject(
            new ChartPoint(new DateTime(2025, 1, 11).AddDays(0.3), 600),
            new ChartPoint(new DateTime(2025, 1, 15).AddDays(0.3), 500));

        var dragged = new RectangleObject(
            new ChartPoint(new DateTime(2025, 1, 1), 100),
            new ChartPoint(new DateTime(2025, 1, 5), 200));

        var allObjects = new List<IChartObject> { target1, target2, dragged };
        var guideLines = new List<SmartGuideLine>();
        // Proposed Left is 102.5 -> Distance to Target 2 (103) is 0.5px, Distance to Target 1 (100) is 2.5px
        var proposedBounds = new Rect(102.5, 400, 50, 50);

        var result = _service.SnapObjectMove(dragged, proposedBounds, allObjects, _transform, _chartArea, SnapThreshold, guideLines);

        Assert.True(result.IsSnappedX);
        Assert.Equal(0.5, result.CorrectionX, precision: 3);
        Assert.Single(guideLines);
        Assert.Equal(target2.Id, guideLines[0].TargetObjectId);
    }

    [Fact]
    public void SnapObjectMove_HorizontalLine_OnlySnapsY()
    {
        // Horizontal line at Price 500 -> Screen Y = 300
        var hLine = new HorizontalLineObject(new ChartPoint(new DateTime(2025, 1, 1), 500));

        var dragged = new RectangleObject(
            new ChartPoint(new DateTime(2025, 1, 1), 100),
            new ChartPoint(new DateTime(2025, 1, 5), 200));

        var allObjects = new List<IChartObject> { hLine, dragged };
        var guideLines = new List<SmartGuideLine>();
        // Proposed: X=100, Y=302 (Distance to Y=300 is 2px)
        var proposedBounds = new Rect(100, 302, 50, 50);

        var result = _service.SnapObjectMove(dragged, proposedBounds, allObjects, _transform, _chartArea, SnapThreshold, guideLines);

        Assert.False(result.IsSnappedX);
        Assert.True(result.IsSnappedY);
        Assert.Equal(-2.0, result.CorrectionY, precision: 3);
        Assert.Single(guideLines);
        Assert.Equal(SmartGuideAxis.Horizontal, guideLines[0].Axis);
        Assert.Equal(300f, guideLines[0].Position, precision: 1);
    }

    [Fact]
    public void SnapHandleMove_SnapsSinglePointToTargetCenter()
    {
        // Target Rect: X [100, 200] (Center = 150), Y [200, 300] (Center = 250)
        var target = new RectangleObject(
            new ChartPoint(new DateTime(2025, 1, 11), 600),
            new ChartPoint(new DateTime(2025, 1, 21), 500));

        var dragged = new RectangleObject(
            new ChartPoint(new DateTime(2025, 1, 1), 100),
            new ChartPoint(new DateTime(2025, 1, 5), 200));

        var allObjects = new List<IChartObject> { target, dragged };
        var guideLines = new List<SmartGuideLine>();
        // Candidate handle screen: (152, 248) -> 2px to CenterX 150, 2px to CenterY 250
        var candidatePoint = new Point(152, 248);

        var result = _service.SnapHandleMove(dragged, 0, candidatePoint, allObjects, _transform, _chartArea, SnapThreshold, guideLines);

        Assert.True(result.IsSnappedX);
        Assert.True(result.IsSnappedY);
        Assert.Equal(-2.0, result.CorrectionX, precision: 3);
        Assert.Equal(2.0, result.CorrectionY, precision: 3);
        Assert.Equal(150.0, result.SnappedScreenPoint.X, precision: 3);
        Assert.Equal(250.0, result.SnappedScreenPoint.Y, precision: 3);

        Assert.Equal(2, guideLines.Count);
    }
}
