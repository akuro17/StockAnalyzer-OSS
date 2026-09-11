namespace StockAnalyzer.Avalonia.Drawing;

using System;
using System.Collections.Generic;
using global::Avalonia;
using global::Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

/// <summary>
/// 3-Point Quadratic Bézier Curve drawing object.
/// Inherits RelativeGeometricRenderer for Zero-Allocation rendering in 60fps hot path.
/// Evaluates quadratic Bézier curve via mathematical degree elevation to SkiaSharp Cubic Bézier.
/// </summary>
public class CurveTrendObject : RelativeGeometricRenderer
{
    public override ChartObjectType Type => ChartObjectType.CurveTrend;

    public CurveTrendObject() : base()
    {
        Color = DrawingThemeContext.DefaultColor;
        Thickness = DrawingThemeContext.DefaultStrokeThickness;
    }

    public CurveTrendObject(ChartPoint p0, ChartPoint p1, ChartPoint p2) : this()
    {
        Points.Add(p0);
        Points.Add(p1);
        Points.Add(p2);
    }

    public CurveTrendObject(IEnumerable<ChartPoint> points) : this()
    {
        if (points != null)
        {
            Points.AddRange(points);
        }
    }

    protected override void DrawGeometry(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points == null || Points.Count < 3) return;

        var s0 = transform.ChartToScreen(Points[0]);
        var s1 = transform.ChartToScreen(Points[1]);
        var s2 = transform.ChartToScreen(Points[2]);

        float s0X = (float)s0.X, s0Y = (float)s0.Y;
        float s1X = (float)s1.X, s1Y = (float)s1.Y;
        float s2X = (float)s2.X, s2Y = (float)s2.Y;

        // IEEE 754 Safety Guard: Abort on any NaN/Inf
        if (float.IsNaN(s0X) || float.IsNaN(s0Y) || float.IsInfinity(s0X) || float.IsInfinity(s0Y) ||
            float.IsNaN(s1X) || float.IsNaN(s1Y) || float.IsInfinity(s1X) || float.IsInfinity(s1Y) ||
            float.IsNaN(s2X) || float.IsNaN(s2Y) || float.IsInfinity(s2X) || float.IsInfinity(s2Y))
        {
            return;
        }

        // Degree elevation: C1 = (S0 + 2*S2)/3, C2 = (S1 + 2*S2)/3
        float c1X = (s0X + 2f * s2X) / 3f;
        float c1Y = (s0Y + 2f * s2Y) / 3f;
        float c2X = (s1X + 2f * s2X) / 3f;
        float c2Y = (s1Y + 2f * s2Y) / 3f;

        _cachedPaint.StrokeJoin = SKStrokeJoin.Round;
        _cachedPaint.StrokeCap = SKStrokeCap.Round;
        _cachedPath.Rewind();

        _cachedPath.MoveTo(s0X, s0Y);
        _cachedPath.CubicTo(c1X, c1Y, c2X, c2Y, s1X, s1Y);

        canvas.DrawPath(_cachedPath, _cachedPaint);
    }

    public override bool HitTest(Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (tolerance < 0.0 || double.IsNaN(tolerance) || double.IsInfinity(tolerance))
            throw new ArgumentOutOfRangeException(nameof(tolerance), "Tolerance must be a non-negative finite number.");

        if (Thickness < 0.0 || double.IsNaN(Thickness) || double.IsInfinity(Thickness))
            throw new ArgumentOutOfRangeException(nameof(Thickness), "Thickness must be a non-negative finite number.");

        if (transform == null || Points == null || Points.Count < 3) return false;

        if (double.IsNaN(screenPoint.X) || double.IsNaN(screenPoint.Y) ||
            double.IsInfinity(screenPoint.X) || double.IsInfinity(screenPoint.Y))
            return false;

        var s0 = transform.ChartToScreen(Points[0]);
        var s1 = transform.ChartToScreen(Points[1]);
        var s2 = transform.ChartToScreen(Points[2]);

        float s0X = (float)s0.X, s0Y = (float)s0.Y;
        float s1X = (float)s1.X, s1Y = (float)s1.Y;
        float s2X = (float)s2.X, s2Y = (float)s2.Y;

        // IEEE 754 Safety Guard
        if (float.IsNaN(s0X) || float.IsNaN(s0Y) || float.IsInfinity(s0X) || float.IsInfinity(s0Y) ||
            float.IsNaN(s1X) || float.IsNaN(s1Y) || float.IsInfinity(s1X) || float.IsInfinity(s1Y) ||
            float.IsNaN(s2X) || float.IsNaN(s2Y) || float.IsInfinity(s2X) || float.IsInfinity(s2Y))
        {
            return false;
        }

        double effTol = tolerance + (Thickness / 2.0);

        // Degenerate case: S0 == S1 == S2 (within 1e-4)
        float d01Sq = (s0X - s1X) * (s0X - s1X) + (s0Y - s1Y) * (s0Y - s1Y);
        float d02Sq = (s0X - s2X) * (s0X - s2X) + (s0Y - s2Y) * (s0Y - s2Y);
        if (d01Sq <= 1e-8f && d02Sq <= 1e-8f)
        {
            double dist = Math.Sqrt((screenPoint.X - s0X) * (screenPoint.X - s0X) + (screenPoint.Y - s0Y) * (screenPoint.Y - s0Y));
            return dist <= effTol;
        }

        // Bounding Box Pre-Check
        float minX = Math.Min(s0X, Math.Min(s1X, s2X));
        float maxX = Math.Max(s0X, Math.Max(s1X, s2X));
        float minY = Math.Min(s0Y, Math.Min(s1Y, s2Y));
        float maxY = Math.Max(s0Y, Math.Max(s1Y, s2Y));

        if (screenPoint.X < minX - effTol || screenPoint.X > maxX + effTol ||
            screenPoint.Y < minY - effTol || screenPoint.Y > maxY + effTol)
        {
            return false;
        }

        // Degree elevation to Cubic Bézier for exact segment testing
        SKPoint p0 = new SKPoint(s0X, s0Y);
        SKPoint p3 = new SKPoint(s1X, s1Y);
        SKPoint c1 = new SKPoint((s0X + 2f * s2X) / 3f, (s0Y + 2f * s2Y) / 3f);
        SKPoint c2 = new SKPoint((s1X + 2f * s2X) / 3f, (s1Y + 2f * s2Y) / 3f);

        return BezierSplineMath.HitTestCubicSegment(
            new SKPoint((float)screenPoint.X, (float)screenPoint.Y),
            p0, c1, c2, p3, effTol);
    }
}
