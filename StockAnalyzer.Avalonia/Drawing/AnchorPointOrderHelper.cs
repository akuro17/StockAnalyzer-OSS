using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Normalizes a chart object's control points into a clockwise (as seen on screen) cycling order,
/// for use by the "AP" anchor-point cycling interaction (see <see cref="IChartObject.AnchorPointIndex"/>).
/// Point placement order (e.g. click order while drawing a Triangle) is not guaranteed to already be
/// clockwise, so callers must not assume <c>Points[i] -> Points[i+1]</c> is already a clockwise walk.
/// </summary>
public static class AnchorPointOrderHelper
{
    /// <summary>
    /// Returns the indices of <paramref name="screenPoints"/> reordered into a clockwise cycling
    /// sequence, as visually perceived on screen (screen Y increases downward). Index 0 of the
    /// result is always the original point at index 0, so the existing default
    /// <see cref="IChartObject.AnchorPointIndex"/> = 0 behavior is unaffected by this normalization
    /// — only the traversal *direction* through the remaining points may be reversed.
    /// Fewer than 3 points (no orientation is defined) or an exactly collinear/degenerate point set
    /// (zero signed area) are returned in their original order unchanged.
    /// </summary>
    public static int[] GetClockwiseCycleOrder(IReadOnlyList<global::Avalonia.Point> screenPoints)
    {
        int n = screenPoints.Count;
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;

        if (n < 3) return order;

        // Shoelace formula. In screen space (Y-down), a positive signed area corresponds to a
        // visually clockwise point sequence (the opposite of the standard math/Y-up convention).
        double signedArea = ComputeSignedArea(screenPoints);
        if (signedArea >= 0) return order;

        for (int i = 1; i < n; i++) order[i] = n - i;
        return order;
    }

    /// <summary>
    /// Overload operating directly on chart-space <see cref="ChartPoint"/>s, for callers (e.g. the
    /// Objects layer panel's "AP" button) that have no screen-space <see cref="ICoordinateTransform"/>
    /// available. Any chart rendering maps Time to screen X with a positive (increasing left-to-right)
    /// scale and Price to screen Y with a negative (higher price = smaller Y) scale, and the shoelace
    /// orientation sign is invariant under per-axis positive scaling and translation and flips under
    /// exactly one axis negation — so mapping to (relative-seconds-since-the-first-point, -Price)
    /// reproduces the same clockwise/counter-clockwise classification as real screen coordinates would,
    /// for any zoom/pan. The relative-seconds offset (rather than raw <c>Time.Ticks</c>) keeps the
    /// magnitudes small so the shoelace products don't lose precision to floating-point cancellation.
    /// </summary>
    public static int[] GetClockwiseCycleOrder(IReadOnlyList<ChartPoint> chartPoints)
    {
        if (chartPoints.Count == 0) return System.Array.Empty<int>();

        var referenceTime = chartPoints[0].Time;
        var pseudoScreenPoints = chartPoints
            .Select(p => new global::Avalonia.Point((p.Time - referenceTime).TotalSeconds, -(double)p.Price))
            .ToList();

        return GetClockwiseCycleOrder(pseudoScreenPoints);
    }

    private static double ComputeSignedArea(IReadOnlyList<global::Avalonia.Point> points)
    {
        double sum = 0;
        int n = points.Count;
        for (int i = 0; i < n; i++)
        {
            var current = points[i];
            var next = points[(i + 1) % n];
            sum += current.X * next.Y - next.X * current.Y;
        }
        return sum;
    }
}
