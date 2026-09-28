using Avalonia.Input;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart;
using System;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Chart;

public class ChartInteractionControllerDoubleTapTests
{
    private static LinearCoordinateTransform MakeTransform()
        => new LinearCoordinateTransform(
            new DateTime(2024, 1, 1), new DateTime(2024, 1, 2),
            0m, 100m, 800, 600);

    // Regression test: a Shift+Click that lands within Avalonia's double-tap
    // gesture window (e.g. shortly after a prior click on the same object) must
    // never open the drawing settings/edit dialog — Shift is reserved for the
    // delete action. Previously HandleDoubleTap ignored modifier keys entirely,
    // so this scenario opened the editor instead of deleting the object.
    [Theory]
    [InlineData(KeyModifiers.Shift)]
    [InlineData(KeyModifiers.Control)]
    public void HandleDoubleTap_DeleteOrCancelModifierHeld_ReturnsFalse(KeyModifiers modifiers)
    {
        var controller = new ChartInteractionController();
        var viewModel = new ChartViewModel();
        var transform = MakeTransform();

        var result = controller.HandleDoubleTap(
            new global::Avalonia.Point(10, 10),
            new global::Avalonia.Point(10, 10),
            viewModel,
            transform,
            snapshot: null!,
            modifiers: modifiers);

        Assert.False(result);
    }

    [Fact]
    public void HandleDoubleTap_NoModifiers_ViewModelNull_ReturnsFalse()
    {
        // Baseline sanity check that the method still behaves for the ordinary
        // (no-modifier) path when there is nothing to edit.
        var controller = new ChartInteractionController();
        var transform = MakeTransform();

        var result = controller.HandleDoubleTap(
            new global::Avalonia.Point(10, 10),
            new global::Avalonia.Point(10, 10),
            viewModel: null,
            transform,
            snapshot: null!,
            modifiers: KeyModifiers.None);

        Assert.False(result);
    }

    [Fact]
    public void HandleDoubleTap_CancelsPendingDragTransaction_AllowsDrawingSettingsDialogEdit()
    {
        var controller = new ChartInteractionController();
        var viewModel = new ChartViewModel();
        viewModel.Symbol = "TEST";
        viewModel.UpdateDrawingCoordinator();
        Assert.NotNull(viewModel.DrawingEditCoordinator);

        var transform = MakeTransform();
        var p1 = new ChartPoint(new DateTime(2024, 1, 1, 6, 0, 0), 50m);
        var p2 = new ChartPoint(new DateTime(2024, 1, 1, 18, 0, 0), 50m);
        var frvp = new FixedRangeVolumeProfileObject(p1, p2);
        viewModel.ObjectManager.AddObject(frvp);

        var midTime = new DateTime(2024, 1, 1, 12, 0, 0);
        var screenPt = transform.ChartToScreen(new ChartPoint(midTime, 50m));
        var point = new global::Avalonia.Point(screenPt.X, screenPt.Y);

        // 1. PointerPressed on object initiates a move drag transaction
        controller.HandlePointerPressed(
            point,
            point,
            viewModel,
            transform,
            KeyModifiers.None,
            clickCount: 1);

        Assert.True(viewModel.DrawingEditCoordinator.IsEditing);
        Assert.Same(frvp, controller.DraggedObject);

        // 2. DoubleTap occurs on the object (as when opening settings dialog)
        var handled = controller.HandleDoubleTap(
            point,
            point,
            viewModel,
            transform,
            snapshot: null!,
            modifiers: KeyModifiers.None);

        Assert.True(handled);

        // Active drag transaction must be cancelled and dragged object cleared
        Assert.False(viewModel.DrawingEditCoordinator.IsEditing);
        Assert.Null(controller.DraggedObject);

        // A subsequent dialog edit transaction (SettingsApply) must succeed rather than fail as busy
        var beginResult = viewModel.DrawingEditCoordinator.BeginEdit(StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.SettingsApply);
        Assert.True(beginResult.IsSuccess);
        viewModel.DrawingEditCoordinator.Cancel(beginResult.Token);
    }

    [Fact]
    public void HandleDoubleTap_FftProjection_CancelsPendingDragTransaction_WithoutCrash()
    {
        var controller = new ChartInteractionController();
        var viewModel = new ChartViewModel();
        viewModel.Symbol = "TEST";
        viewModel.UpdateDrawingCoordinator();
        Assert.NotNull(viewModel.DrawingEditCoordinator);

        var transform = MakeTransform();
        var p1 = new ChartPoint(new DateTime(2024, 1, 1, 6, 0, 0), 50m);
        var p2 = new ChartPoint(new DateTime(2024, 1, 1, 18, 0, 0), 50m);
        var fft = new StockAnalyzer.Avalonia.Drawing.Objects.FftProjectionObject();
        fft.Points.Add(p1);
        fft.Points.Add(p2);
        viewModel.ObjectManager.AddObject(fft);

        var midTime = new DateTime(2024, 1, 1, 12, 0, 0);
        var screenPt = transform.ChartToScreen(new ChartPoint(midTime, 50m));
        var point = new global::Avalonia.Point(screenPt.X, screenPt.Y);

        // 1. First click: PointerPressed initiates a move drag transaction (captures document before state)
        controller.HandlePointerPressed(
            point,
            point,
            viewModel,
            transform,
            KeyModifiers.None,
            clickCount: 1);

        Assert.True(viewModel.DrawingEditCoordinator.IsEditing);

        // 2. Second click: DoubleTap cancels the active drag transaction (restores state via Materialize)
        var handled = controller.HandleDoubleTap(
            point,
            point,
            viewModel,
            transform,
            snapshot: null!,
            modifiers: KeyModifiers.None);

        Assert.True(handled);
        Assert.False(viewModel.DrawingEditCoordinator.IsEditing);
    }

    [Fact]
    public void HandleDoubleTap_DtwProjection_CancelsPendingDragTransaction_WithoutCrash()
    {
        var controller = new ChartInteractionController();
        var viewModel = new ChartViewModel();
        viewModel.Symbol = "TEST";
        viewModel.UpdateDrawingCoordinator();
        Assert.NotNull(viewModel.DrawingEditCoordinator);

        var transform = MakeTransform();
        var p1 = new ChartPoint(new DateTime(2024, 1, 1, 6, 0, 0), 50m);
        var p2 = new ChartPoint(new DateTime(2024, 1, 1, 18, 0, 0), 50m);
        var dtw = new StockAnalyzer.Avalonia.Drawing.Objects.DtwProjectionObject();
        dtw.Points.Add(p1);
        dtw.Points.Add(p2);
        dtw.ProjectedPath.Add(new StockAnalyzer.Core.Models.Point(100, 55.0));
        dtw.MatchedStartTime = DateTime.UtcNow;
        dtw.MatchedEndTime = DateTime.UtcNow.AddDays(5);
        viewModel.ObjectManager.AddObject(dtw);

        var midTime = new DateTime(2024, 1, 1, 12, 0, 0);
        var screenPt = transform.ChartToScreen(new ChartPoint(midTime, 50m));
        var point = new global::Avalonia.Point(screenPt.X, screenPt.Y);

        // 1. PointerPressed initiates drag
        controller.HandlePointerPressed(
            point,
            point,
            viewModel,
            transform,
            KeyModifiers.None,
            clickCount: 1);

        Assert.True(viewModel.DrawingEditCoordinator.IsEditing);

        // 2. DoubleTap cancels drag and materializes
        var handled = controller.HandleDoubleTap(
            point,
            point,
            viewModel,
            transform,
            snapshot: null!,
            modifiers: KeyModifiers.None);

        Assert.True(handled);
        Assert.False(viewModel.DrawingEditCoordinator.IsEditing);
    }
}
