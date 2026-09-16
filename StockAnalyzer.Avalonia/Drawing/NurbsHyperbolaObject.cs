namespace StockAnalyzer.Avalonia.Drawing;

using System;
using System.Collections.Generic;
using global::Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

/// <summary>
/// NURBS Hyperbola drawing object using 3-point quadratic rational B-spline (w1 > 1.0).
/// Uses 3 anchor points: Points[0] = Start, Points[1] = Vertex/Asymptote intersection, Points[2] = End.
/// Default color matches Arrow tool (Colors.Green).
/// </summary>
public class NurbsHyperbolaObject : RelativeGeometricRenderer, INurbsWeightedCurveObject
{
    public override ChartObjectType Type => ChartObjectType.NurbsHyperbola;

    private double _weight = 2.0;
    public double Weight
    {
        get => _weight;
        set => _weight = Math.Clamp(value, 1.01, 100.0);
    }

    public double WeightRangeMin => 1.01;
    public double WeightRangeMax => 20.0;
    public string WeightLabelKey => "Setting_Nurbs_HyperbolaWeight";
    public double WeightIncrement => 0.1;
    public string WeightFormatString => "0.0";

    public NurbsHyperbolaObject() : base()
    {
        Color = Colors.Green;
    }

    public NurbsHyperbolaObject(ChartPoint p0, ChartPoint p1, ChartPoint p2) : this()
    {
        Points.Add(p0);
        Points.Add(p1);
        Points.Add(p2);
    }

    public NurbsHyperbolaObject(IEnumerable<ChartPoint> points) : this()
    {
        if (points != null)
        {
            Points.AddRange(points);
        }
    }

    protected override void DrawGeometry(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count < 3) return;

        var s0 = transform.ChartToScreen(Points[0]);
        var s1 = transform.ChartToScreen(Points[1]);
        var s2 = transform.ChartToScreen(Points[2]);

        Span<SKPoint> pts = stackalloc SKPoint[3]
        {
            new SKPoint((float)s0.X, (float)s0.Y),
            new SKPoint((float)s1.X, (float)s1.Y),
            new SKPoint((float)s2.X, (float)s2.Y)
        };

        _cachedPath.Rewind();
        NurbsConicFactory.BuildHyperbolaPath(_cachedPath, pts, Weight);

        _cachedPaint.StrokeJoin = SKStrokeJoin.Round;
        _cachedPaint.StrokeCap = SKStrokeCap.Round;
        canvas.DrawPath(_cachedPath, _cachedPaint);
    }

    public override bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (transform == null || Points.Count < 3) return false;

        var s0 = transform.ChartToScreen(Points[0]);
        var s1 = transform.ChartToScreen(Points[1]);
        var s2 = transform.ChartToScreen(Points[2]);

        Span<SKPoint> pts = stackalloc SKPoint[3]
        {
            new SKPoint((float)s0.X, (float)s0.Y),
            new SKPoint((float)s1.X, (float)s1.Y),
            new SKPoint((float)s2.X, (float)s2.Y)
        };

        float minX = Math.Min(pts[0].X, Math.Min(pts[1].X, pts[2].X));
        float maxX = Math.Max(pts[0].X, Math.Max(pts[1].X, pts[2].X));
        float minY = Math.Min(pts[0].Y, Math.Min(pts[1].Y, pts[2].Y));
        float maxY = Math.Max(pts[0].Y, Math.Max(pts[1].Y, pts[2].Y));

        double effectiveTolerance = tolerance + (Thickness / 2.0);
        if (screenPoint.X < minX - effectiveTolerance || screenPoint.X > maxX + effectiveTolerance ||
            screenPoint.Y < minY - effectiveTolerance || screenPoint.Y > maxY + effectiveTolerance)
        {
            return false;
        }

        Span<double> knots = stackalloc double[6] { 0, 0, 0, 1, 1, 1 };
        Span<double> weights = stackalloc double[3] { 1.0, Weight, 1.0 };

        int sampleCount = 40;
        double dt = 1.0 / (sampleCount - 1);
        SKPoint skPt = new SKPoint((float)screenPoint.X, (float)screenPoint.Y);

        if (!NurbsMath.TryEvaluate(0.0, 2, pts, weights, knots, out var prevSample))
            return false;

        double tolSquared = effectiveTolerance * effectiveTolerance;

        for (int i = 1; i < sampleCount; i++)
        {
            double t = i * dt;
            if (NurbsMath.TryEvaluate(t, 2, pts, weights, knots, out var currSample))
            {
                if (DistanceSquaredToSegment(skPt, prevSample, currSample) <= tolSquared)
                    return true;
                prevSample = currSample;
            }
        }

        return false;
    }

    private static double DistanceSquaredToSegment(SKPoint p, SKPoint a, SKPoint b)
    {
        float dx = b.X - a.X;
        float dy = b.Y - a.Y;
        float l2 = dx * dx + dy * dy;
        if (l2 <= 1e-12f)
        {
            float px = p.X - a.X;
            float py = p.Y - a.Y;
            return px * px + py * py;
        }

        float t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2;
        t = Math.Clamp(t, 0f, 1f);
        float projX = a.X + t * dx;
        float projY = a.Y + t * dy;
        float dX = p.X - projX;
        float dY = p.Y - projY;
        return dX * dX + dY * dY;
    }
}
