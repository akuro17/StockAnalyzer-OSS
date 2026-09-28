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

namespace StockAnalyzer.Tests.Drawing;

/// <summary>
/// Regression tests for FixedRangeVolumeProfileObject's control-point handle drag.
/// Root cause: Render()'s full (profile-available) path drew the two handle circles at
/// the axis-aligned bounding-box corners (left,top)/(right,bottom) computed from
/// Points[0]/Points[1], instead of at Points[0]/Points[1]'s own screen positions (which
/// is what the "not enough data yet" preview fallback already did, and what
/// ChartInteractionController's generic handle hit-test checks against -- it tests
/// distance to Points[i]'s screen position, not to wherever Render() happened to draw).
/// When the box is drawn "anti-diagonally" -- e.g. Points[0] at a lower price/earlier time
/// (bottom-left on screen) and Points[1] at a higher price/later time (top-right on
/// screen), a natural "select an uptrend range" gesture -- left/top and right/bottom are
/// synthesized corners that belong to neither raw point, so clicking the visibly-drawn
/// handle circle never registered as a hit on either Points[0] or Points[1] and a
/// handle-drag could never start (only the whole-object Move drag, which relies on
/// HitTest()'s bounding box rather than handle positions, still worked).
///
/// These tests find the ACTUAL rendered handle pixel (by scanning a bitmap for the
/// handle color, mirroring the pattern in TwoClickCreationPreviewReproTests) and click
/// exactly there, so they fail whenever Render() draws a handle somewhere the hit-test
/// can't reach -- rather than assuming any particular corner formula.
/// </summary>
public class FixedRangeVolumeProfileHandleDragTests
{
    private static List<CoreCandleData> BuildCandles()
    {
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 20; i++)
        {
            decimal close = 100m + i * 10m;
            candles.Add(new CoreCandleData(new DateTime(2025, 1, 1).AddDays(i), close, close + 5, close - 5, close, 1000));
        }
        return candles;
    }

    private static SKBitmap Render(FixedRangeVolumeProfileObject obj, ICoordinateTransform t, int width, int height)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        obj.Render(canvas, t);
        canvas.Flush();
        return bitmap;
    }

    private static Point? FindHandlePixelNear(SKBitmap bitmap, global::Avalonia.Point approx, int searchRadius = 30)
    {
        for (int dy = -searchRadius; dy <= searchRadius; dy++)
        for (int dx = -searchRadius; dx <= searchRadius; dx++)
        {
            int px = (int)approx.X + dx;
            int py = (int)approx.Y + dy;
            if (px < 0 || py < 0 || px >= bitmap.Width || py >= bitmap.Height) continue;
            // A handle can render in the default color (SKColors.Red) or, for the point matching
            // AnchorPointIndex (0 by default), in AnchorPointColor -- either still marks a handle.
            var pixel = bitmap.GetPixel(px, py);
            if (pixel == SKColors.Red || pixel == DrawingThemeContext.AnchorPointColor) return new Point(px, py);
        }
        return null;
    }

    [Fact]
    public void HandleDrag_AntiDiagonalBox_ClickOnActuallyRenderedHandle_StartsHandleDragOnPoint0()
    {
        var controller = new ChartInteractionController(new MagnetSnapService(), new DialogService(), new SmartGuideService());
        var candles = BuildCandles();
        var viewModel = new ChartViewModel { CurrentTool = DrawingTool.Pointer, Candles = candles };

        var t = new LinearCoordinateTransform(
            new DateTime(2025, 1, 1), new DateTime(2025, 1, 21), 0m, 300m, 1000, 600);

        // Anti-diagonal box: Points[0] = earlier time + LOWER price (bottom-left on
        // screen), Points[1] = later time + HIGHER price (top-right on screen).
        var obj = new FixedRangeVolumeProfileObject(
            new ChartPoint(new DateTime(2025, 1, 2), 100m),
            new ChartPoint(new DateTime(2025, 1, 10), 250m));
        var coreCandles = candles.ConvertAll(c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));
        obj.Recalculate(coreCandles);
        Assert.NotEmpty(obj.ProfileData);

        viewModel.ObjectManager.AddObject(obj);
        viewModel.ObjectManager.SelectObject(obj.Id);

        var p1Screen = t.ChartToScreen(obj.Points[0]);

        using var bitmap = Render(obj, t, 1000, 600);
        var renderedHandle = FindHandlePixelNear(bitmap, p1Screen);
        Assert.NotNull(renderedHandle);

        bool pressed = controller.HandlePointerPressed(renderedHandle!.Value, renderedHandle.Value, viewModel, t, KeyModifiers.None, 1);

        Assert.True(pressed, "Clicking exactly where Points[0]'s handle is visibly rendered should start a handle drag.");
        Assert.Same(obj, controller.DraggedObject);
        Assert.Equal(0, controller.DraggedHandleIndex);
    }

    [Fact]
    public void HandleDrag_MainDiagonalBox_ClickOnActuallyRenderedHandle_StartsHandleDragOnPoint1()
    {
        var controller = new ChartInteractionController(new MagnetSnapService(), new DialogService(), new SmartGuideService());
        var candles = BuildCandles();
        var viewModel = new ChartViewModel { CurrentTool = DrawingTool.Pointer, Candles = candles };

        var t = new LinearCoordinateTransform(
            new DateTime(2025, 1, 1), new DateTime(2025, 1, 21), 0m, 300m, 1000, 600);

        // Main diagonal box: Points[0] = earlier time + HIGHER price (top-left on screen),
        // Points[1] = later time + LOWER price (bottom-right on screen) -- guardrail so the
        // already-working orientation doesn't regress.
        var obj = new FixedRangeVolumeProfileObject(
            new ChartPoint(new DateTime(2025, 1, 2), 250m),
            new ChartPoint(new DateTime(2025, 1, 10), 100m));
        var coreCandles = candles.ConvertAll(c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));
        obj.Recalculate(coreCandles);
        Assert.NotEmpty(obj.ProfileData);

        viewModel.ObjectManager.AddObject(obj);
        viewModel.ObjectManager.SelectObject(obj.Id);

        var p2Screen = t.ChartToScreen(obj.Points[1]);

        using var bitmap = Render(obj, t, 1000, 600);
        var renderedHandle = FindHandlePixelNear(bitmap, p2Screen);
        Assert.NotNull(renderedHandle);

        bool pressed = controller.HandlePointerPressed(renderedHandle!.Value, renderedHandle.Value, viewModel, t, KeyModifiers.None, 1);

        Assert.True(pressed);
        Assert.Same(obj, controller.DraggedObject);
        Assert.Equal(1, controller.DraggedHandleIndex);
    }

    [Fact]
    public void HandleDrag_WhenLockRangeIsTrue_DraggingHandleMovesBothPointsPreservingBarCount()
    {
        var controller = new ChartInteractionController(new MagnetSnapService(), new DialogService(), new SmartGuideService());
        var candles = BuildCandles();
        var viewModel = new ChartViewModel { CurrentTool = DrawingTool.Pointer, Candles = candles };

        var t = new LinearCoordinateTransform(
            new DateTime(2025, 1, 1), new DateTime(2025, 1, 21), 0m, 300m, 1000, 600);

        // Initial points: index 1 to index 5 -> 5 bars
        var obj = new FixedRangeVolumeProfileObject(
            new ChartPoint(candles[1].Timestamp, 100m),
            new ChartPoint(candles[5].Timestamp, 200m))
        {
            LockRange = true
        };

        viewModel.ObjectManager.AddObject(obj);
        viewModel.ObjectManager.SelectObject(obj.Id);

        var p2Screen = t.ChartToScreen(obj.Points[1]);
        using var bitmap = Render(obj, t, 1000, 600);
        var renderedHandle = FindHandlePixelNear(bitmap, p2Screen);
        Assert.NotNull(renderedHandle);

        bool pressed = controller.HandlePointerPressed(renderedHandle!.Value, renderedHandle.Value, viewModel, t, KeyModifiers.None, 1);
        Assert.True(pressed);
        Assert.Same(obj, controller.DraggedObject);
        Assert.Equal(1, controller.DraggedHandleIndex);

        // Drag handle from index 5 to index 8 (+3 bars)
        var targetPoint = t.ChartToScreen(new ChartPoint(candles[8].Timestamp, 200m));
        var snapshot = new ChartDataSnapshot(candles);
        bool dragged = controller.HandleObjectDrag(targetPoint, snapshot, t, 0, 0, new global::Avalonia.Rect(0, 0, 1000, 600), KeyModifiers.None);
        Assert.True(dragged);

        // Both points must have shifted by +3 bars:
        // Points[0]: index 1 + 3 = 4
        // Points[1]: index 5 + 3 = 8
        Assert.Equal(candles[4].Timestamp, obj.Points[0].Time);
        Assert.Equal(candles[8].Timestamp, obj.Points[1].Time);

        // End drag and check recalculate preserves bar count
        controller.HandlePointerReleased(viewModel);
        Assert.Equal(5, obj.RangeBars);
    }

    /// <summary>
    /// F11 regression: LockRange bar-index resolution previously used snapshot.Candles -- the
    /// currently-visible slice -- instead of the full history. Panning the visible window so a VP
    /// endpoint sits outside it made FindNearestBarIndex clamp that endpoint to the slice's own
    /// edge, silently changing the bar count on drag. Mirrors the report's own example: full
    /// history [0,100), VP endpoints at global index 10/50, visible window [20,60) -- a +1 bar
    /// drag must still resolve to global index 11/51 (41 bars preserved), not reanchor endpoint 10
    /// to the visible slice's start (index 20).
    /// See sa_analysis_report_VolumeProfile_PostImplementation_StrictReview_20260914.md F11.
    /// </summary>
    [Fact]
    public void HandleDrag_LockRangeWithPannedVisibleWindow_ResolvesIndicesAgainstFullHistory()
    {
        var controller = new ChartInteractionController(new MagnetSnapService(), new DialogService(), new SmartGuideService());

        var fullCandles = new List<CoreCandleData>();
        for (int i = 0; i < 100; i++)
        {
            decimal close = 100m + i;
            fullCandles.Add(new CoreCandleData(new DateTime(2025, 1, 1).AddDays(i), close, close + 5, close - 5, close, 1000));
        }

        var t = new LinearCoordinateTransform(
            fullCandles[0].Timestamp, fullCandles[99].Timestamp.AddDays(1), 0m, 300m, 1000, 600);

        // VP spans global index 10..50 (41 bars); Points[0] (index 10) sits off-screen once the
        // visible window pans to [20,60).
        var obj = new FixedRangeVolumeProfileObject(
            new ChartPoint(fullCandles[10].Timestamp, 100m),
            new ChartPoint(fullCandles[50].Timestamp, 200m))
        {
            LockRange = true
        };

        controller.DraggedObject = obj;
        controller.DraggedHandleIndex = 1;

        var visibleSnapshot = new ChartDataSnapshot(fullCandles, startIndex: 20, count: 40);

        // Drag the visible handle (Points[1], global index 50) by +1 bar, to global index 51.
        var targetScreen = t.ChartToScreen(new ChartPoint(fullCandles[51].Timestamp, 200m));

        bool dragged = controller.HandleObjectDrag(
            targetScreen, visibleSnapshot, t, 0, 0,
            new global::Avalonia.Rect(0, 0, 1000, 600), KeyModifiers.None, allCandles: fullCandles);

        Assert.True(dragged);
        Assert.Equal(fullCandles[11].Timestamp, obj.Points[0].Time);
        Assert.Equal(fullCandles[51].Timestamp, obj.Points[1].Time);
    }
}
