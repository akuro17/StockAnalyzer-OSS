using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Input;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models;
using Xunit;
using Point = global::Avalonia.Point;
using Rect = global::Avalonia.Rect;

namespace StockAnalyzer.Tests.Drawing;

/// <summary>
/// Reported: "Trend Line / Line Text" used a click-and-hold-to-draw gesture (click point 1
/// while holding the mouse button, drag, release to fix point 2) instead of the two-click
/// gesture (click point 1, move the mouse freely with the line following live, click point
/// 2 to finish) used by other line tools. Fixed by converting TrendLineBehavior /
/// LineTextBehavior from DragToDrawBehavior&lt;T&gt; to TwoClickBehavior&lt;T&gt;, mirroring
/// item ③'s identical conversion for RegressionTrend/FixedRangeVolumeProfile earlier this
/// session. Unlike those two tools, TrendLineObject/LineTextObject draw their line directly
/// from Points[] with no candle-data extraction step, so no additional "not enough data yet"
/// preview fallback was needed -- the line is already visible from the very first frame.
///
/// These tests drive the real ChartInteractionController end-to-end to prove the actual
/// press/release/press sequence now behaves as a two-click gesture: releasing right after
/// the first click must NOT finish the drawing (unlike the old drag-to-draw behavior, where
/// release always finished it), and a second click is what finishes it.
/// </summary>
public class TrendLineLineTextTwoClickConversionTests
{
    private static List<CoreCandleData> BuildCandles()
    {
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 10; i++)
        {
            decimal close = 100m + i * 10m;
            candles.Add(new CoreCandleData(new DateTime(2025, 1, 1).AddDays(i), close, close + 5, close - 5, close, 1000));
        }
        return candles;
    }

    [Theory]
    [InlineData(DrawingTool.TrendLine)]
    [InlineData(DrawingTool.LineText)]
    public void TwoClickGesture_PressReleaseAfterFirstClick_DoesNotFinishDrawing_SecondClickDoes(DrawingTool tool)
    {
        var controller = new ChartInteractionController(new MagnetSnapService(), new DialogService(), new SmartGuideService());
        var candles = BuildCandles();
        var viewModel = new ChartViewModel { CurrentTool = tool, Candles = candles };

        var t = new LinearCoordinateTransform(new DateTime(2025, 1, 1), new DateTime(2025, 1, 11), 0m, 200m, 1000, 600);
        var snapshot = new ChartDataSnapshot(candles);
        var bounds = new Rect(0, 0, 1000, 600);

        // Click 1: press AND release, simulating a plain click (not a click-and-hold).
        var clickPoint = new Point(100, 400);
        bool pressed = controller.HandlePointerPressed(clickPoint, clickPoint, viewModel, t, KeyModifiers.None, 1);
        Assert.True(pressed);
        Assert.True(controller.IsDrawingNewShape);
        Assert.NotNull(controller.CurrentDrawingObject);

        bool releaseHandled = controller.HandlePointerReleased(viewModel);
        // With the old drag-to-draw behavior, release always finished the drawing
        // (IsDrawingNewShape -> false). With the two-click behavior, release after just
        // the first click must be a no-op for the in-progress drawing.
        Assert.True(controller.IsDrawingNewShape, $"[{tool}] Releasing right after the first click must NOT finish a two-click drawing.");
        Assert.NotNull(controller.CurrentDrawingObject);

        // Moving the mouse (without holding any button) must live-update point 2.
        var movePoint = new Point(500, 200);
        controller.HandlePointerMoved(movePoint, movePoint, viewModel, t, snapshot, bounds, 0, 0, out _, KeyModifiers.None);
        var movedChartPoint = t.ScreenToChart(movePoint);
        Assert.Equal(movedChartPoint.Time, controller.CurrentDrawingObject!.Points[1].Time);

        // Click 2: this is what finishes the drawing.
        var secondClickPoint = new Point(500, 200);
        bool secondPressed = controller.HandlePointerPressed(secondClickPoint, secondClickPoint, viewModel, t, KeyModifiers.None, 1);
        Assert.True(secondPressed);
        Assert.False(controller.IsDrawingNewShape, $"[{tool}] The second click must finish the two-click drawing.");
        Assert.Single(viewModel.ObjectManager.Objects);
    }
}
