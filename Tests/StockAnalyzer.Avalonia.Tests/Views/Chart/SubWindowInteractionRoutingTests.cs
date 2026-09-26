using System;
using Avalonia.Input;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models;
using Xunit;
using Point = Avalonia.Point;

namespace StockAnalyzer.Avalonia.Tests.Views.Chart;

/// <summary>
/// T5a/T5b of "SubWindow Drawing Tools": ChartInteractionController's pointer entry points accept an
/// activePanelIndex (resolved by ChartBaseControl from the panel under the pointer). New objects are
/// stamped with it and pick/hit-testing is scoped to it. Default (-1) keeps main-chart behavior.
/// </summary>
public class SubWindowInteractionRoutingTests
{
    private static LinearCoordinateTransform MakeTransform()
        => new(new DateTime(2024, 1, 1), new DateTime(2024, 1, 10), 0m, 100m, 800, 600);

    [Fact]
    public void TwoClickShape_DefaultActivePanel_CreatesMainChartObject()
    {
        var controller = new ChartInteractionController();
        var viewModel = new ChartViewModel { CurrentTool = DrawingTool.TrendLine };
        var t = MakeTransform();

        controller.HandlePointerPressed(new Point(100, 100), new Point(100, 100), viewModel, t, KeyModifiers.None, 1);
        controller.HandlePointerPressed(new Point(200, 150), new Point(200, 150), viewModel, t, KeyModifiers.None, 1);

        Assert.Single(viewModel.ObjectManager.Objects);
        Assert.Equal(-1, viewModel.ObjectManager.Objects[0].PanelIndex);
    }

    [Fact]
    public void TwoClickShape_WithActivePanelIndex_StampsThatPanelOnTheCreatedObject()
    {
        var controller = new ChartInteractionController();
        var viewModel = new ChartViewModel { CurrentTool = DrawingTool.TrendLine };
        var t = MakeTransform();

        controller.HandlePointerPressed(new Point(100, 100), new Point(100, 100), viewModel, t, KeyModifiers.None, 1, activePanelIndex: 2);
        controller.HandlePointerPressed(new Point(200, 150), new Point(200, 150), viewModel, t, KeyModifiers.None, 1, activePanelIndex: 2);

        Assert.Single(viewModel.ObjectManager.Objects);
        Assert.Equal(2, viewModel.ObjectManager.Objects[0].PanelIndex);
    }

    [Fact]
    public void Pick_InPanel_SelectsOnlyObjectsOfThatPanel()
    {
        var controller = new ChartInteractionController();
        var viewModel = new ChartViewModel { CurrentTool = DrawingTool.Pointer };
        var t = MakeTransform();

        var onMain = new HorizontalLineObject(new ChartPoint(new DateTime(2024, 1, 5), 50m)) { PanelIndex = -1 };
        var onPanel0 = new HorizontalLineObject(new ChartPoint(new DateTime(2024, 1, 5), 50m)) { PanelIndex = 0 };
        var onPanel1 = new HorizontalLineObject(new ChartPoint(new DateTime(2024, 1, 5), 50m)) { PanelIndex = 1 };
        viewModel.ObjectManager.AddObject(onMain);
        viewModel.ObjectManager.AddObject(onPanel0);
        viewModel.ObjectManager.AddObject(onPanel1);

        double y = t.ChartToScreen(new ChartPoint(new DateTime(2024, 1, 5), 50m)).Y;
        var press = new Point(400, y);

        controller.HandlePointerPressed(press, press, viewModel, t, KeyModifiers.None, 1, activePanelIndex: 0);

        Assert.NotNull(viewModel.ObjectManager.SelectedObject);
        Assert.Equal(0, viewModel.ObjectManager.SelectedObject!.PanelIndex);
        Assert.Same(onPanel0, viewModel.ObjectManager.SelectedObject);
    }

    [Fact]
    public void Pick_OnMainChart_IgnoresPanelObjects()
    {
        var controller = new ChartInteractionController();
        var viewModel = new ChartViewModel { CurrentTool = DrawingTool.Pointer };
        var t = MakeTransform();

        var onPanel0 = new HorizontalLineObject(new ChartPoint(new DateTime(2024, 1, 5), 50m)) { PanelIndex = 0 };
        viewModel.ObjectManager.AddObject(onPanel0);

        double y = t.ChartToScreen(new ChartPoint(new DateTime(2024, 1, 5), 50m)).Y;
        var press = new Point(400, y);

        controller.HandlePointerPressed(press, press, viewModel, t, KeyModifiers.None, 1, activePanelIndex: -1);

        Assert.Null(viewModel.ObjectManager.SelectedObject);
    }

    [Fact]
    public void NewShapeUpdate_InSubPanel_SkipsCandleMagnetSnap()
    {
        var controller = new ChartInteractionController();
        var viewModel = new ChartViewModel { CurrentTool = DrawingTool.TrendLine };
        var t = MakeTransform();

        // Begin a two-click shape inside sub-panel 1.
        controller.HandlePointerPressed(new Point(100, 100), new Point(100, 100), viewModel, t, KeyModifiers.None, 1, activePanelIndex: 1);
        Assert.True(controller.IsDrawingNewShape);

        // A candle whose Close sits exactly under the pointer would snap on the main chart.
        var candles = new[] { new CoreCandleData(new DateTime(2024, 1, 5), 40m, 60m, 40m, 50m, 100L) };
        var closeScreen = t.ChartToScreen(new ChartPoint(new DateTime(2024, 1, 5), 50m));
        var pointerPos = new Point(closeScreen.X, closeScreen.Y);

        controller.UpdateNewShape(pointerPos, candles, t);

        Assert.Null(controller.LastSnapChartPoint);
        var committed = controller.CurrentDrawingObject!.Points[^1];
        var raw = t.ScreenToChart(pointerPos);
        Assert.Equal(raw.Price, committed.Price, precision: 4);
    }

    [Fact]
    public void NewShapeUpdate_OnMainChart_StillCandleMagnetSnaps()
    {
        var controller = new ChartInteractionController();
        var viewModel = new ChartViewModel { CurrentTool = DrawingTool.TrendLine };
        var t = MakeTransform();

        controller.HandlePointerPressed(new Point(100, 100), new Point(100, 100), viewModel, t, KeyModifiers.None, 1, activePanelIndex: -1);
        Assert.True(controller.IsDrawingNewShape);

        var candles = new[] { new CoreCandleData(new DateTime(2024, 1, 5), 40m, 60m, 40m, 50m, 100L) };
        var closeScreen = t.ChartToScreen(new ChartPoint(new DateTime(2024, 1, 5), 50m));

        controller.UpdateNewShape(new Point(closeScreen.X, closeScreen.Y), candles, t);

        Assert.NotNull(controller.LastSnapChartPoint);
    }

    [Fact]
    public void PointerMoved_AcceptsActivePanelIndex_WithoutDisturbingAnIdleMainChart()
    {
        var controller = new ChartInteractionController();
        var viewModel = new ChartViewModel { CurrentTool = DrawingTool.Pointer };
        var t = MakeTransform();

        bool handled = controller.HandlePointerMoved(
            new Point(120, 130), new Point(120, 130), viewModel, t, new ChartDataSnapshot(Array.Empty<CoreCandleData>()),
            new global::Avalonia.Rect(0, 0, 800, 600), 50, 0, out bool needsUpdate, KeyModifiers.None, activePanelIndex: 1);

        Assert.False(handled);
        Assert.Empty(viewModel.ObjectManager.Objects);
    }
}
