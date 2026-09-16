using System;
using System.Collections.Generic;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

/// <summary>
/// Regression tests: RegressionTrendObject / RangeSplineObject /
/// FixedRangeVolumeProfileObject must show an anchor marker at Points[0]
/// immediately after the first click, even before the second point has moved far
/// enough for the [Points[0].Time, Points[1].Time] range to contain >= 2 candles.
/// Their Render() methods historically bailed out entirely (drawing nothing at all,
/// not even the just-placed first point) whenever the regression fit / extracted
/// spline / volume profile could not yet be computed from too few candles.
/// </summary>
public class DrawingToolFirstPointVisibilityTests
{
    private static LinearCoordinateTransform MakeTransform()
        => new LinearCoordinateTransform(
            new DateTime(2025, 1, 1), new DateTime(2025, 1, 10),
            0m, 300m, 800, 600);

    private static SKBitmap Render(IChartObject obj, ICoordinateTransform t)
    {
        var bitmap = new SKBitmap(800, 600);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        obj.Render(canvas, t);
        canvas.Flush();
        return bitmap;
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

    [Fact]
    public void RegressionTrendObject_ImmediatelyAfterFirstClick_ShowsAnchorPoint()
    {
        var t = MakeTransform();
        // Both points identical (matches how RegressionTrendBehavior.CreateInstance
        // constructs the object at the moment of the first click), so the time range
        // is zero-width and Recalculate() cannot produce a valid regression yet.
        var anchor = new ChartPoint(new DateTime(2025, 1, 2), 100m);
        var obj = new RegressionTrendObject(anchor, anchor);
        obj.Recalculate(new List<CoreCandleData>
        {
            new(new DateTime(2025, 1, 2), 100m, 110m, 90m, 100m, 1000),
        });

        using var bitmap = Render(obj, t);

        Assert.True(BitmapContainsColor(bitmap, SKColors.Red));
    }

    [Fact]
    public void RangeSplineObject_ImmediatelyAfterFirstClick_ShowsAnchorPoint()
    {
        var t = MakeTransform();
        var anchor = new ChartPoint(new DateTime(2025, 1, 2), 100m);
        var obj = new RangeSplineObject(anchor, anchor);
        obj.Recalculate(new List<CoreCandleData>
        {
            new(new DateTime(2025, 1, 2), 100m, 110m, 90m, 100m, 1000),
        });

        using var bitmap = Render(obj, t);

        Assert.True(BitmapContainsColor(bitmap, SKColors.Red));
    }

    [Fact]
    public void FixedRangeVolumeProfileObject_ImmediatelyAfterFirstClick_ShowsAnchorPoint()
    {
        var t = MakeTransform();
        var anchor = new ChartPoint(new DateTime(2025, 1, 2), 100m);
        var obj = new FixedRangeVolumeProfileObject(anchor, anchor);
        // No candles recalculated at all yet (mirrors FixedRangeVolumeBehavior,
        // whose CreateInstance does not call Recalculate() -- only OnPointUpdated
        // does, once the mouse actually moves).

        using var bitmap = Render(obj, t);

        Assert.True(BitmapContainsColor(bitmap, SKColors.Red));
    }
}
