using System;
using StockAnalyzer.Avalonia.Drawing;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class FreehandInputControllerTests
{
    [Fact]
    public void Begin_FromIdle_StartsDrawing()
    {
        var controller = new FreehandInputController();
        var sample = new FreehandInputSample(10, 100, 200);
        var pt = new ChartPoint(DateTime.UtcNow, 150m);

        var result = controller.Begin(sample, pt);

        Assert.Equal(DrawingInputResult.Started, result);
        Assert.True(controller.IsDrawing);
        Assert.Equal(10, controller.OwnedPointerId);
        Assert.Equal(1, controller.CurrentPointCount);
    }

    [Fact]
    public void Begin_WhileAlreadyDrawing_ReturnsRejected()
    {
        var controller = new FreehandInputController();
        controller.Begin(new FreehandInputSample(1, 10, 10), new ChartPoint(DateTime.UtcNow, 100m));

        var result = controller.Begin(new FreehandInputSample(2, 20, 20), new ChartPoint(DateTime.UtcNow, 101m));

        Assert.Equal(DrawingInputResult.Rejected, result);
        Assert.Equal(1, controller.OwnedPointerId);
    }

    [Fact]
    public void Move_WithOwnerPointer_UpdatesPoints()
    {
        var controller = new FreehandInputController();
        controller.Begin(new FreehandInputSample(1, 10, 10), new ChartPoint(DateTime.UtcNow, 100m));

        var moveResult = controller.Move(new FreehandInputSample(1, 20, 20), new ChartPoint(DateTime.UtcNow.AddMinutes(1), 105m));

        Assert.Equal(DrawingInputResult.Updated, moveResult);
        Assert.Equal(2, controller.CurrentPointCount);
    }

    [Fact]
    public void Move_WithDifferentPointer_Ignored()
    {
        var controller = new FreehandInputController();
        controller.Begin(new FreehandInputSample(1, 10, 10), new ChartPoint(DateTime.UtcNow, 100m));

        var moveResult = controller.Move(new FreehandInputSample(2, 20, 20), new ChartPoint(DateTime.UtcNow.AddMinutes(1), 105m));

        Assert.Equal(DrawingInputResult.Ignored, moveResult);
        Assert.Equal(1, controller.CurrentPointCount);
    }

    [Fact]
    public void Move_WhenCapacityExceeded_CancelsStrokeEntirely()
    {
        var controller = new FreehandInputController(2); // capacity of 2
        controller.Begin(new FreehandInputSample(1, 10, 10), new ChartPoint(DateTime.UtcNow, 100m));
        controller.Move(new FreehandInputSample(1, 20, 20), new ChartPoint(DateTime.UtcNow, 101m));

        // 3rd point exceeds capacity
        var result = controller.Move(new FreehandInputSample(1, 30, 30), new ChartPoint(DateTime.UtcNow, 102m));

        Assert.Equal(DrawingInputResult.Cancelled, result);
        Assert.False(controller.IsDrawing);
        Assert.Equal(0, controller.CurrentPointCount);
        Assert.Equal(DrawingCancelReason.CapacityExceeded, controller.LastCancelReason);
    }

    [Fact]
    public void Release_WithOwnerPointer_CommitsAndResetsToIdle()
    {
        var controller = new FreehandInputController();
        controller.Begin(new FreehandInputSample(1, 10, 10), new ChartPoint(DateTime.UtcNow, 100m));
        controller.Move(new FreehandInputSample(1, 20, 20), new ChartPoint(DateTime.UtcNow, 105m));

        var releaseResult = controller.Release(new FreehandInputSample(1, 30, 30), new ChartPoint(DateTime.UtcNow, 110m));

        Assert.Equal(DrawingInputResult.Committed, releaseResult);
        Assert.False(controller.IsDrawing);
    }

    [Fact]
    public void Release_WithDifferentPointer_Ignored()
    {
        var controller = new FreehandInputController();
        controller.Begin(new FreehandInputSample(1, 10, 10), new ChartPoint(DateTime.UtcNow, 100m));

        var result = controller.Release(new FreehandInputSample(99, 20, 20), new ChartPoint(DateTime.UtcNow, 105m));

        Assert.Equal(DrawingInputResult.Ignored, result);
        Assert.True(controller.IsDrawing);
    }

    [Fact]
    public void Cancel_ClearsBufferAndResetsToIdle()
    {
        var controller = new FreehandInputController();
        controller.Begin(new FreehandInputSample(1, 10, 10), new ChartPoint(DateTime.UtcNow, 100m));
        controller.Move(new FreehandInputSample(1, 20, 20), new ChartPoint(DateTime.UtcNow, 105m));

        var cancelResult = controller.Cancel(DrawingCancelReason.CaptureLost);

        Assert.Equal(DrawingInputResult.Cancelled, cancelResult);
        Assert.False(controller.IsDrawing);
        Assert.Equal(0, controller.CurrentPointCount);
        Assert.Equal(DrawingCancelReason.CaptureLost, controller.LastCancelReason);
    }

    [Fact]
    public void CompleteByModifier_CommitsCurrentPoints()
    {
        var controller = new FreehandInputController();
        controller.Begin(new FreehandInputSample(1, 10, 10), new ChartPoint(DateTime.UtcNow, 100m));
        controller.Move(new FreehandInputSample(1, 20, 20), new ChartPoint(DateTime.UtcNow, 105m));

        var result = controller.CompleteByModifier();

        Assert.Equal(DrawingInputResult.Committed, result);
        Assert.False(controller.IsDrawing);
        Assert.Equal(2, controller.CurrentPointCount);
    }

    [Fact]
    public void AllPoints_OrderAndDuplicatesPreserved_IncludingReverseDirection()
    {
        var controller = new FreehandInputController();
        var t0 = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddMinutes(5);
        var t2 = t0.AddMinutes(2); // reverse time direction (Time(P2) < Time(P1))
        var t3 = t0.AddMinutes(10);

        var p0 = new ChartPoint(t0, 100m);
        var p1 = new ChartPoint(t1, 105m);
        var p1Duplicate = new ChartPoint(t1, 105m);
        var p2 = new ChartPoint(t2, 103m);
        var p3 = new ChartPoint(t3, 110m);

        controller.Begin(new FreehandInputSample(1, 0, 0), p0);
        controller.Move(new FreehandInputSample(1, 1, 1), p1);
        controller.Move(new FreehandInputSample(1, 2, 2), p1Duplicate);
        controller.Move(new FreehandInputSample(1, 3, 3), p2);
        controller.Release(new FreehandInputSample(1, 4, 4), p3);

        var points = controller.GetCurrentPoints().ToArray();
        Assert.Equal(5, points.Length);
        Assert.Equal(p0.Time, points[0].Time);
        Assert.Equal(p1.Time, points[1].Time);
        Assert.Equal(p1Duplicate.Time, points[2].Time);
        Assert.Equal(p2.Time, points[3].Time); // reverse time preserved
        Assert.Equal(p3.Time, points[4].Time);
    }
}
