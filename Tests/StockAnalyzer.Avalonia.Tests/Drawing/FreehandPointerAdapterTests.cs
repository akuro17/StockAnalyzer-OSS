using System;
using Avalonia;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;
using Point = Avalonia.Point;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

[Collection("DrawingThemeContext State")]
public class FreehandPointerAdapterTests
{
    [Theory]
    [InlineData(DrawingToolContinuationMode.ContinueDrawing)]
    [InlineData(DrawingToolContinuationMode.ReturnToPointer)]
    public void RealCoordinator_RepeatedStrokes_ReleaseOwnershipAndCreateSeparateHistory(DrawingToolContinuationMode mode)
    {
        var previousMode = DrawingThemeContext.DrawingToolContinuationMode;
        DrawingThemeContext.SetDrawingToolContinuationModeForTesting(mode);
        var viewModel = new ChartViewModel { CurrentTool = DrawingTool.Freehand };
        viewModel.UpdateDrawingCoordinator();
        var coordinator = Assert.IsAssignableFrom<StockAnalyzer.Avalonia.Services.Drawing.IDrawingEditCoordinator>(viewModel.DrawingEditCoordinator);
        var adapter = new FreehandPointerAdapter();
        var transform = CreateTransform();
        try
        {
            for (int stroke = 0; stroke < 3; stroke++)
            {
                Assert.Equal(DrawingTool.Freehand, viewModel.CurrentTool);
                Assert.True(adapter.HandlePointerPressed(new Point(10, 10), new Point(10, 10), 1, viewModel, transform, -1, PanelKey.Main));
                Assert.True(adapter.IsActive);
                Assert.True(coordinator.IsEditing);
                Assert.True(adapter.HandlePointerMoved(new Point(20, 20), new Point(20, 20), 1, transform, out _));
                Assert.True(adapter.HandlePointerReleased(new Point(30, 30), new Point(30, 30), 1, viewModel, transform, -1, PanelKey.Main, out _));
                Assert.False(adapter.IsActive);
                Assert.False(coordinator.IsEditing);
                Assert.Equal(DrawingTool.Freehand, viewModel.CurrentTool);
                Assert.Equal(stroke + 1, coordinator.UndoCount);
                Assert.Equal(stroke + 1, viewModel.ObjectManager.Objects.Count);
            }
            Assert.True(coordinator.Undo().IsSuccess);
            Assert.Equal(2, viewModel.ObjectManager.Objects.Count);
            Assert.True(coordinator.Redo().IsSuccess);
            Assert.Equal(3, viewModel.ObjectManager.Objects.Count);
        }
        finally
        {
            coordinator.Dispose();
            DrawingThemeContext.SetDrawingToolContinuationModeForTesting(previousMode);
        }
    }

    private static ICoordinateTransform CreateTransform() =>
        new GenericCoordinateTransform(ChartAxisMode.Time, 1000, 500);

    [Fact]
    public void HandlePointerPressed_WhenToolNotFreehand_ReturnsFalse()
    {
        var adapter = new FreehandPointerAdapter();
        var viewModel = new ChartViewModel { CurrentTool = DrawingTool.Pointer };
        var transform = CreateTransform();

        bool handled = adapter.HandlePointerPressed(
            new Point(10, 10),
            new Point(10, 10),
            pointerId: 1,
            viewModel,
            transform,
            panelIndex: -1,
            PanelKey.Main);

        Assert.False(handled);
        Assert.False(adapter.IsActive);
        Assert.Null(adapter.PreviewObject);
    }

    [Fact]
    public void HandlePointerPressed_WhenToolIsFreehand_StartsPreview()
    {
        var adapter = new FreehandPointerAdapter();
        var viewModel = new ChartViewModel { CurrentTool = DrawingTool.Freehand };
        var transform = CreateTransform();

        bool handled = adapter.HandlePointerPressed(
            new Point(10, 10),
            new Point(10, 10),
            pointerId: 1,
            viewModel,
            transform,
            panelIndex: -1,
            PanelKey.Main);

        Assert.True(handled);
        Assert.True(adapter.IsActive);
        Assert.NotNull(adapter.PreviewObject);
        Assert.Equal(1, adapter.PreviewObject.Points.Count);
    }

    [Fact]
    public void HandlePointerPressed_WhenActiveLayerIsEditLocked_Rejects()
    {
        var adapter = new FreehandPointerAdapter();
        var viewModel = new ChartViewModel { CurrentTool = DrawingTool.Freehand };
        var transform = CreateTransform();

        // Lock the active layer
        var activeLayerId = viewModel.LayerService.GetActiveLayerId(PanelKey.Main);
        Assert.True(activeLayerId.HasValue);
        viewModel.LayerService.SetLayerEditLock(activeLayerId.Value, true);

        bool handled = adapter.HandlePointerPressed(
            new Point(10, 10),
            new Point(10, 10),
            pointerId: 1,
            viewModel,
            transform,
            panelIndex: -1,
            PanelKey.Main);

        Assert.True(handled); // Handled to suppress drawing
        Assert.False(adapter.IsActive);
        Assert.Null(adapter.PreviewObject);
    }

    [Fact]
    public void HandlePointerMoved_WhenActive_UpdatesPreview()
    {
        var adapter = new FreehandPointerAdapter();
        var viewModel = new ChartViewModel { CurrentTool = DrawingTool.Freehand };
        var transform = CreateTransform();

        adapter.HandlePointerPressed(new Point(10, 10), new Point(10, 10), 1, viewModel, transform, -1, PanelKey.Main);

        bool moved = adapter.HandlePointerMoved(new Point(20, 20), new Point(20, 20), 1, transform, out bool needsInvalidate);

        Assert.True(moved);
        Assert.True(needsInvalidate);
        Assert.NotNull(adapter.PreviewObject);
        Assert.Equal(2, adapter.PreviewObject.Points.Count);
    }

    [Fact]
    public void HandlePointerReleased_CommitsToManagerAndLayer()
    {
        DrawingThemeContext.SetDrawingToolContinuationModeForTesting(DrawingToolContinuationMode.ReturnToPointer);

        var adapter = new FreehandPointerAdapter();
        var viewModel = new ChartViewModel { CurrentTool = DrawingTool.Freehand };
        var transform = CreateTransform();

        adapter.HandlePointerPressed(new Point(10, 10), new Point(10, 10), 1, viewModel, transform, -1, PanelKey.Main);
        adapter.HandlePointerMoved(new Point(20, 20), new Point(20, 20), 1, transform, out _);

        bool released = adapter.HandlePointerReleased(
            new Point(30, 30),
            new Point(30, 30),
            pointerId: 1,
            viewModel,
            transform,
            panelIndex: -1,
            PanelKey.Main,
            out bool needsInvalidate);

        Assert.True(released);
        Assert.True(needsInvalidate);
        Assert.False(adapter.IsActive);
        Assert.Null(adapter.PreviewObject);

        // Committed object check
        Assert.Single(viewModel.ObjectManager.Objects);
        var committed = viewModel.ObjectManager.Objects[0] as FreehandObject;
        Assert.NotNull(committed);
        Assert.Equal(3, committed.Points.Count);
        Assert.True(committed.Points.Capacity <= 4, $"Capacity should be trimmed, but was {committed.Points.Capacity}");

        // Layer check
        Assert.True(viewModel.LayerService.IsObjectRegistered(committed.Id));

        // Tool continuation check: Freehand tool is maintained for continuous drawing
        Assert.Equal(DrawingTool.Freehand, viewModel.CurrentTool);
    }

    [Fact]
    public void HandlePointerReleased_ContinueDrawingMode_PreservesTool()
    {
        DrawingThemeContext.SetDrawingToolContinuationModeForTesting(DrawingToolContinuationMode.ContinueDrawing);

        var adapter = new FreehandPointerAdapter();
        var viewModel = new ChartViewModel { CurrentTool = DrawingTool.Freehand };
        var transform = CreateTransform();

        adapter.HandlePointerPressed(new Point(10, 10), new Point(10, 10), 1, viewModel, transform, -1, PanelKey.Main);
        adapter.HandlePointerReleased(new Point(20, 20), new Point(20, 20), 1, viewModel, transform, -1, PanelKey.Main, out _);

        Assert.Equal(DrawingTool.Freehand, viewModel.CurrentTool);
    }

    [Fact]
    public void HandleCancel_DiscardsPreviewWithoutCommit()
    {
        var adapter = new FreehandPointerAdapter();
        var viewModel = new ChartViewModel { CurrentTool = DrawingTool.Freehand };
        var transform = CreateTransform();

        adapter.HandlePointerPressed(new Point(10, 10), new Point(10, 10), 1, viewModel, transform, -1, PanelKey.Main);
        adapter.HandlePointerMoved(new Point(20, 20), new Point(20, 20), 1, transform, out _);

        bool cancelled = adapter.HandleCancel(DrawingCancelReason.CaptureLost, out bool needsInvalidate);

        Assert.True(cancelled);
        Assert.True(needsInvalidate);
        Assert.False(adapter.IsActive);
        Assert.Null(adapter.PreviewObject);
        Assert.Empty(viewModel.ObjectManager.Objects);
    }

    [Fact]
    public void HandleModifierComplete_CommitsCapturedPointsAndRevertsToPointer()
    {
        var adapter = new FreehandPointerAdapter();
        var viewModel = new ChartViewModel { CurrentTool = DrawingTool.Freehand };
        var transform = CreateTransform();

        adapter.HandlePointerPressed(new Point(10, 10), new Point(10, 10), 1, viewModel, transform, -1, PanelKey.Main);
        adapter.HandlePointerMoved(new Point(20, 20), new Point(20, 20), 1, transform, out _);

        bool completed = adapter.HandleModifierComplete(viewModel, -1, PanelKey.Main, out bool needsInvalidate);

        Assert.True(completed);
        Assert.True(needsInvalidate);
        Assert.False(adapter.IsActive);
        Assert.Null(adapter.PreviewObject);
        Assert.Equal(DrawingTool.Pointer, viewModel.CurrentTool);

        Assert.Single(viewModel.ObjectManager.Objects);
        var obj = viewModel.ObjectManager.Objects[0];
        Assert.Equal(2, obj.Points.Count);
    }

    [Fact]
    public void HandlePointerReleased_ConsecutiveStrokes_SucceedWithoutException()
    {
        var adapter = new FreehandPointerAdapter();
        var viewModel = new ChartViewModel { CurrentTool = DrawingTool.Freehand };
        var transform = CreateTransform();

        // Stroke 1
        adapter.HandlePointerPressed(new Point(10, 10), new Point(10, 10), 1, viewModel, transform, -1, PanelKey.Main);
        adapter.HandlePointerMoved(new Point(20, 20), new Point(20, 20), 1, transform, out _);
        adapter.HandlePointerReleased(new Point(30, 30), new Point(30, 30), 1, viewModel, transform, -1, PanelKey.Main, out _);

        Assert.Single(viewModel.ObjectManager.Objects);

        // Reset tool for Stroke 2
        viewModel.CurrentTool = DrawingTool.Freehand;

        // Stroke 2
        adapter.HandlePointerPressed(new Point(50, 50), new Point(50, 50), 2, viewModel, transform, -1, PanelKey.Main);
        adapter.HandlePointerMoved(new Point(60, 60), new Point(60, 60), 2, transform, out _);
        adapter.HandlePointerReleased(new Point(70, 70), new Point(70, 70), 2, viewModel, transform, -1, PanelKey.Main, out _);

        Assert.Equal(2, viewModel.ObjectManager.Objects.Count);
        Assert.False(adapter.IsActive);
        Assert.Null(adapter.PreviewObject);
    }

    [Fact]
    public void HandlePointerReleased_NonOwnerPointer_DoesNotCancelActiveStroke()
    {
        var adapter = new FreehandPointerAdapter();
        var viewModel = new ChartViewModel { CurrentTool = DrawingTool.Freehand };
        var transform = CreateTransform();

        adapter.HandlePointerPressed(new Point(10, 10), new Point(10, 10), pointerId: 1, viewModel, transform, -1, PanelKey.Main);
        Assert.True(adapter.IsActive);

        // Event from non-owner pointer (pointerId: 2)
        bool nonOwnerReleased = adapter.HandlePointerReleased(
            new Point(20, 20),
            new Point(20, 20),
            pointerId: 2,
            viewModel,
            transform,
            -1,
            PanelKey.Main,
            out _);

        Assert.False(nonOwnerReleased);
        Assert.True(adapter.IsActive);
        Assert.NotNull(adapter.PreviewObject);

        // Real owner releases
        bool ownerReleased = adapter.HandlePointerReleased(
            new Point(20, 20),
            new Point(20, 20),
            pointerId: 1,
            viewModel,
            transform,
            -1,
            PanelKey.Main,
            out _);

        Assert.True(ownerReleased);
        Assert.False(adapter.IsActive);
        Assert.Null(adapter.PreviewObject);
    }

    [Fact]
    public void SecondContact_WhileStrokeActive_IsIgnored_AndOnlyTheOwnerCommitsOneObjectAndOneHistoryEntry()
    {
        var viewModel = new ChartViewModel { CurrentTool = DrawingTool.Freehand };
        viewModel.UpdateDrawingCoordinator();
        var coordinator = Assert.IsAssignableFrom<StockAnalyzer.Avalonia.Services.Drawing.IDrawingEditCoordinator>(viewModel.DrawingEditCoordinator);
        var adapter = new FreehandPointerAdapter();
        var transform = CreateTransform();
        try
        {
            Assert.True(adapter.HandlePointerPressed(new Point(10, 10), new Point(10, 10), pointerId: 1, viewModel, transform, -1, PanelKey.Main));

            // Second contact: no second stroke, no second edit transaction, owner unchanged.
            Assert.False(adapter.HandlePointerPressed(new Point(300, 200), new Point(300, 200), pointerId: 2, viewModel, transform, -1, PanelKey.Main));
            Assert.Equal(1, adapter.Controller.OwnedPointerId);
            Assert.True(coordinator.IsEditing);

            Assert.False(adapter.HandlePointerMoved(new Point(310, 210), new Point(310, 210), pointerId: 2, transform, out _));
            Assert.True(adapter.HandlePointerMoved(new Point(20, 20), new Point(20, 20), pointerId: 1, transform, out _));
            Assert.False(adapter.HandlePointerReleased(new Point(320, 220), new Point(320, 220), pointerId: 2, viewModel, transform, -1, PanelKey.Main, out _));
            Assert.True(adapter.IsActive);
            Assert.Equal(0, coordinator.UndoCount);

            Assert.True(adapter.HandlePointerReleased(new Point(30, 30), new Point(30, 30), pointerId: 1, viewModel, transform, -1, PanelKey.Main, out _));

            Assert.False(coordinator.IsEditing);
            Assert.Equal(1, coordinator.UndoCount);
            var stroke = Assert.Single(viewModel.ObjectManager.Objects);
            Assert.Equal(3, stroke.Points.Count); // press + owner move + owner release; nothing from the second contact
        }
        finally
        {
            coordinator.Dispose();
        }
    }

    [Fact]
    public void HandlePointerPressed_WhenLayerHidden_Rejects()
    {
        var adapter = new FreehandPointerAdapter();
        var viewModel = new ChartViewModel { CurrentTool = DrawingTool.Freehand };
        var transform = CreateTransform();

        // Hide the active layer
        var activeLayerId = viewModel.LayerService.GetActiveLayerId(PanelKey.Main);
        Assert.True(activeLayerId.HasValue);
        viewModel.LayerService.SetLayerVisibility(activeLayerId.Value, false);

        bool handled = adapter.HandlePointerPressed(
            new Point(10, 10),
            new Point(10, 10),
            pointerId: 1,
            viewModel,
            transform,
            panelIndex: -1,
            PanelKey.Main);

        Assert.True(handled); // Handled to suppress drawing
        Assert.False(adapter.IsActive);
        Assert.Null(adapter.PreviewObject);
    }

    [Fact]
    public void HandlePointerPressed_NonFiniteCoordinates_Rejects()
    {
        var adapter = new FreehandPointerAdapter();
        var viewModel = new ChartViewModel { CurrentTool = DrawingTool.Freehand };
        var transform = CreateTransform();

        bool handled = adapter.HandlePointerPressed(
            new Point(double.NaN, 10),
            new Point(10, 10),
            pointerId: 1,
            viewModel,
            transform,
            panelIndex: -1,
            PanelKey.Main);

        Assert.False(handled);
        Assert.False(adapter.IsActive);
        Assert.Null(adapter.PreviewObject);
    }

    [Fact]
    public void HandlePointerPressed_StyleLockedAndCapacityPreAllocated()
    {
        var adapter = new FreehandPointerAdapter();
        var viewModel = new ChartViewModel { CurrentTool = DrawingTool.Freehand };
        var transform = CreateTransform();

        adapter.HandlePointerPressed(new Point(10, 10), new Point(10, 10), 1, viewModel, transform, -1, PanelKey.Main);

        Assert.NotNull(adapter.PreviewObject);
        Assert.True(adapter.PreviewObject.Points.Capacity >= DrawingInteractionLimits.MaxStrokeSamples);
        Assert.Equal(DrawingThemeContext.DefaultColor, adapter.PreviewObject.Color);
        Assert.Equal(DrawingThemeContext.DefaultStrokeThickness, adapter.PreviewObject.Thickness);
    }

    [Fact]
    public void HandlePointerReleased_ConsecutiveStrokes_SamePointerId_ContinueDrawingMode_Succeeds()
    {
        var adapter = new FreehandPointerAdapter();
        var viewModel = new ChartViewModel { Symbol = "AAPL", CurrentTool = DrawingTool.Freehand };
        viewModel.UpdateDrawingCoordinator();

        var prevMode = DrawingThemeContext.DrawingToolContinuationMode;
        try
        {
            DrawingThemeContext.SetDrawingToolContinuationModeForTesting(DrawingToolContinuationMode.ContinueDrawing);
            var transform = CreateTransform();

            // Stroke 1 (pointerId: 10)
            adapter.HandlePointerPressed(new Point(10, 10), new Point(10, 10), 10, viewModel, transform, -1, PanelKey.Main);
            adapter.HandlePointerMoved(new Point(20, 20), new Point(20, 20), 10, transform, out _);
            adapter.HandlePointerReleased(new Point(30, 30), new Point(30, 30), 10, viewModel, transform, -1, PanelKey.Main, out _);

            Assert.Single(viewModel.ObjectManager.Objects);
            Assert.Equal(DrawingTool.Freehand, viewModel.CurrentTool);
            Assert.False(adapter.IsActive);

            // Stroke 2 (same pointerId: 10, no tool reset)
            adapter.HandlePointerPressed(new Point(50, 50), new Point(50, 50), 10, viewModel, transform, -1, PanelKey.Main);
            adapter.HandlePointerMoved(new Point(60, 60), new Point(60, 60), 10, transform, out _);
            adapter.HandlePointerReleased(new Point(70, 70), new Point(70, 70), 10, viewModel, transform, -1, PanelKey.Main, out _);

            Assert.Equal(2, viewModel.ObjectManager.Objects.Count);
            Assert.Equal(DrawingTool.Freehand, viewModel.CurrentTool);
            Assert.False(adapter.IsActive);

            // Stroke 3 (same pointerId: 10, no tool reset)
            adapter.HandlePointerPressed(new Point(100, 100), new Point(100, 100), 10, viewModel, transform, -1, PanelKey.Main);
            adapter.HandlePointerMoved(new Point(110, 110), new Point(110, 110), 10, transform, out _);
            adapter.HandlePointerReleased(new Point(120, 120), new Point(120, 120), 10, viewModel, transform, -1, PanelKey.Main, out _);

            Assert.Equal(3, viewModel.ObjectManager.Objects.Count);
            Assert.Equal(DrawingTool.Freehand, viewModel.CurrentTool);
            Assert.False(adapter.IsActive);
        }
        finally
        {
            DrawingThemeContext.SetDrawingToolContinuationModeForTesting(prevMode);
        }
    }

    private class FailingTransform : ICoordinateTransform
    {
        private readonly ICoordinateTransform _inner = CreateTransform();

        public bool ShouldThrow { get; set; } = true;

        public ChartPoint ScreenToChart(Point screenPoint)
        {
            if (ShouldThrow)
            {
                throw new InvalidOperationException("Simulated coordinate transform overflow");
            }
            return _inner.ScreenToChart(screenPoint);
        }

        public Point ChartToScreen(ChartPoint chartPoint) => _inner.ChartToScreen(chartPoint);
        public Point NumericToScreen(double x, double y) => _inner.NumericToScreen(x, y);
        public (double x, double y) ScreenToNumeric(Point screenPoint) => _inner.ScreenToNumeric(screenPoint);
        public void UpdateRange(DateTime minTime, DateTime maxTime, decimal minPrice, decimal maxPrice, double? newCanvasWidth = null, double? newCanvasHeight = null) =>
            _inner.UpdateRange(minTime, maxTime, minPrice, maxPrice, newCanvasWidth, newCanvasHeight);
        public void SetTimeMap(System.Collections.Generic.IReadOnlyList<DateTime> timeMap) => _inner.SetTimeMap(timeMap);
        public System.Collections.Generic.IReadOnlyList<DateTime>? TimeMap => _inner.TimeMap;
        public double CanvasWidth => _inner.CanvasWidth;
        public double CanvasHeight => _inner.CanvasHeight;
        public Rect ScreenRect => _inner.ScreenRect;
        public double ViewportX => _inner.ViewportX;
        public double ViewportWidth => _inner.ViewportWidth;
        public double ScaleX => _inner.ScaleX;
        public double GetXFromIndex(double index) => _inner.GetXFromIndex(index);
        public double GetYFromPrice(decimal price) => _inner.GetYFromPrice(price);
        public PriceScaleType PriceScale => _inner.PriceScale;
        public TransformMetadata Metadata => _inner.Metadata;
    }

    [Fact]
    public void HandlePointerPressed_TransformException_CleansUpToken_NextStrokeSucceeds()
    {
        var adapter = new FreehandPointerAdapter();
        var viewModel = new ChartViewModel { Symbol = "AAPL", CurrentTool = DrawingTool.Freehand };
        viewModel.UpdateDrawingCoordinator();
        Assert.NotNull(viewModel.DrawingEditCoordinator);

        var failingTransform = new FailingTransform { ShouldThrow = true };
        var normalTransform = CreateTransform();

        // Stroke 1: ScreenToChart throws during HandlePointerPressed
        Assert.Throws<InvalidOperationException>(() =>
            adapter.HandlePointerPressed(new Point(10, 10), new Point(10, 10), 1, viewModel, failingTransform, -1, PanelKey.Main));

        Assert.False(adapter.IsActive);
        Assert.Null(adapter.PreviewObject);

        // Stroke 2: Next stroke with normal transform must NOT fail with Busy or token leak!
        bool started = adapter.HandlePointerPressed(new Point(20, 20), new Point(20, 20), 1, viewModel, normalTransform, -1, PanelKey.Main);
        Assert.True(started);
        Assert.True(adapter.IsActive);

        adapter.HandlePointerReleased(new Point(30, 30), new Point(30, 30), 1, viewModel, normalTransform, -1, PanelKey.Main, out _);
        Assert.Single(viewModel.ObjectManager.Objects);
        Assert.False(adapter.IsActive);
    }

    [Fact]
    public void HandlePointerReleased_WhenReleaseCatchesException_CoordinatorNotLeftBusy()
    {
        var adapter = new FreehandPointerAdapter();
        var viewModel = new ChartViewModel { Symbol = "AAPL", CurrentTool = DrawingTool.Freehand };
        viewModel.UpdateDrawingCoordinator();
        var normalTransform = CreateTransform();
        var failingTransform = new FailingTransform { ShouldThrow = false };

        // Start stroke normally
        bool started = adapter.HandlePointerPressed(new Point(10, 10), new Point(10, 10), 1, viewModel, failingTransform, -1, PanelKey.Main);
        Assert.True(started);
        Assert.True(adapter.IsActive);
        Assert.True(viewModel.DrawingEditCoordinator!.IsEditing);

        // Make transform fail during Release
        failingTransform.ShouldThrow = true;
        bool released = adapter.HandlePointerReleased(new Point(20, 20), new Point(20, 20), 1, viewModel, failingTransform, -1, PanelKey.Main, out _);
        Assert.True(released);
        Assert.False(adapter.IsActive);
        Assert.False(viewModel.DrawingEditCoordinator.IsEditing);

        // Next stroke must be able to start and succeed cleanly without Busy!
        failingTransform.ShouldThrow = false;
        bool nextStarted = adapter.HandlePointerPressed(new Point(30, 30), new Point(30, 30), 1, viewModel, failingTransform, -1, PanelKey.Main);
        Assert.True(nextStarted);
        Assert.True(adapter.IsActive);
        Assert.True(viewModel.DrawingEditCoordinator.IsEditing);

        adapter.HandlePointerReleased(new Point(40, 40), new Point(40, 40), 1, viewModel, failingTransform, -1, PanelKey.Main, out _);
        Assert.Single(viewModel.ObjectManager.Objects);
    }
}
