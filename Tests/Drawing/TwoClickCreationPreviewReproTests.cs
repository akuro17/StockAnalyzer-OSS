using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Input;
using SkiaSharp;
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
/// Regression tests for Regression Trend / Range Spline / Fixed Range Volume Profile's
/// two-click creation preview. Root cause: each tool's control-point handle circles were
/// gated behind IsSelected, which is never true for CurrentDrawingObject (it only becomes
/// true once the finished object joins ChartObjectManager). During two-click creation the
/// "not enough data yet" fallback (drawn unconditionally) showed a marker right after the
/// first click, but as soon as the mouse moved far enough for the tool's underlying
/// calculation (regression fit / extracted spline / volume profile) to become valid, the
/// code fell through to the normal render path whose handles were IsSelected-gated -- so
/// the control-point circles vanished (only the line/curve/histogram remained), even
/// though Points[] itself was still being updated correctly every frame.
/// Drives the real ChartInteractionController end-to-end (PointerPressed then
/// PointerMoved) instead of manipulating object state directly, per sa_minimal_fix's
/// "prefer executable reproduction for interaction bugs" guidance.
/// </summary>
public class TwoClickCreationPreviewReproTests
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

    private static bool BitmapContainsColor(SKBitmap bitmap, SKColor color)
    {
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y) == color) return true;
            }
        }
        return false;
    }

    private static SKBitmap Render(IChartObject? obj, ICoordinateTransform t, int width, int height)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        obj?.Render(canvas, t);
        canvas.Flush();
        return bitmap;
    }

    [Theory]
    [InlineData(DrawingTool.RegressionTrend)]
    [InlineData(DrawingTool.RangeSpline)]
    [InlineData(DrawingTool.FixedRangeVolumeProfile)]
    public void TwoClickCreation_AfterFirstClick_ShowsMarker_AndKeepsShowingItAfterMovingToPoint2(DrawingTool tool)
    {
        var controller = new ChartInteractionController(new MagnetSnapService(), new DialogService(), new SmartGuideService());
        var candles = BuildCandles();
        var viewModel = new ChartViewModel
        {
            CurrentTool = tool,
            Candles = candles
        };

        var t = new LinearCoordinateTransform(
            new DateTime(2025, 1, 1), new DateTime(2025, 1, 11), 0m, 200m, 1000, 600);
        var snapshot = new ChartDataSnapshot(candles);
        var bounds = new Rect(0, 0, 1000, 600);

        var clickPoint = new Point(100, 400);
        bool pressed = controller.HandlePointerPressed(clickPoint, clickPoint, viewModel, t, KeyModifiers.None, 1);
        Assert.True(pressed);
        Assert.True(controller.IsDrawingNewShape);
        Assert.NotNull(controller.CurrentDrawingObject);

        using (var bitmapAfterClick = Render(controller.CurrentDrawingObject, t, 1000, 600))
        {
            Assert.True(BitmapContainsColor(bitmapAfterClick, SKColors.Red),
                $"[{tool}] Expected a visible marker/handle immediately after the first click.");
        }

        // Move far enough that the tool's underlying calculation (regression fit /
        // extracted spline / volume profile) becomes valid, which is exactly the moment
        // the pre-fix code fell through to the IsSelected-gated handle-drawing path.
        var movePoint = new Point(500, 200);
        controller.HandlePointerMoved(movePoint, movePoint, viewModel, t, snapshot, bounds, 0, 0, out _, KeyModifiers.None);

        var movedChartPoint = t.ScreenToChart(movePoint);
        Assert.Equal(movedChartPoint.Time, controller.CurrentDrawingObject!.Points[1].Time);

        using var bitmapAfterMove = Render(controller.CurrentDrawingObject, t, 1000, 600);
        Assert.True(BitmapContainsColor(bitmapAfterMove, SKColors.Red),
            $"[{tool}] Expected the control-point handle to remain visible while moving toward point 2.");
    }
}
