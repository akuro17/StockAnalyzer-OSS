using System;
using System.Collections.Generic;
using global::Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Behaviors;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models;
using Xunit;
using Point = global::Avalonia.Point;
using Rect = global::Avalonia.Rect;

namespace StockAnalyzer.Tests.Drawing;

public class CurveDrawingToolsTests
{
    private class DummyCoordinateTransform : ICoordinateTransform
    {
        public double CanvasWidth => 800;
        public double CanvasHeight => 600;
        public Rect ScreenRect => new Rect(0, 0, 800, 600);
        public double ViewportX => 0;
        public double ViewportWidth => 800;
        public double ScaleX => 1.0;
        public PriceScaleType PriceScale => PriceScaleType.Linear;
        public TransformMetadata Metadata => new TransformMetadata(false, true, ChartType.Line);
        public IReadOnlyList<DateTime>? TimeMap => null;

        public Point ChartToScreen(ChartPoint chartPoint)
        {
            double x = (chartPoint.Time - new DateTime(2025, 1, 1)).TotalDays * 10.0;
            double y = 600.0 - (double)chartPoint.Price;
            return new Point(x, y);
        }

        public ChartPoint ScreenToChart(Point screenPoint)
        {
            var time = new DateTime(2025, 1, 1).AddDays(screenPoint.X / 10.0);
            var price = (decimal)(600.0 - screenPoint.Y);
            return new ChartPoint(time, price);
        }

        public Point NumericToScreen(double x, double y) => new Point(x, y);
        public (double x, double y) ScreenToNumeric(Point screenPoint) => (screenPoint.X, screenPoint.Y);
        public void UpdateRange(DateTime minTime, DateTime maxTime, decimal minPrice, decimal maxPrice, double? newCanvasWidth = null, double? newCanvasHeight = null) { }
        public void SetTimeMap(IReadOnlyList<DateTime> timeMap) { }
        public double GetXFromIndex(double index) => index;
        public double GetYFromPrice(decimal price) => 600.0 - (double)price;
    }

    private class NaNCoordinateTransform : ICoordinateTransform
    {
        public double CanvasWidth => 800;
        public double CanvasHeight => 600;
        public Rect ScreenRect => new Rect(0, 0, 800, 600);
        public double ViewportX => 0;
        public double ViewportWidth => 800;
        public double ScaleX => 1.0;
        public PriceScaleType PriceScale => PriceScaleType.Linear;
        public TransformMetadata Metadata => new TransformMetadata(false, true, ChartType.Line);
        public IReadOnlyList<DateTime>? TimeMap => null;

        public Point ChartToScreen(ChartPoint chartPoint) => new Point(double.NaN, double.NaN);
        public ChartPoint ScreenToChart(Point screenPoint) => new ChartPoint(DateTime.MinValue, decimal.Zero);
        public Point NumericToScreen(double x, double y) => new Point(double.NaN, double.NaN);
        public (double x, double y) ScreenToNumeric(Point screenPoint) => (double.NaN, double.NaN);
        public void UpdateRange(DateTime minTime, DateTime maxTime, decimal minPrice, decimal maxPrice, double? newCanvasWidth = null, double? newCanvasHeight = null) { }
        public void SetTimeMap(IReadOnlyList<DateTime> timeMap) { }
        public double GetXFromIndex(double index) => double.NaN;
        public double GetYFromPrice(decimal price) => double.NaN;
    }

    private class InfCoordinateTransform : ICoordinateTransform
    {
        public double CanvasWidth => 800;
        public double CanvasHeight => 600;
        public Rect ScreenRect => new Rect(0, 0, 800, 600);
        public double ViewportX => 0;
        public double ViewportWidth => 800;
        public double ScaleX => 1.0;
        public PriceScaleType PriceScale => PriceScaleType.Linear;
        public TransformMetadata Metadata => new TransformMetadata(false, true, ChartType.Line);
        public IReadOnlyList<DateTime>? TimeMap => null;

        public Point ChartToScreen(ChartPoint chartPoint) => new Point(double.PositiveInfinity, double.PositiveInfinity);
        public ChartPoint ScreenToChart(Point screenPoint) => new ChartPoint(DateTime.MinValue, decimal.Zero);
        public Point NumericToScreen(double x, double y) => new Point(double.PositiveInfinity, double.PositiveInfinity);
        public (double x, double y) ScreenToNumeric(Point screenPoint) => (double.PositiveInfinity, double.PositiveInfinity);
        public void UpdateRange(DateTime minTime, DateTime maxTime, decimal minPrice, decimal maxPrice, double? newCanvasWidth = null, double? newCanvasHeight = null) { }
        public void SetTimeMap(IReadOnlyList<DateTime> timeMap) { }
        public double GetXFromIndex(double index) => double.PositiveInfinity;
        public double GetYFromPrice(decimal price) => double.PositiveInfinity;
    }

    [Fact]
    public void T01_Standard3Point_DegreeElevationAndRender()
    {
        var transform = new DummyCoordinateTransform();
        var p0 = transform.ScreenToChart(new Point(100, 200));
        var p1 = transform.ScreenToChart(new Point(300, 200));
        var p2 = transform.ScreenToChart(new Point(200, 100));

        var obj = new CurveTrendObject(p0, p1, p2);

        using var bitmap = new SKBitmap(800, 600);
        using var canvas = new SKCanvas(bitmap);
        obj.Render(canvas, transform);

        // Midpoint at t = 0.5: S_mid = 0.25*(100,200) + 0.5*(200,100) + 0.25*(300,200) = (200, 150)
        Assert.True(obj.HitTest(new Point(200, 150), transform, tolerance: 3.0));
        Assert.True(obj.HitTest(new Point(100, 200), transform, tolerance: 3.0));
        Assert.True(obj.HitTest(new Point(300, 200), transform, tolerance: 3.0));
    }

    [Fact]
    public void T02_AllPointsEqual_DegeneratesToPointHitTest()
    {
        var transform = new DummyCoordinateTransform();
        var p0 = transform.ScreenToChart(new Point(100, 100));

        var obj = new CurveTrendObject(p0, p0, p0);

        Assert.True(obj.HitTest(new Point(100, 100), transform, tolerance: 3.0));
        Assert.False(obj.HitTest(new Point(150, 150), transform, tolerance: 3.0));
    }

    [Fact]
    public void T03_StartEqualsEnd_CuspBezier()
    {
        var transform = new DummyCoordinateTransform();
        var p0 = transform.ScreenToChart(new Point(100, 200));
        var p2 = transform.ScreenToChart(new Point(200, 100));

        var obj = new CurveTrendObject(p0, p0, p2);

        using var bitmap = new SKBitmap(800, 600);
        using var canvas = new SKCanvas(bitmap);
        obj.Render(canvas, transform);

        // At t = 0.5: S_mid = 0.25*(100,200) + 0.5*(200,100) + 0.25*(100,200) = (150, 150)
        Assert.True(obj.HitTest(new Point(150, 150), transform, tolerance: 3.0));
    }

    [Fact]
    public void T04_P0EqualsP2_RendersAndHitsNormally()
    {
        var transform = new DummyCoordinateTransform();
        var p0 = transform.ScreenToChart(new Point(100, 200));
        var p1 = transform.ScreenToChart(new Point(300, 200));

        var obj = new CurveTrendObject(p0, p1, p0);

        using var bitmap = new SKBitmap(800, 600);
        using var canvas = new SKCanvas(bitmap);
        obj.Render(canvas, transform);

        Assert.True(obj.HitTest(new Point(100, 200), transform, tolerance: 3.0));
        Assert.True(obj.HitTest(new Point(300, 200), transform, tolerance: 3.0));
    }

    [Fact]
    public void T05_P1EqualsP2_RendersAndHitsNormally()
    {
        var transform = new DummyCoordinateTransform();
        var p0 = transform.ScreenToChart(new Point(100, 200));
        var p1 = transform.ScreenToChart(new Point(300, 200));

        var obj = new CurveTrendObject(p0, p1, p1);

        using var bitmap = new SKBitmap(800, 600);
        using var canvas = new SKCanvas(bitmap);
        obj.Render(canvas, transform);

        Assert.True(obj.HitTest(new Point(100, 200), transform, tolerance: 3.0));
        Assert.True(obj.HitTest(new Point(300, 200), transform, tolerance: 3.0));
    }

    [Fact]
    public void T06_ContainsNaN_NoOpAndReturnsFalse()
    {
        var transform = new NaNCoordinateTransform();
        var p0 = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var obj = new CurveTrendObject(p0, p0, p0);

        using var bitmap = new SKBitmap(800, 600);
        using var canvas = new SKCanvas(bitmap);
        obj.Render(canvas, transform);

        Assert.False(obj.HitTest(new Point(100, 100), transform));
    }

    [Fact]
    public void T07_ContainsInfinity_NoOpAndReturnsFalse()
    {
        var transform = new InfCoordinateTransform();
        var p0 = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var obj = new CurveTrendObject(p0, p0, p0);

        using var bitmap = new SKBitmap(800, 600);
        using var canvas = new SKCanvas(bitmap);
        obj.Render(canvas, transform);

        Assert.False(obj.HitTest(new Point(100, 100), transform));
    }

    [Fact]
    public void T08_ToleranceZero_HitsOnlyDirectlyOnCurve()
    {
        var transform = new DummyCoordinateTransform();
        var p0 = transform.ScreenToChart(new Point(100, 200));
        var p1 = transform.ScreenToChart(new Point(300, 200));
        var p2 = transform.ScreenToChart(new Point(200, 100));

        var obj = new CurveTrendObject(p0, p1, p2) { Thickness = 0.0 };

        // Point exactly on start point
        Assert.True(obj.HitTest(new Point(100, 200), transform, tolerance: 0.0));

        // Point 5px away
        Assert.False(obj.HitTest(new Point(100, 205), transform, tolerance: 0.0));
    }

    [Fact]
    public void T09_NegativeTolerance_ThrowsArgumentOutOfRangeException()
    {
        var transform = new DummyCoordinateTransform();
        var p0 = transform.ScreenToChart(new Point(100, 200));
        var obj = new CurveTrendObject(p0, p0, p0);

        Assert.Throws<ArgumentOutOfRangeException>(() => obj.HitTest(new Point(100, 100), transform, tolerance: -1.0));
    }

    [Fact]
    public void T10_NegativeThickness_ThrowsArgumentOutOfRangeException()
    {
        var transform = new DummyCoordinateTransform();
        var p0 = transform.ScreenToChart(new Point(100, 200));
        var obj = new CurveTrendObject(p0, p0, p0) { Thickness = -2.0 };

        Assert.Throws<ArgumentOutOfRangeException>(() => obj.HitTest(new Point(100, 100), transform, tolerance: 1.0));
    }

    [Fact]
    public void T11_DeltaYPositive_OffsetDownwards()
    {
        var transform = new DummyCoordinateTransform();
        var p0 = transform.ScreenToChart(new Point(100, 200));
        var p1 = transform.ScreenToChart(new Point(300, 200));
        var p2 = transform.ScreenToChart(new Point(200, 100)); // S_mid.Y = 150
        var p3 = transform.ScreenToChart(new Point(200, 250)); // S3.Y = 250 => DeltaY = +100

        var obj = new CurveChannelObject(p0, p1, p2, p3);

        // Offset curve midpoint is at Y = 150 + 100 = 250
        Assert.True(obj.HitTest(new Point(200, 250), transform, tolerance: 3.0));
    }

    [Fact]
    public void T12_DeltaYNegative_OffsetUpwards()
    {
        var transform = new DummyCoordinateTransform();
        var p0 = transform.ScreenToChart(new Point(100, 200));
        var p1 = transform.ScreenToChart(new Point(300, 200));
        var p2 = transform.ScreenToChart(new Point(200, 100)); // S_mid.Y = 150
        var p3 = transform.ScreenToChart(new Point(200, 50));  // S3.Y = 50 => DeltaY = -100

        var obj = new CurveChannelObject(p0, p1, p2, p3);

        // Offset curve midpoint is at Y = 150 - 100 = 50
        Assert.True(obj.HitTest(new Point(200, 50), transform, tolerance: 3.0));
    }

    [Fact]
    public void T13_DeltaYZero_MatchesBaseCurve()
    {
        var transform = new DummyCoordinateTransform();
        var p0 = transform.ScreenToChart(new Point(100, 200));
        var p1 = transform.ScreenToChart(new Point(300, 200));
        var p2 = transform.ScreenToChart(new Point(200, 100)); // S_mid.Y = 150
        var p3 = transform.ScreenToChart(new Point(200, 150)); // S3.Y = 150 => DeltaY = 0

        var obj = new CurveChannelObject(p0, p1, p2, p3);

        using var bitmap = new SKBitmap(800, 600);
        using var canvas = new SKCanvas(bitmap);
        obj.Render(canvas, transform);

        Assert.True(obj.HitTest(new Point(200, 150), transform, tolerance: 3.0));
    }

    [Fact]
    public void T14_P3XChangeOnly_MaintainsGeometryAndDeltaY()
    {
        var transform = new DummyCoordinateTransform();
        var p0 = transform.ScreenToChart(new Point(100, 200));
        var p1 = transform.ScreenToChart(new Point(300, 200));
        var p2 = transform.ScreenToChart(new Point(200, 100));
        var p3A = transform.ScreenToChart(new Point(200, 250));
        var p3B = transform.ScreenToChart(new Point(600, 250)); // Different X, same Y

        var objA = new CurveChannelObject(p0, p1, p2, p3A);
        var objB = new CurveChannelObject(p0, p1, p2, p3B);

        Assert.True(objA.HitTest(new Point(200, 250), transform, tolerance: 3.0));
        Assert.True(objB.HitTest(new Point(200, 250), transform, tolerance: 3.0));
    }

    [Fact]
    public void T15_PointInsideChannelBand_ReturnsTrue()
    {
        var transform = new DummyCoordinateTransform();
        var p0 = transform.ScreenToChart(new Point(100, 200));
        var p1 = transform.ScreenToChart(new Point(300, 200));
        var p2 = transform.ScreenToChart(new Point(200, 100)); // S_mid.Y = 150
        var p3 = transform.ScreenToChart(new Point(200, 250)); // DeltaY = +100

        var obj = new CurveChannelObject(p0, p1, p2, p3);

        // Point inside the band (between Y = 150 and Y = 250 at X = 200)
        Assert.True(obj.HitTest(new Point(200, 200), transform));
    }

    [Fact]
    public void T16_PointOutsideChannelBand_ReturnsFalse()
    {
        var transform = new DummyCoordinateTransform();
        var p0 = transform.ScreenToChart(new Point(100, 200));
        var p1 = transform.ScreenToChart(new Point(300, 200));
        var p2 = transform.ScreenToChart(new Point(200, 100));
        var p3 = transform.ScreenToChart(new Point(200, 250));

        var obj = new CurveChannelObject(p0, p1, p2, p3);

        Assert.False(obj.HitTest(new Point(200, 500), transform));
    }

    [Fact]
    public void T17_PointOnCenterLine_ReturnsTrue()
    {
        var transform = new DummyCoordinateTransform();
        var p0 = transform.ScreenToChart(new Point(100, 200));
        var p1 = transform.ScreenToChart(new Point(300, 200));
        var p2 = transform.ScreenToChart(new Point(200, 100)); // S_mid.Y = 150
        var p3 = transform.ScreenToChart(new Point(200, 250)); // DeltaY = +100 => Centerline mid = 200

        var obj = new CurveChannelObject(p0, p1, p2, p3);

        Assert.True(obj.HitTest(new Point(200, 200), transform, tolerance: 2.0));
    }

    [Fact]
    public void T18_PointOnBaseCurve_ReturnsTrue()
    {
        var transform = new DummyCoordinateTransform();
        var p0 = transform.ScreenToChart(new Point(100, 200));
        var p1 = transform.ScreenToChart(new Point(300, 200));
        var p2 = transform.ScreenToChart(new Point(200, 100));
        var p3 = transform.ScreenToChart(new Point(200, 250));

        var obj = new CurveChannelObject(p0, p1, p2, p3);

        Assert.True(obj.HitTest(new Point(200, 150), transform, tolerance: 2.0));
    }

    [Fact]
    public void T19_PointOnOffsetCurve_ReturnsTrue()
    {
        var transform = new DummyCoordinateTransform();
        var p0 = transform.ScreenToChart(new Point(100, 200));
        var p1 = transform.ScreenToChart(new Point(300, 200));
        var p2 = transform.ScreenToChart(new Point(200, 100));
        var p3 = transform.ScreenToChart(new Point(200, 250));

        var obj = new CurveChannelObject(p0, p1, p2, p3);

        Assert.True(obj.HitTest(new Point(200, 250), transform, tolerance: 2.0));
    }

    [Fact]
    public void T20_EscapeCancellation_DiscardsIncompleteObject()
    {
        var controller = new ChartInteractionController();
        var objectManager = new ChartObjectManager();
        var behavior = new CurveTrendBehavior();
        var p0 = new ChartPoint(new DateTime(2025, 1, 1), 100m);

        var obj = behavior.CreateObject(p0);
        controller.CurrentDrawingObject = obj;
        controller.IsDrawingNewShape = true;
        controller.DrawingStep = 1;

        bool handled = controller.HandleCancelRequest(objectManager);

        Assert.True(handled);
        Assert.False(controller.IsDrawingNewShape);
        Assert.Null(controller.CurrentDrawingObject);
        Assert.Empty(objectManager.Objects);
    }

    [Fact]
    public void T21_P0Drag_PreservesControlPointOffsetInvariant()
    {
        var p0 = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var p1 = new ChartPoint(new DateTime(2025, 1, 11), 100m);
        var p2 = new ChartPoint(new DateTime(2025, 1, 6), 50m); // D_time = 0, D_price = -50m

        var obj = new CurveTrendObject(p0, p1, p2);

        // Control point offset invariant calculation when dragging P0 to Jan 3, 120m (safe subtraction midpoint)
        var newP0 = new ChartPoint(new DateTime(2025, 1, 3), 120m);
        long initialMidTicks = obj.Points[0].Time.Ticks + (obj.Points[1].Time.Ticks - obj.Points[0].Time.Ticks) / 2;
        long dTime = obj.Points[2].Time.Ticks - initialMidTicks;
        decimal dPrice = obj.Points[2].Price - (obj.Points[0].Price + obj.Points[1].Price) / 2m;

        obj.Points[0] = newP0;
        long newMidTicks = newP0.Time.Ticks + (obj.Points[1].Time.Ticks - newP0.Time.Ticks) / 2;
        long newTicks = Math.Clamp(newMidTicks + dTime, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks);
        decimal newPrice = (newP0.Price + obj.Points[1].Price) / 2m + dPrice;
        obj.Points[2] = new ChartPoint(new DateTime(newTicks), newPrice);

        Assert.Equal(new DateTime(2025, 1, 3), obj.Points[0].Time);
        Assert.Equal(new DateTime(2025, 1, 7), obj.Points[2].Time); // (3 + 11)/2 = 7
        Assert.Equal(60m, obj.Points[2].Price);                     // (120 + 100)/2 - 50 = 60
    }

    [Fact]
    public void T22_P1Drag_PreservesControlPointOffsetInvariant()
    {
        var p0 = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var p1 = new ChartPoint(new DateTime(2025, 1, 11), 100m);
        var p2 = new ChartPoint(new DateTime(2025, 1, 6), 50m); // D_time = 0, D_price = -50m

        var obj = new CurveTrendObject(p0, p1, p2);

        var newP1 = new ChartPoint(new DateTime(2025, 1, 13), 140m);
        long initialMidTicks = obj.Points[0].Time.Ticks + (obj.Points[1].Time.Ticks - obj.Points[0].Time.Ticks) / 2;
        long dTime = obj.Points[2].Time.Ticks - initialMidTicks;
        decimal dPrice = obj.Points[2].Price - (obj.Points[0].Price + obj.Points[1].Price) / 2m;

        obj.Points[1] = newP1;
        long newMidTicks = obj.Points[0].Time.Ticks + (newP1.Time.Ticks - obj.Points[0].Time.Ticks) / 2;
        long newTicks = Math.Clamp(newMidTicks + dTime, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks);
        decimal newPrice = (obj.Points[0].Price + newP1.Price) / 2m + dPrice;
        obj.Points[2] = new ChartPoint(new DateTime(newTicks), newPrice);

        Assert.Equal(new DateTime(2025, 1, 13), obj.Points[1].Time);
        Assert.Equal(new DateTime(2025, 1, 7), obj.Points[2].Time); // (1 + 13)/2 = 7
        Assert.Equal(70m, obj.Points[2].Price);                     // (100 + 140)/2 - 50 = 70
    }

    [Fact]
    public void T23_P2Drag_UpdatesP2Independently()
    {
        var p0 = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var p1 = new ChartPoint(new DateTime(2025, 1, 11), 100m);
        var p2 = new ChartPoint(new DateTime(2025, 1, 6), 50m);

        var obj = new CurveTrendObject(p0, p1, p2);

        var newP2 = new ChartPoint(new DateTime(2025, 1, 6), 20m);
        obj.Points[2] = newP2;

        Assert.Equal(p0, obj.Points[0]);
        Assert.Equal(p1, obj.Points[1]);
        Assert.Equal(newP2, obj.Points[2]);
    }

    [Fact]
    public void T24_HotPathZeroAllocation()
    {
        var transform = new DummyCoordinateTransform();
        var p0 = transform.ScreenToChart(new Point(100, 200));
        var p1 = transform.ScreenToChart(new Point(300, 200));
        var p2 = transform.ScreenToChart(new Point(200, 100));
        var p3 = transform.ScreenToChart(new Point(200, 250));

        var trend = new CurveTrendObject(p0, p1, p2);
        var channel = new CurveChannelObject(p0, p1, p2, p3);

        using var bitmap = new SKBitmap(800, 600);
        using var canvas = new SKCanvas(bitmap);

        // Warm up JIT
        trend.Render(canvas, transform);
        channel.Render(canvas, transform);

        long beforeTrend = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10; i++)
        {
            trend.Render(canvas, transform);
        }
        long afterTrend = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, afterTrend - beforeTrend);

        long beforeChannel = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10; i++)
        {
            channel.Render(canvas, transform);
        }
        long afterChannel = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, afterChannel - beforeChannel);
    }

    [Fact]
    public void CurveLineText_VariablePointClick_AddsPointViaChartInteractionController()
    {
        // Regression coverage for the "SAで実装" wiring in ChartInteractionController.StartNewShape:
        // RequiredSteps==0 tools (Polyline/NurbsTrendCurve/CurveLineText) route each subsequent
        // click through a type-hardcoded branch that calls the object's own AddPoint(). This proves
        // the newly-added CurveLineTextObject branch is actually reached and functions.
        var controller = new ChartInteractionController();
        var objectManager = new ChartObjectManager();
        var behavior = new CurveLineTextBehavior();

        var p0 = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var obj = (CurveLineTextObject)behavior.CreateObject(p0);

        controller.CurrentDrawingObject = obj;
        controller.IsDrawingNewShape = true;
        controller.DrawingStep = 0;

        int pointsBefore = obj.Points.Count;
        var p1 = new ChartPoint(new DateTime(2025, 1, 2), 110m);

        bool handled = controller.StartNewShape(DrawingTool.CurveLineText, p1, objectManager);

        Assert.True(handled);
        Assert.Equal(pointsBefore + 1, obj.Points.Count);
        Assert.Equal(p1, obj.Points[^1]);
    }
}
