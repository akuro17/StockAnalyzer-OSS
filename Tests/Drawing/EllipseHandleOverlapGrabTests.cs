using System;
using Avalonia.Input;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart;
using Xunit;
using Point = global::Avalonia.Point;

namespace StockAnalyzer.Tests.Drawing;

/// <summary>
/// Regression test for the bug where dragging a circumference angle handle (Points[2]/[3]) to overlap
/// the corner handle (Points[1]) made it permanently ungrabbable afterward: ChartInteractionController's
/// handle hit-test loop scanned index 0 -> 3 and returned on the first match, so an overlapping
/// circumference handle was always shadowed by the corner handle underneath it (checked first, at a
/// lower index). The fix scans highest-index-first, matching the draw order (circumference handles are
/// rendered on top of center/corner), so the topmost handle wins an overlapping click.
/// </summary>
public class EllipseHandleOverlapGrabTests
{
    [Fact]
    public void ClickOnCircumferenceHandle_OverlappingCorner_StartsHandleDragOnCircumferenceHandle_NotCorner()
    {
        var controller = new ChartInteractionController(new MagnetSnapService(), new DialogService(), new SmartGuideService());
        var viewModel = new ChartViewModel { CurrentTool = DrawingTool.Pointer };

        var t = new LinearCoordinateTransform(
            new DateTime(2025, 1, 1), new DateTime(2025, 1, 21), 0m, 300m, 1000, 600);

        var center = t.ScreenToChart(new Point(200, 200));
        var corner = t.ScreenToChart(new Point(300, 200)); // due east, distance 100
        var obj = new EllipseObject(center, corner) { IsArcEnabled = true };

        // Drag the circumference handle (Points[2]) to overlap the corner's own screen position.
        var cornerScreen = t.ChartToScreen(obj.Points[1]);
        obj.Points[2] = t.ScreenToChart(cornerScreen);

        viewModel.ObjectManager.AddObject(obj);
        viewModel.ObjectManager.SelectObject(obj.Id);

        var handles = obj.GetSelectionHandleScreenPositions(t);
        Assert.Equal(handles[1], handles[2]); // sanity check: circumference handle now overlaps the corner

        bool pressed = controller.HandlePointerPressed(handles[2], handles[2], viewModel, t, KeyModifiers.None, 1);

        Assert.True(pressed, "Clicking the overlapping circumference handle should start a handle drag.");
        Assert.Same(obj, controller.DraggedObject);
        Assert.Equal(2, controller.DraggedHandleIndex);
    }
}
