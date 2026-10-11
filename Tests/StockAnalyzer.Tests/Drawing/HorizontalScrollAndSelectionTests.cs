using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Input;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.Chart;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;
using Point = Avalonia.Point;

namespace StockAnalyzer.Tests.Drawing;

public class HorizontalScrollAndSelectionTests
{
    private sealed class TestGeometricObject : RelativeGeometricRenderer
    {
        public override ChartObjectType Type => ChartObjectType.TrendLine;

        public TestGeometricObject(DateTime time1, decimal price1, DateTime time2, decimal price2)
        {
            Points.Add(new ChartPoint(time1, price1));
            Points.Add(new ChartPoint(time2, price2));
        }

        protected override void DrawGeometry(SKCanvas canvas, ICoordinateTransform transform) { }

        public override bool HitTest(Point screenPoint, ICoordinateTransform transform, double tolerance = 5)
        {
            var p1 = transform.ChartToScreen(Points[0]);
            var p2 = transform.ChartToScreen(Points[1]);
            double minX = Math.Min(p1.X, p2.X) - tolerance;
            double maxX = Math.Max(p1.X, p2.X) + tolerance;
            double minY = Math.Min(p1.Y, p2.Y) - tolerance;
            double maxY = Math.Max(p1.Y, p2.Y) + tolerance;
            return screenPoint.X >= minX && screenPoint.X <= maxX && screenPoint.Y >= minY && screenPoint.Y <= maxY;
        }
    }

    private static GenericCoordinateTransform CreateTransform(double width = 800, double height = 600)
    {
        var transform = new GenericCoordinateTransform(ChartAxisMode.Time, width, height);
        transform.SetTimeRange(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc));
        transform.SetPriceRange(100m, 200m);
        return transform;
    }

    [Fact]
    public void HandlePointerTool_EmptySpace_WithoutModifiers_ReturnsFalseToAllowPan_AndDeselects()
    {
        var chartVm = new ChartViewModel { CurrentTool = DrawingTool.Pointer };
        var controller = new ChartInteractionController();
        var transform = CreateTransform();

        // Place an object at (Jan 5, 150)
        var obj = new TestGeometricObject(
            new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc), 150m,
            new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc), 155m);
        chartVm.ObjectManager.AddObject(obj);
        chartVm.ObjectManager.SelectObject(obj.Id);
        Assert.True(chartVm.ObjectManager.HasSelection);

        // Click in empty space at top-left (Jan 2, 190) far from the object
        var screenPoint = new Point(100, 100);
        bool handled = controller.HandlePointerPressed(
            screenPoint,
            screenPoint,
            chartVm,
            transform,
            KeyModifiers.None,
            clickCount: 1);

        // Must return false to allow chart panning fallback
        Assert.False(handled);
        Assert.False(controller.IsRubberBandSelecting);
        // Active selection should be cleared on empty space click
        Assert.False(chartVm.ObjectManager.HasSelection);
    }

    [Fact]
    public void HandlePointerTool_EmptySpace_WithShift_ReturnsFalse_PreservingShiftForDelete()
    {
        var chartVm = new ChartViewModel { CurrentTool = DrawingTool.Pointer };
        var controller = new ChartInteractionController();
        var transform = CreateTransform();

        var screenPoint = new Point(100, 100);
        bool handled = controller.HandlePointerPressed(
            screenPoint,
            screenPoint,
            chartVm,
            transform,
            KeyModifiers.Shift,
            clickCount: 1);

        // Shift is dedicated to shape deletion; clicking empty space with Shift does not enter rubber band
        Assert.False(handled);
        Assert.False(controller.IsRubberBandSelecting);
    }

    [Fact]
    public void HandlePointerTool_EmptySpace_WithControl_ReturnsTrue_AndStartsRubberBand()
    {
        var chartVm = new ChartViewModel { CurrentTool = DrawingTool.Pointer };
        var controller = new ChartInteractionController();
        var transform = CreateTransform();

        var screenPoint = new Point(100, 100);
        bool handled = controller.HandlePointerPressed(
            screenPoint,
            screenPoint,
            chartVm,
            transform,
            KeyModifiers.Control,
            clickCount: 1);

        Assert.True(handled);
        Assert.True(controller.IsRubberBandSelecting);
        Assert.Equal(screenPoint, controller.RubberBandStartPoint);
    }

    [Fact]
    public void RubberBand_OnPointerReleased_SelectsIntersectingObjects()
    {
        var chartVm = new ChartViewModel { CurrentTool = DrawingTool.Pointer };
        var controller = new ChartInteractionController();
        var transform = CreateTransform(1000, 800);

        // obj1: around Jan 2 to Jan 3 (screen X approx 100..250)
        var obj1 = new TestGeometricObject(
            new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), 140m,
            new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc), 160m);
        // obj2: around Jan 8 to Jan 9 (screen X approx 750..900)
        var obj2 = new TestGeometricObject(
            new DateTime(2026, 1, 8, 0, 0, 0, DateTimeKind.Utc), 140m,
            new DateTime(2026, 1, 9, 0, 0, 0, DateTimeKind.Utc), 160m);

        chartVm.ObjectManager.AddObject(obj1);
        chartVm.ObjectManager.AddObject(obj2);

        // Start rubber band at (50, 50) with Control
        controller.HandlePointerPressed(
            new Point(50, 50),
            new Point(50, 50),
            chartVm,
            transform,
            KeyModifiers.Control,
            clickCount: 1);

        Assert.True(controller.IsRubberBandSelecting);

        // Move to (300, 500) covering obj1 but not obj2
        controller.HandlePointerMoved(
            new Point(300, 500),
            new Point(300, 500),
            chartVm,
            transform,
            new ChartDataSnapshot(new List<CoreCandleData>()),
            new Rect(0, 0, 1000, 800),
            0,
            0,
            out _,
            KeyModifiers.Shift);

        // Release pointer
        bool released = controller.HandlePointerReleased(chartVm, transform);

        Assert.True(released);
        Assert.False(controller.IsRubberBandSelecting);
        Assert.True(obj1.IsSelected);
        Assert.False(obj2.IsSelected);
        Assert.Single(chartVm.ObjectManager.SelectedIds);
        Assert.Contains(obj1.Id, chartVm.ObjectManager.SelectedIds);
    }
}
