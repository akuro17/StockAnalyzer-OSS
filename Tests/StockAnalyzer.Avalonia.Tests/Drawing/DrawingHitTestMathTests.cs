using System;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Tests.Common;
using Xunit;
using Point = Avalonia.Point;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public sealed class DrawingHitTestMathTests
{
    private const double EdgeExclusion = 0.01;   // px: the reference path rounds the vertices to float, a point this close to an edge may differ

    [Fact]
    public void PolygonContains_InsideOutsideAndCrossedQuadrilateral()
    {
        var a = new Point(0, 0);
        var b = new Point(10, 0);
        var c = new Point(10, 10);
        var d = new Point(0, 10);

        Assert.True(DrawingHitTestMath.PolygonContains(new Point(5, 5), a, b, c, d));
        Assert.False(DrawingHitTestMath.PolygonContains(new Point(15, 5), a, b, c, d));
        Assert.False(DrawingHitTestMath.PolygonContains(new Point(5, -1), a, b, c, d));

        // Crossed quadrilateral (bow tie): a-b-d-c. The two lobes are inside, the pinch sides outside.
        Assert.True(DrawingHitTestMath.PolygonContains(new Point(5, 1), a, b, d, c));
        Assert.True(DrawingHitTestMath.PolygonContains(new Point(5, 9), a, b, d, c));
        Assert.False(DrawingHitTestMath.PolygonContains(new Point(1, 5), a, b, d, c));
    }

    [Fact]
    public void PolygonContains_AgreesWithSkPathContains_OnRandomAndCrossedQuadrilaterals()
    {
        // The even-odd rule and the winding rule of SKPath.Contains agree for every quadrilateral (at most one self-intersection),
        // except exactly on an edge. Checked on a point grid for 300 random quadrilaterals, crossed ones included.
        var random = new Random(12345);
        int compared = 0, crossed = 0;
        for (int q = 0; q < 300; q++)
        {
            Point[] v = new Point[4];
            for (int i = 0; i < 4; i++) v[i] = new Point(random.NextDouble() * 100, random.NextDouble() * 100);

            using var path = new SKPath();
            path.MoveTo((float)v[0].X, (float)v[0].Y);
            for (int i = 1; i < 4; i++) path.LineTo((float)v[i].X, (float)v[i].Y);
            path.Close();

            if (SegmentsCross(v[0], v[1], v[2], v[3]) || SegmentsCross(v[1], v[2], v[3], v[0])) crossed++;

            for (double x = -5; x <= 105; x += 2.5)
            {
                for (double y = -5; y <= 105; y += 2.5)
                {
                    var p = new Point(x, y);
                    if (DistanceToBoundary(p, v) < EdgeExclusion) continue;

                    Assert.True(path.Contains((float)x, (float)y) == DrawingHitTestMath.PolygonContains(p, v[0], v[1], v[2], v[3]),
                        $"quad {q} ({v[0]}, {v[1]}, {v[2]}, {v[3]}) at ({x}, {y})");
                    compared++;
                }
            }
        }

        Assert.True(compared > 100_000);
        Assert.True(crossed > 20, $"only {crossed} crossed quadrilaterals");
    }

    [Fact]
    public void ParallelChannelHitTest_InBothModes_AllocatesNothing_AndKeepsTheBowTieLobesInside()
    {
        var transform = new PixelCoordinateTransform();
        foreach (ChannelMode mode in new[] { ChannelMode.Parallel, ChannelMode.FlatOpposite })
        {
            // P1 below and P2 above the price of P3: in FlatOpposite the opposite edge (y = 200) crosses the base edge: a bow tie.
            using var obj = new ParallelChannelObject(PixelCoordinateTransform.Chart(100, 100), PixelCoordinateTransform.Chart(300, 300), PixelCoordinateTransform.Chart(100, 200))
            {
                ChannelMode = mode,
                IsFilled = true,
                Color = Colors.White
            };

            var inside = mode == ChannelMode.Parallel ? new Point(150, 160) : new Point(120, 190);   // inside the (first) lobe
            var outside = new Point(700, 20);
            Assert.True(obj.HitTest(inside, transform), mode.ToString());
            Assert.False(obj.HitTest(outside, transform), mode.ToString());

            foreach (Point probe in new[] { inside, outside })
            {
                long bytes = AllocationProbe.InCollectionFreeWindow(() => obj.HitTest(probe, transform));
                Assert.Equal(0, bytes);
            }
        }
    }

    private static bool SegmentsCross(Point a, Point b, Point c, Point d)
    {
        static double Cross(Point o, Point p, Point q) => (p.X - o.X) * (q.Y - o.Y) - (p.Y - o.Y) * (q.X - o.X);
        double d1 = Cross(c, d, a), d2 = Cross(c, d, b), d3 = Cross(a, b, c), d4 = Cross(a, b, d);
        return d1 * d2 < 0 && d3 * d4 < 0;
    }

    private static double DistanceToBoundary(Point p, Point[] v)
    {
        double best = double.MaxValue;
        for (int i = 0; i < 4; i++)
        {
            Point from = v[i], to = v[(i + 1) % 4];
            double dx = to.X - from.X, dy = to.Y - from.Y;
            double length2 = dx * dx + dy * dy;
            double t = length2 == 0 ? 0 : Math.Clamp(((p.X - from.X) * dx + (p.Y - from.Y) * dy) / length2, 0, 1);
            double px = from.X + t * dx, py = from.Y + t * dy;
            best = Math.Min(best, Math.Sqrt((p.X - px) * (p.X - px) + (p.Y - py) * (p.Y - py)));
        }

        return best;
    }
}
