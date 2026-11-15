namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Screen-space hit-test geometry shared by the drawing objects (no allocation, no native resource).
/// </summary>
internal static class DrawingHitTestMath
{
    /// <summary>
    /// Even-odd point-in-quadrilateral test (no allocation; also correct for a crossed quadrilateral).
    /// A quadrilateral has at most one self-intersection, which gives two lobes of opposite orientation (winding +1 and -1) and no region of winding +-2,
    /// so the even-odd rule and the winding rule of <c>SKPath.Contains</c> give the same answer everywhere except exactly on an edge.
    /// </summary>
    /// <param name="p">The screen point.</param>
    /// <param name="a">First vertex; the vertices are given in boundary order (a-b-c-d-a).</param>
    internal static bool PolygonContains(global::Avalonia.Point p, global::Avalonia.Point a, global::Avalonia.Point b, global::Avalonia.Point c, global::Avalonia.Point d)
    {
        bool inside = false;
        if (EdgeCrossesRay(p, d, a)) inside = !inside;
        if (EdgeCrossesRay(p, a, b)) inside = !inside;
        if (EdgeCrossesRay(p, b, c)) inside = !inside;
        if (EdgeCrossesRay(p, c, d)) inside = !inside;
        return inside;
    }

    private static bool EdgeCrossesRay(global::Avalonia.Point p, global::Avalonia.Point from, global::Avalonia.Point to)
    {
        if ((from.Y > p.Y) == (to.Y > p.Y)) return false;
        double xAtY = from.X + (p.Y - from.Y) / (to.Y - from.Y) * (to.X - from.X);
        return p.X < xAtY;
    }
}
