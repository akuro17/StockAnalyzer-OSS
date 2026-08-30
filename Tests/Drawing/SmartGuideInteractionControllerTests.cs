using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Input;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models;
using Xunit;
using Point = global::Avalonia.Point;
using Rect = global::Avalonia.Rect;

namespace StockAnalyzer.Tests.Drawing;

public class SmartGuideInteractionControllerTests
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
            double x = (chartPoint.Time - new DateTime(2025, 1, 1)).TotalDays * 10.0;
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
    private readonly ChartDataSnapshot _snapshot = ChartDataSnapshot.Empty;

    [Fact]
    public void ObjectDrag_WithSmartGuides_AppliesSnapAndExposesGuideLines()
    {
        var magnetService = new MagnetSnapService();
        var smartGuideService = new SmartGuideService();
        var controller = new ChartInteractionController(magnetService, new DialogService(), smartGuideService);

        var objectManager = new ChartObjectManager();
        // Target: X [100, 200], Y [200, 300]
        var target = new RectangleObject(
            new ChartPoint(new DateTime(2025, 1, 11), 600),
            new ChartPoint(new DateTime(2025, 1, 21), 500));
        objectManager.AddObject(target);

        // Dragged: Initial X [10, 60], Y [700, 750]
        var dragged = new RectangleObject(
            new ChartPoint(new DateTime(2025, 1, 2), 100),
            new ChartPoint(new DateTime(2025, 1, 7), 50));
        objectManager.AddObject(dragged);
        objectManager.SelectObject(dragged.Id);

        // Simulate pointer press on dragged object
        controller.SetActiveObjectManager(objectManager);
        controller.DraggedObject = dragged;
        controller.DraggedHandleIndex = -1;
        controller.LastDragPoint = new Point(50, 50);

        // Proposed drag point puts dragged.Left near target.Left (100) -> e.g., 98 (2px away <= 5px)
        // chartMarginHorizontal = 0, chartMarginTop = 0
        var newMousePos = new Point(50 + 88, 50); // delta = 88px -> new dragged Left = 10 + 88 = 98px
        var handled = controller.HandleObjectDrag(newMousePos, _snapshot, _transform, 0, 0, new Rect(0, 0, 1000, 800), KeyModifiers.None);

        // Verify that dragging succeeded
        Assert.True(handled);
        // Verify guide lines populated
        Assert.NotEmpty(controller.ActiveSmartGuideLines);
        Assert.Contains(controller.ActiveSmartGuideLines, g => g.Axis == SmartGuideAxis.Vertical && Math.Abs(g.Position - 100f) < 0.1f);
    }

    [Fact]
    public void ObjectDrag_AfterTransientSnap_DoesNotPermanentlyDriftFromCursor()
    {
        var magnetService = new MagnetSnapService();
        var smartGuideService = new SmartGuideService();
        var controller = new ChartInteractionController(magnetService, new DialogService(), smartGuideService);

        var objectManager = new ChartObjectManager();
        // Target: X [100, 200], Y [200, 300]
        var target = new RectangleObject(
            new ChartPoint(new DateTime(2025, 1, 11), 600),
            new ChartPoint(new DateTime(2025, 1, 21), 500));
        objectManager.AddObject(target);

        // Dragged: Initial X [10, 60], Y [700, 750]
        var dragged = new RectangleObject(
            new ChartPoint(new DateTime(2025, 1, 2), 100),
            new ChartPoint(new DateTime(2025, 1, 7), 50));
        objectManager.AddObject(dragged);
        objectManager.SelectObject(dragged.Id);

        controller.SetActiveObjectManager(objectManager);
        controller.DraggedObject = dragged;
        controller.DraggedHandleIndex = -1;
        controller.LastDragPoint = new Point(50, 50);

        var initialTime = dragged.Points[0].Time;

        // Frame 1: mouse moves near the target's Left edge (100px) -> Smart Guide
        // snaps dragged.Left from 98px onto 100px (a transient +2px correction).
        var frame1MousePos = new Point(50 + 88, 50);
        Assert.True(controller.HandleObjectDrag(frame1MousePos, _snapshot, _transform, 0, 0, new Rect(0, 0, 1000, 800), KeyModifiers.None));
        Assert.NotEmpty(controller.ActiveSmartGuideLines); // Confirms the snap actually engaged this frame.

        // Frame 2: mouse continues far past the target, well outside snap range.
        var frame2MousePos = new Point(50 + 500, 50);
        Assert.True(controller.HandleObjectDrag(frame2MousePos, _snapshot, _transform, 0, 0, new Rect(0, 0, 1000, 800), KeyModifiers.None));
        Assert.Empty(controller.ActiveSmartGuideLines); // No longer near any target.

        // Total mouse displacement across both frames: (550 - 50)px = 50 days
        // (TestCoordinateTransform maps 10px per day). Once no snap is active, the
        // dragged object's total displacement must match the cursor's exactly,
        // regardless of a transient snap earlier in the same drag gesture -- a
        // regression test for a bug where the transient correction was permanently
        // baked into the object's position instead of being re-evaluated each frame.
        var expectedTime = initialTime.AddDays(50);
        Assert.Equal(expectedTime, dragged.Points[0].Time);
    }

    [Fact]
    public void ObjectDrag_WithAltModifier_BypassesSmartGuides()
    {
        var magnetService = new MagnetSnapService();
        var smartGuideService = new SmartGuideService();
        var controller = new ChartInteractionController(magnetService, new DialogService(), smartGuideService);

        var objectManager = new ChartObjectManager();
        var target = new RectangleObject(
            new ChartPoint(new DateTime(2025, 1, 11), 600),
            new ChartPoint(new DateTime(2025, 1, 21), 500));
        objectManager.AddObject(target);

        var dragged = new RectangleObject(
            new ChartPoint(new DateTime(2025, 1, 2), 100),
            new ChartPoint(new DateTime(2025, 1, 7), 50));
        objectManager.AddObject(dragged);
        objectManager.SelectObject(dragged.Id);

        controller.SetActiveObjectManager(objectManager);
        controller.DraggedObject = dragged;
        controller.DraggedHandleIndex = -1;
        controller.LastDragPoint = new Point(50, 50);

        var newMousePos = new Point(50 + 88, 50);
        // Drag with Alt modifier
        var handled = controller.HandleObjectDrag(newMousePos, _snapshot, _transform, 0, 0, new Rect(0, 0, 1000, 800), KeyModifiers.Alt);

        Assert.True(handled);
        // Verify guide lines are empty because Alt bypassed smart guides
        Assert.Empty(controller.ActiveSmartGuideLines);
    }

    [Fact]
    public void HandleObjectDrag_FixedRangeVolumeProfile_DefersRecalculationUntilRelease()
    {
        var controller = new ChartInteractionController(new MagnetSnapService(), new DialogService(), new SmartGuideService());

        var objectManager = new ChartObjectManager();
        var dragged = new FixedRangeVolumeProfileObject(
            new ChartPoint(new DateTime(2025, 1, 2), 700),
            new ChartPoint(new DateTime(2025, 1, 7), 750));
        objectManager.AddObject(dragged);
        objectManager.SelectObject(dragged.Id);

        controller.SetActiveObjectManager(objectManager);
        controller.DraggedObject = dragged;
        controller.DraggedHandleIndex = 1; // Dragging the P1 (end) handle.
        controller.LastDragPoint = new Point(70, 100);

        var viewModel = new ChartViewModel
        {
            Candles = new List<CoreCandleData>
            {
                new CoreCandleData(new DateTime(2025, 1, 2), 100, 110, 90, 105, 1000),
                new CoreCandleData(new DateTime(2025, 1, 3), 105, 115, 95, 110, 1200),
                new CoreCandleData(new DateTime(2025, 1, 4), 110, 120, 100, 115, 1300),
                new CoreCandleData(new DateTime(2025, 1, 5), 115, 125, 105, 120, 1400),
                new CoreCandleData(new DateTime(2025, 1, 6), 120, 130, 110, 125, 1500),
                new CoreCandleData(new DateTime(2025, 1, 7), 125, 135, 115, 130, 1600),
            }
        };

        Assert.Empty(dragged.ProfileData); // Precondition: nothing computed yet.

        // Dragging the handle must move the point but must NOT recompute the volume
        // profile mid-drag (that per-frame LINQ + histogram recompute caused stutter).
        var handled = controller.HandleObjectDrag(new Point(90, 100), _snapshot, _transform, 0, 0, new Rect(0, 0, 1000, 800), KeyModifiers.None);
        Assert.True(handled);
        Assert.Empty(dragged.ProfileData);

        // Releasing the drag performs the deferred recalculation exactly once.
        controller.HandlePointerReleased(viewModel);
        Assert.NotEmpty(dragged.ProfileData);
    }

    [Fact]
    public void HandleObjectDrag_GeometricPatternMove_DefersRecalculationUntilRelease()
    {
        var controller = new ChartInteractionController(new MagnetSnapService(), new DialogService(), new SmartGuideService());

        var objectManager = new ChartObjectManager();
        var dragged = new StockAnalyzer.Avalonia.Drawing.Objects.GeometricPatternObject(
            new ChartPoint(new DateTime(2025, 1, 2), 100));
        dragged.Points.Add(new ChartPoint(new DateTime(2025, 1, 7), 150));
        objectManager.AddObject(dragged);
        objectManager.SelectObject(dragged.Id);

        controller.SetActiveObjectManager(objectManager);
        controller.DraggedObject = dragged;
        controller.DraggedHandleIndex = -1; // Whole-object Move drag.
        controller.LastDragPoint = new Point(50, 50);

        var viewModel = new ChartViewModel
        {
            Candles = new List<CoreCandleData>
            {
                new CoreCandleData(new DateTime(2025, 1, 2), 100, 110, 90, 105, 1000),
                new CoreCandleData(new DateTime(2025, 1, 3), 105, 115, 95, 110, 1200),
            }
        };

        Assert.Null(dragged.Formations); // Precondition: Recalculate never ran.

        // Moving the object must translate its points but must NOT re-run pattern
        // detection mid-drag (that per-frame LINQ + detection recompute caused stutter).
        var handled = controller.HandleObjectDrag(new Point(88, 50), _snapshot, _transform, 0, 0, new Rect(0, 0, 1000, 800), KeyModifiers.None);
        Assert.True(handled);
        Assert.Null(dragged.Formations);

        // Releasing the drag performs the deferred recalculation exactly once
        // (Recalculate always assigns a non-null Formations, even on empty detection).
        controller.HandlePointerReleased(viewModel);
        Assert.NotNull(dragged.Formations);
    }
}
