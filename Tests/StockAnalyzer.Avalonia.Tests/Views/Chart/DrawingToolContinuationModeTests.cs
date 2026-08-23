using System;
using Avalonia.Input;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Chart;

/// <summary>
/// Settings -> Chart -> Drawing: "After Finishing a Shape" (DrawingToolContinuationMode).
/// Default (ReturnToPointer) reverts CurrentTool to Pointer once a fixed-point tool finishes;
/// ContinueDrawing keeps it active so the next click starts a brand-new shape at that click.
/// </summary>
[Collection("DrawingThemeContext State")]
public class DrawingToolContinuationModeTests : IDisposable
{
    private static LinearCoordinateTransform MakeTransform()
        => new LinearCoordinateTransform(
            new DateTime(2024, 1, 1), new DateTime(2024, 1, 10),
            0m, 100m, 800, 600);

    public void Dispose()
    {
        // DrawingThemeContext is process-wide static state; restore the default so other tests
        // in this collection aren't affected by whichever mode this test last set.
        DrawingThemeContext.SetDrawingToolContinuationModeForTesting(DrawingToolContinuationMode.ReturnToPointer);
    }

    [Fact]
    public void TwoStepTool_ReturnToPointer_RevertsCurrentToolAfterFinishing()
    {
        DrawingThemeContext.SetDrawingToolContinuationModeForTesting(DrawingToolContinuationMode.ReturnToPointer);

        var controller = new ChartInteractionController();
        var viewModel = new ChartViewModel { CurrentTool = DrawingTool.TrendLine };
        var transform = MakeTransform();

        controller.HandlePointerPressed(new global::Avalonia.Point(100, 100), new global::Avalonia.Point(100, 100), viewModel, transform, KeyModifiers.None, 1);
        controller.HandlePointerPressed(new global::Avalonia.Point(200, 100), new global::Avalonia.Point(200, 100), viewModel, transform, KeyModifiers.None, 1);

        Assert.Equal(DrawingTool.Pointer, viewModel.CurrentTool);
        Assert.Single(viewModel.ObjectManager.Objects);
    }

    [Fact]
    public void TwoStepTool_ContinueDrawing_KeepsToolActiveAndStartsNextShapeAtNewClick()
    {
        DrawingThemeContext.SetDrawingToolContinuationModeForTesting(DrawingToolContinuationMode.ContinueDrawing);

        var controller = new ChartInteractionController();
        var viewModel = new ChartViewModel { CurrentTool = DrawingTool.TrendLine };
        var transform = MakeTransform();

        // First shape: click at (100,100) then (200,100).
        controller.HandlePointerPressed(new global::Avalonia.Point(100, 100), new global::Avalonia.Point(100, 100), viewModel, transform, KeyModifiers.None, 1);
        controller.HandlePointerPressed(new global::Avalonia.Point(200, 100), new global::Avalonia.Point(200, 100), viewModel, transform, KeyModifiers.None, 1);

        Assert.Equal(DrawingTool.TrendLine, viewModel.CurrentTool);
        Assert.Single(viewModel.ObjectManager.Objects);
        var firstShapeEndPoint = viewModel.ObjectManager.Objects[0].Points[1];

        // Second shape starts fresh at the next click (400,300), NOT at the first shape's endpoint.
        controller.HandlePointerPressed(new global::Avalonia.Point(400, 300), new global::Avalonia.Point(400, 300), viewModel, transform, KeyModifiers.None, 1);
        controller.HandlePointerPressed(new global::Avalonia.Point(500, 300), new global::Avalonia.Point(500, 300), viewModel, transform, KeyModifiers.None, 1);

        Assert.Equal(DrawingTool.TrendLine, viewModel.CurrentTool);
        Assert.Equal(2, viewModel.ObjectManager.Objects.Count);
        var secondShapeStartPoint = viewModel.ObjectManager.Objects[1].Points[0];
        Assert.NotEqual(firstShapeEndPoint.Time, secondShapeStartPoint.Time);
    }

    [Fact]
    public void SingleStepTool_ContinueDrawing_KeepsToolActiveAfterPlacement()
    {
        DrawingThemeContext.SetDrawingToolContinuationModeForTesting(DrawingToolContinuationMode.ContinueDrawing);

        var controller = new ChartInteractionController();
        var viewModel = new ChartViewModel { CurrentTool = DrawingTool.HorizontalLine };
        var transform = MakeTransform();

        controller.HandlePointerPressed(new global::Avalonia.Point(100, 100), new global::Avalonia.Point(100, 100), viewModel, transform, KeyModifiers.None, 1);

        Assert.Equal(DrawingTool.HorizontalLine, viewModel.CurrentTool);
        Assert.Single(viewModel.ObjectManager.Objects);
    }

    // Formerly a regression test ("Angle Tool ignores Continue Drawing") for DragToDrawBehavior<T>
    // tools (FinishesOnRelease == true) finishing on HandlePointerReleased via a separate code path
    // that previously ignored the Continue Drawing setting. Removed (2-click unification effort,
    // Task 5): every DrawingTool now uses TwoClickBehavior<T>, so no production tool exercises the
    // release-finish path anymore and this test had no real subject left to point at. The
    // DragToDrawBehavior<T> base class and ChartInteractionController's FinishesOnRelease == true
    // branch are consequently unreachable dead code as of this task; left in place pending a
    // decision on removing them via a follow-up quality-improvement pass (not part of this task's scope).
}
