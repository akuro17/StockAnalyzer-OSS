using System;
using System.Collections.Generic;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Behaviors;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

/// <summary>
/// Regression tests: RegressionTrendBehavior and FixedRangeVolumeBehavior must use the
/// two-click interaction model (click point 1 -> move updates a live preview -> click
/// point 2 finishes), matching RangeSplineBehavior/GhostFeedBehavior, instead of the
/// previous drag-to-draw model (press, hold, release finishes).
/// </summary>
public class AnalysisBehaviorsTests
{
    [Fact]
    public void TrendLineBehavior_UsesTwoClickModel()
    {
        var behavior = new StockAnalyzer.Avalonia.Drawing.Behaviors.TrendLineBehavior();
        Assert.Equal(2, behavior.RequiredSteps);
        Assert.False(behavior.FinishesOnRelease);

        var p0 = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var obj = behavior.CreateObject(p0);

        Assert.IsType<TrendLineObject>(obj);
        Assert.Equal(2, obj.Points.Count);
        Assert.Equal(p0, obj.Points[0]);
        Assert.Equal(p0, obj.Points[1]);

        // Mouse move while the second point has not been clicked yet (drawingStep 1)
        // must live-update Points[1] as a preview, without touching Points[0].
        var p1 = new ChartPoint(new DateTime(2025, 1, 3), 140m);
        behavior.UpdatePoint(obj, 1, p1);
        Assert.Equal(p0, obj.Points[0]);
        Assert.Equal(p1, obj.Points[1]);
    }

    [Fact]
    public void LineTextBehavior_UsesTwoClickModel()
    {
        var behavior = new StockAnalyzer.Avalonia.Drawing.Behaviors.LineTextBehavior();
        Assert.Equal(2, behavior.RequiredSteps);
        Assert.False(behavior.FinishesOnRelease);

        var p0 = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var obj = behavior.CreateObject(p0);

        Assert.IsType<LineTextObject>(obj);
        Assert.Equal(2, obj.Points.Count);
        Assert.Equal(p0, obj.Points[0]);
        Assert.Equal(p0, obj.Points[1]);

        var p1 = new ChartPoint(new DateTime(2025, 1, 3), 140m);
        behavior.UpdatePoint(obj, 1, p1);
        Assert.Equal(p0, obj.Points[0]);
        Assert.Equal(p1, obj.Points[1]);
    }

    [Fact]
    public void RegressionTrendBehavior_UsesTwoClickModel()
    {
        var behavior = new RegressionTrendBehavior();
        Assert.Equal(2, behavior.RequiredSteps);
        Assert.False(behavior.FinishesOnRelease);

        var p0 = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var obj = behavior.CreateObject(p0);

        Assert.IsType<RegressionTrendObject>(obj);
        Assert.Equal(2, obj.Points.Count);
        Assert.Equal(p0, obj.Points[0]);
        Assert.Equal(p0, obj.Points[1]);

        // Mouse move while the second point has not been clicked yet (drawingStep 1)
        // must live-update Points[1] as a preview, without touching Points[0].
        var p1 = new ChartPoint(new DateTime(2025, 1, 3), 140m);
        behavior.UpdatePoint(obj, 1, p1);
        Assert.Equal(p0, obj.Points[0]);
        Assert.Equal(p1, obj.Points[1]);
    }

    [Fact]
    public void FixedRangeVolumeBehavior_UsesTwoClickModel()
    {
        var behavior = new FixedRangeVolumeBehavior();
        Assert.Equal(2, behavior.RequiredSteps);
        Assert.False(behavior.FinishesOnRelease);

        var p0 = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var obj = behavior.CreateObject(p0);

        Assert.IsType<FixedRangeVolumeProfileObject>(obj);
        Assert.Equal(2, obj.Points.Count);
        Assert.Equal(p0, obj.Points[0]);
        Assert.Equal(p0, obj.Points[1]);

        var p1 = new ChartPoint(new DateTime(2025, 1, 3), 140m);
        behavior.UpdatePoint(obj, 1, p1);
        Assert.Equal(p0, obj.Points[0]);
        Assert.Equal(p1, obj.Points[1]);
    }

    [Fact]
    public void RangeSplineBehavior_StillUsesTwoClickModel()
    {
        // Not part of this fix (it already used the two-click model), kept here as a
        // guardrail so a future change doesn't silently regress it back to drag-to-draw.
        var behavior = new RangeSplineBehavior();
        Assert.Equal(2, behavior.RequiredSteps);
        Assert.False(behavior.FinishesOnRelease);
    }

    [Fact]
    public void FixedRangeVolumeProfileObject_WhileChoosingPoint2_ShowsLiveBoxPreview()
    {
        var t = new LinearCoordinateTransform(
            new DateTime(2025, 1, 1), new DateTime(2025, 1, 11),
            0m, 300m, 1000, 600);

        var anchor = new ChartPoint(new DateTime(2025, 1, 2), 100m);
        var obj = new FixedRangeVolumeProfileObject(anchor, anchor);

        // Simulate the two-click flow's live preview: point 1 is fixed, point 2 tracks
        // the mouse (no Recalculate() call yet -- ProfileData stays empty until the
        // second click, same as before this fix).
        obj.Points[1] = new ChartPoint(new DateTime(2025, 1, 9), 250m);

        var bitmap = new SKBitmap((int)t.CanvasWidth, (int)t.CanvasHeight);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        obj.Render(canvas, t);
        canvas.Flush();

        var p2Screen = t.ChartToScreen(obj.Points[1]);
        Assert.True(BitmapContainsColorNear(bitmap, SKColors.Red, p2Screen, radius: 3));
    }

    private static bool BitmapContainsColorNear(SKBitmap bitmap, SKColor color, global::Avalonia.Point center, int radius)
    {
        int minX = Math.Max(0, (int)center.X - radius);
        int maxX = Math.Min(bitmap.Width - 1, (int)center.X + radius);
        int minY = Math.Max(0, (int)center.Y - radius);
        int maxY = Math.Min(bitmap.Height - 1, (int)center.Y + radius);
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                if (bitmap.GetPixel(x, y) == color) return true;
            }
        }
        return false;
    }
}
