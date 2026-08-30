using System;
using System.Collections.Generic;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

public class RegressionTrendObjectTests
{
    private static SKBitmap Render(IChartObject obj, ICoordinateTransform t)
    {
        var bitmap = new SKBitmap((int)t.CanvasWidth, (int)t.CanvasHeight);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        obj.Render(canvas, t);
        canvas.Flush();
        return bitmap;
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

    // Candles whose regression-fitted price clearly diverges from the user's raw
    // click price, so a bug that hit-tests against the raw click position (instead
    // of the rendered handle position) is guaranteed to be caught by this test.
    private static List<CoreCandleData> BuildTrendingCandles()
    {
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 6; i++)
        {
            decimal close = 100m + i * 20m; // Perfect uptrend: 100, 120, 140, 160, 180, 200
            candles.Add(new CoreCandleData(new DateTime(2025, 1, 2 + i), close, close + 5, close - 5, close, 1000));
        }
        return candles;
    }

    [Fact]
    public void Recalculate_SyncsPointsToRenderedHandlePositions()
    {
        // User clicks/drags at an arbitrary raw price (300) far from where the fitted
        // regression line actually sits for this candle range.
        var obj = new RegressionTrendObject(
            new ChartPoint(new DateTime(2025, 1, 2), 300m),
            new ChartPoint(new DateTime(2025, 1, 7), 300m));

        obj.Recalculate(BuildTrendingCandles());

        // Render()/HitTest() draw and hit-test handles at the regression-fitted price
        // (GetValueAt(0)=100, GetValueAt(5)=200 for this perfectly linear data), not
        // at the user's raw 300-price click. The interaction controller's generic
        // handle hit-test checks Points[] directly, so Points[] must match what is
        // actually rendered -- otherwise clicking the visible handle circle never
        // registers as a hit and the control point cannot be dragged.
        Assert.Equal(new DateTime(2025, 1, 2), obj.Points[0].Time);
        Assert.Equal(100m, obj.Points[0].Price);
        Assert.Equal(new DateTime(2025, 1, 7), obj.Points[1].Time);
        Assert.Equal(200m, obj.Points[1].Price);
    }

    [Fact]
    public void Recalculate_PreservesPointIdentity_WhenPointsAreReversed()
    {
        // Points[0] is chronologically LATER than Points[1] (e.g. the user dragged
        // handle 0 past handle 1). Recalculate must still assign each index its own
        // chronologically-correct fitted value, not silently swap which index is
        // "the start" -- a mid-drag identity swap would make the handle currently
        // being dragged jump to the opposite end.
        var obj = new RegressionTrendObject(
            new ChartPoint(new DateTime(2025, 1, 7), 0m),
            new ChartPoint(new DateTime(2025, 1, 2), 0m));

        obj.Recalculate(BuildTrendingCandles());

        Assert.Equal(new DateTime(2025, 1, 7), obj.Points[0].Time);
        Assert.Equal(200m, obj.Points[0].Price);
        Assert.Equal(new DateTime(2025, 1, 2), obj.Points[1].Time);
        Assert.Equal(100m, obj.Points[1].Price);
    }

    [Fact]
    public void Render_WhilePointsAreStaleFromDeferredRecalculate_FollowsLiveDragPosition()
    {
        // ChartInteractionController.HandleObjectDrag intentionally defers
        // Recalculate() to HandlePointerReleased during a handle drag (a prior fix
        // for per-PointerMoved-frame regression-recompute stutter) and, mid-drag,
        // assigns only the dragged handle's raw mouse position directly into
        // Points[DraggedHandleIndex] -- simulated here without needing the full
        // interaction controller.
        var t = new LinearCoordinateTransform(
            new DateTime(2025, 1, 1), new DateTime(2025, 1, 11),
            0m, 300m, 1000, 600);

        var obj = new RegressionTrendObject(
            new ChartPoint(new DateTime(2025, 1, 2), 100m),
            new ChartPoint(new DateTime(2025, 1, 7), 100m))
        {
            IsSelected = true
        };
        obj.Recalculate(BuildTrendingCandles());

        var oldEndScreen = t.ChartToScreen(obj.Points[1]);

        // Drag handle 1 far away without calling Recalculate() again (Points[] is now
        // "stale" relative to the cached _start/_end/_result).
        obj.Points[1] = new ChartPoint(new DateTime(2025, 1, 10), 280m);
        var newEndScreen = t.ChartToScreen(obj.Points[1]);

        using var bitmap = Render(obj, t);

        // The rendered handle must appear at the live dragged position...
        Assert.True(BitmapContainsColorNear(bitmap, SKColors.Red, newEndScreen, radius: 3));
        // ...and must NOT still be frozen at the pre-drag position.
        Assert.False(BitmapContainsColorNear(bitmap, SKColors.Red, oldEndScreen, radius: 3));
    }

    /// <summary>
    /// Recalculate() has two candle-filtering code paths: an O(log N) binary-search fast
    /// path when given a real IReadOnlyList&lt;CoreCandleData&gt; (used by
    /// ChartInteractionController's PointerMoved-frequency callers, per
    /// SA_RENDERING_PERFORMANCE.md's "no LINQ in hot paths" rule), and a foreach-plus-Sort
    /// fallback for an arbitrary IEnumerable (e.g. the LINQ .Select()-wrapped candles
    /// passed once per drag at release time). Both must produce identical results for the
    /// same logical candle set, including when the fallback receives its candles
    /// out-of-order (verifying the explicit sort still runs, since the fast path relies on
    /// its input already being chronological but the fallback historically tolerated
    /// unsorted input via `.OrderBy()`).
    /// </summary>
    [Fact]
    public void Recalculate_FastPathAndSlowPath_ProduceIdenticalResults()
    {
        var candles = BuildTrendingCandles();
        // A candle outside [Points[0].Time, Points[1].Time] must be excluded by both paths.
        candles.Add(new CoreCandleData(new DateTime(2025, 1, 20), 900m, 905m, 895m, 900m, 1000));

        var objFast = new RegressionTrendObject(
            new ChartPoint(new DateTime(2025, 1, 2), 0m),
            new ChartPoint(new DateTime(2025, 1, 7), 0m));
        objFast.Recalculate(candles); // candles is List<T> -> IReadOnlyList<T> fast path

        var shuffled = new List<CoreCandleData> { candles[3], candles[0], candles[5], candles[1], candles[4], candles[2], candles[6] };
        var objSlow = new RegressionTrendObject(
            new ChartPoint(new DateTime(2025, 1, 2), 0m),
            new ChartPoint(new DateTime(2025, 1, 7), 0m));
        objSlow.Recalculate(EnumerateLazily(shuffled)); // plain IEnumerable<T>, not IReadOnlyList -> slow path

        Assert.Equal(objFast.Points[0], objSlow.Points[0]);
        Assert.Equal(objFast.Points[1], objSlow.Points[1]);
    }

    /// <summary>Wraps a list as a plain lazily-evaluated IEnumerable, deliberately NOT an
    /// IReadOnlyList, to force Recalculate() into its foreach-plus-Sort fallback path.</summary>
    private static IEnumerable<CoreCandleData> EnumerateLazily(IEnumerable<CoreCandleData> source)
    {
        foreach (var c in source) yield return c;
    }
}
