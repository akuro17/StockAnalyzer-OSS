using System;
using System.Collections.Generic;
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
/// Regression test for RangeSplineObject's control-point handle drag on an EXISTING
/// (already-placed, already-selected) object -- distinct from the two-click creation
/// preview covered by TwoClickCreationPreviewReproTests.
///
/// Two root causes were fixed here, discovered in two rounds:
///
/// Round 1: DrawGeometry() always rendered the spline curve from _extractedPoints, which
/// is only refreshed by Recalculate(). ChartInteractionController.HandleObjectDrag
/// deferred Recalculate() for RangeSplineObject to HandlePointerReleased (to avoid
/// per-PointerMoved-frame recompute causing drag stutter -- the same performance change
/// that originally caused RegressionTrendObject's "①-B" bug). Unlike RegressionTrendObject,
/// RangeSplineObject had no "stale" detection at all: the control-point handle circles
/// (drawn straight from live Points[] via DrawControlPointHandles) tracked the cursor, but
/// the spline curve stayed frozen at its pre-drag shape until release. Fixed by adding an
/// isStale check to DrawGeometry(), mirroring RegressionTrendObject's ①-B fallback: draw a
/// raw straight-line preview from live Points[] while stale.
///
/// Round 2: that raw straight-line preview turned out to be an incomplete fix -- it made
/// something track the drag live, but that something was a straight line standing in for
/// the curve, not the curve itself, so real-world testing on ranges with many candles
/// still looked like "just a line" throughout the drag. The original deferral was overly
/// broad: it lumped RangeSplineObject in with RegressionTrendObject/FRVP, but unlike
/// those two, RangeSplineObject.Recalculate() is a cheap O(log N) binary-search range
/// lookup -- cheap enough to run on every drag frame -- as long as it's given the real
/// IReadOnlyList&lt;CoreCandleData&gt; snapshot rather than the LINQ .Select()-wrapped
/// IEnumerable used at release time (which silently falls back to an O(N) linear scan and
/// was the actual source of the original stutter concern). Fixed by having
/// HandleObjectDrag call Recalculate(snapshot.Candles) live during a RangeSplineObject
/// handle drag, so the real curve (not a placeholder) now tracks the cursor.
/// </summary>
public class RangeSplineHandleDragLiveUpdateTests
{
    private static List<CoreCandleData> BuildCandles()
    {
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 30; i++)
        {
            decimal close = 100m + i * 5m;
            candles.Add(new CoreCandleData(new DateTime(2025, 1, 1).AddDays(i), close, close + 5, close - 5, close, 1000));
        }
        return candles;
    }

    private static int FindMinXOfColor(SKBitmap bitmap, SKColor color)
    {
        for (int x = 0; x < bitmap.Width; x++)
        for (int y = 0; y < bitmap.Height; y++)
        {
            if (bitmap.GetPixel(x, y) == color) return x;
        }
        return -1;
    }

    [Fact]
    public void HandleDrag_OnExistingSelectedObject_CurveFollowsLiveDragPosition()
    {
        var controller = new ChartInteractionController(new MagnetSnapService(), new DialogService(), new SmartGuideService());
        var candles = BuildCandles();
        var viewModel = new ChartViewModel { CurrentTool = DrawingTool.Pointer, Candles = candles };

        var t = new LinearCoordinateTransform(
            new DateTime(2025, 1, 1), new DateTime(2025, 1, 31), 0m, 300m, 1000, 600);

        var obj = new RangeSplineObject(new ChartPoint(new DateTime(2025, 1, 10), 100m), new ChartPoint(new DateTime(2025, 1, 15), 100m));
        // Explicit override (independent of Settings -> Chart -> Drawings -> Line Thickness):
        // exact-color pixel matching below needs a stroke wide enough to guarantee a
        // non-anti-aliased pixel, regardless of whatever the global default happens to be.
        obj.Thickness = 2.0;
        var coreCandles = candles.ConvertAll(c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));
        obj.Recalculate(coreCandles);
        Assert.True(obj.ExtractedPoints.Count >= 3);

        viewModel.ObjectManager.AddObject(obj);
        viewModel.ObjectManager.SelectObject(obj.Id);

        var curveColor = obj.SkiaColor;

        using var bitmapBefore = new SKBitmap(1000, 600);
        using (var canvasBefore = new SKCanvas(bitmapBefore))
        {
            canvasBefore.Clear(SKColors.Transparent);
            obj.Render(canvasBefore, t);
            canvasBefore.Flush();
        }
        int minXBefore = FindMinXOfColor(bitmapBefore, curveColor);
        Assert.True(minXBefore >= 0, "Expected to find the spline curve color before drag.");

        var handleScreenPos = t.ChartToScreen(obj.Points[0]);
        var clickPoint = new Point(handleScreenPos.X, handleScreenPos.Y);
        bool pressed = controller.HandlePointerPressed(clickPoint, clickPoint, viewModel, t, KeyModifiers.None, 1);
        Assert.True(pressed);
        Assert.Equal(0, controller.DraggedHandleIndex);

        // Drag Points[0] far to the left (much earlier time). If the curve tracks the
        // drag live, its left edge should extend well to the left; if frozen (stale), it
        // stays exactly where it was before the drag started.
        var snapshot = new ChartDataSnapshot(candles);
        var bounds = new Rect(0, 0, 1000, 600);
        var farLeftChartPoint = new ChartPoint(new DateTime(2025, 1, 2), 100m);
        var movePoint = t.ChartToScreen(farLeftChartPoint);
        bool moveHandled = controller.HandlePointerMoved(movePoint, movePoint, viewModel, t, snapshot, bounds, 0, 0, out _, KeyModifiers.None);
        Assert.True(moveHandled);
        Assert.Equal(farLeftChartPoint.Time, obj.Points[0].Time);

        using var bitmapAfter = new SKBitmap(1000, 600);
        using (var canvasAfter = new SKCanvas(bitmapAfter))
        {
            canvasAfter.Clear(SKColors.Transparent);
            obj.Render(canvasAfter, t);
            canvasAfter.Flush();
        }
        int minXAfter = FindMinXOfColor(bitmapAfter, curveColor);
        Assert.True(minXAfter >= 0, "Expected to find the spline curve color after drag.");

        Assert.True(minXAfter < minXBefore - 50,
            $"Expected the spline curve's left edge to move left in real time while dragging Points[0] far earlier. Before={minXBefore}, After={minXAfter}");
    }

    private static List<CoreCandleData> BuildNonLinearCandles()
    {
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 30; i++)
        {
            // Pseudo-random (non-symmetric) prices so a real spline curve is
            // unambiguously distinguishable from a straight line, regardless of which
            // two days happen to be chosen as endpoints.
            decimal close = 100m + (i * 37 % 53);
            candles.Add(new CoreCandleData(new DateTime(2025, 1, 1).AddDays(i), close, close + 5, close - 5, close, 1000));
        }
        return candles;
    }

    private static double MaxDeviationFromStraightLine(SKBitmap bitmap, SKColor curveColor, Point p0, Point p1)
    {
        int xStart = (int)Math.Min(p0.X, p1.X);
        int xEnd = (int)Math.Max(p0.X, p1.X);
        double slope = (p1.Y - p0.Y) / (p1.X - p0.X);
        double maxDev = -1;
        for (int x = xStart; x <= xEnd; x++)
        {
            if (x < 0 || x >= bitmap.Width) continue;
            double straightY = p0.Y + slope * (x - p0.X);
            for (int y = 0; y < bitmap.Height; y++)
            {
                if (bitmap.GetPixel(x, y) == curveColor)
                {
                    double dev = Math.Abs(y - straightY);
                    if (dev > maxDev) maxDev = dev;
                    break;
                }
            }
        }
        return maxDev;
    }

    [Fact]
    public void HandleDrag_OnExistingSelectedObject_RendersARealCurve_NotAStraightLinePlaceholder()
    {
        var controller = new ChartInteractionController(new MagnetSnapService(), new DialogService(), new SmartGuideService());
        var candles = BuildNonLinearCandles();
        var viewModel = new ChartViewModel { CurrentTool = DrawingTool.Pointer, Candles = candles };

        var t = new LinearCoordinateTransform(
            new DateTime(2025, 1, 1), new DateTime(2025, 1, 31), 0m, 300m, 1000, 600);

        var obj = new RangeSplineObject(new ChartPoint(new DateTime(2025, 1, 5), 100m), new ChartPoint(new DateTime(2025, 1, 20), 100m));
        // Explicit override (independent of Settings -> Chart -> Drawings -> Line Thickness):
        // exact-color pixel matching below needs a stroke wide enough to guarantee a
        // non-anti-aliased pixel, regardless of whatever the global default happens to be.
        obj.Thickness = 2.0;
        var coreCandles = candles.ConvertAll(c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));
        obj.Recalculate(coreCandles);
        Assert.True(obj.ExtractedPoints.Count >= 10);

        viewModel.ObjectManager.AddObject(obj);
        viewModel.ObjectManager.SelectObject(obj.Id);

        var curveColor = obj.SkiaColor;
        var handleScreenPos = t.ChartToScreen(obj.Points[0]);
        var clickPoint = new Point(handleScreenPos.X, handleScreenPos.Y);
        bool pressed = controller.HandlePointerPressed(clickPoint, clickPoint, viewModel, t, KeyModifiers.None, 1);
        Assert.True(pressed);

        // Drag Points[0], but still leave many candles in the selected range (day8..day20)
        // -- this is the "many candles, still a straight line" scenario reported.
        var snapshot = new ChartDataSnapshot(candles);
        var bounds = new Rect(0, 0, 1000, 600);
        var movePoint = t.ChartToScreen(new ChartPoint(new DateTime(2025, 1, 8), 100m));
        controller.HandlePointerMoved(movePoint, movePoint, viewModel, t, snapshot, bounds, 0, 0, out _, KeyModifiers.None);
        Assert.True(obj.ExtractedPoints.Count >= 8, $"Expected many candles still in range mid-drag, got {obj.ExtractedPoints.Count}");

        using var bitmap = new SKBitmap(1000, 600);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            obj.Render(canvas, t);
            canvas.Flush();
        }

        var p0 = t.ChartToScreen(obj.Points[0]);
        var p1 = t.ChartToScreen(obj.Points[1]);
        double maxDev = MaxDeviationFromStraightLine(bitmap, curveColor, new Point(p0.X, p0.Y), new Point(p1.X, p1.Y));

        Assert.True(maxDev >= 15,
            $"Expected the curve rendered DURING the drag to actually deviate from a straight Points[0]-Points[1] line (a real recalculated spline), but maxDev={maxDev}. ExtractedCount={obj.ExtractedPoints.Count}");
    }
}
