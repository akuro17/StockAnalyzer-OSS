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
}
