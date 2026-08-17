namespace StockAnalyzer.Avalonia.Drawing;

using System;
using System.Collections.Generic;
using global::Avalonia;
using global::Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

/// <summary>
/// 4-Point Parallel Curve Channel drawing object.
/// Inherits RelativeGeometricRenderer for Zero-Allocation rendering in 60fps hot path.
/// P0, P1, P2 define the base quadratic Bézier curve.
/// P3 defines the vertical channel width/offset.
/// </summary>
public class CurveChannelObject : RelativeGeometricRenderer
{
    public override ChartObjectType Type => ChartObjectType.CurveChannel;

    public bool IsFilled { get; set; } = true;

    // Zero-Allocation pre-allocated paints & path
    private readonly SKPaint _fillPaint;
    private readonly SKPaint _centerPaint;
    private readonly SKPaint _controlPolygonPaint;
    private readonly SKPath _cachedCenterPath;

    public CurveChannelObject() : base()
    {
        Color = DrawingThemeContext.DefaultColor;
        Thickness = DrawingThemeContext.DefaultStrokeThickness;

        _fillPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        _centerPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash(new float[] { 5f, 5f }, 0f)
        };

        _controlPolygonPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash(new float[] { 3f, 3f }, 0f)
        };

        _cachedCenterPath = new SKPath();
    }

    public CurveChannelObject(ChartPoint p0, ChartPoint p1, ChartPoint p2, ChartPoint p3) : this()
    {
        Points.Add(p0);
        Points.Add(p1);
        Points.Add(p2);
        Points.Add(p3);
    }

    public CurveChannelObject(IEnumerable<ChartPoint> points) : this()
    {
        if (points != null)
        {
            Points.AddRange(points);
        }
    }

    protected override void DrawGeometry(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points == null || Points.Count < 4) return;

        var s0 = transform.ChartToScreen(Points[0]);
        var s1 = transform.ChartToScreen(Points[1]);
        var s2 = transform.ChartToScreen(Points[2]);
        var s3 = transform.ChartToScreen(Points[3]);

        float s0X = (float)s0.X, s0Y = (float)s0.Y;
        float s1X = (float)s1.X, s1Y = (float)s1.Y;
        float s2X = (float)s2.X, s2Y = (float)s2.Y;
        float s3X = (float)s3.X, s3Y = (float)s3.Y;

        // IEEE 754 Safety Guard
        if (float.IsNaN(s0X) || float.IsNaN(s0Y) || float.IsInfinity(s0X) || float.IsInfinity(s0Y) ||
            float.IsNaN(s1X) || float.IsNaN(s1Y) || float.IsInfinity(s1X) || float.IsInfinity(s1Y) ||
            float.IsNaN(s2X) || float.IsNaN(s2Y) || float.IsInfinity(s2X) || float.IsInfinity(s2Y) ||
            float.IsNaN(s3X) || float.IsNaN(s3Y) || float.IsInfinity(s3X) || float.IsInfinity(s3Y))
        {
            return;
        }

        // Base curve degree elevation
        SKPoint s0Pt = new SKPoint(s0X, s0Y);
        SKPoint s1Pt = new SKPoint(s1X, s1Y);
        SKPoint c1 = new SKPoint((s0X + 2f * s2X) / 3f, (s0Y + 2f * s2Y) / 3f);
        SKPoint c2 = new SKPoint((s1X + 2f * s2X) / 3f, (s1Y + 2f * s2Y) / 3f);

        // Geometric midpoint at t = 0.5: S_mid = 0.25*S0 + 0.5*S2 + 0.25*S1
        float smidY = 0.25f * s0Y + 0.5f * s2Y + 0.25f * s1Y;
        float deltaY = s3Y - smidY;

        // Parallel curve control points
        SKPoint sPrime0 = new SKPoint(s0X, s0Y + deltaY);
        SKPoint sPrime1 = new SKPoint(s1X, s1Y + deltaY);
        SKPoint cPrime1 = new SKPoint(c1.X, c1.Y + deltaY);
        SKPoint cPrime2 = new SKPoint(c2.X, c2.Y + deltaY);

        // Center curve control points
        float halfDeltaY = 0.5f * deltaY;
        SKPoint sDoublePrime0 = new SKPoint(s0X, s0Y + halfDeltaY);
        SKPoint sDoublePrime1 = new SKPoint(s1X, s1Y + halfDeltaY);
        SKPoint cDoublePrime1 = new SKPoint(c1.X, c1.Y + halfDeltaY);
        SKPoint cDoublePrime2 = new SKPoint(c2.X, c2.Y + halfDeltaY);

        // Construct band path
        _cachedPath.Rewind();
        _cachedPath.FillType = SKPathFillType.Winding;
        _cachedPath.MoveTo(s0Pt);
        _cachedPath.CubicTo(c1, c2, s1Pt);
        _cachedPath.LineTo(sPrime1);
        _cachedPath.CubicTo(cPrime2, cPrime1, sPrime0);
        _cachedPath.Close();

        // 1. Fill Band
        if (IsFilled)
        {
            _fillPaint.Color = new SKColor(SkiaColor.Red, SkiaColor.Green, SkiaColor.Blue, 30);
            canvas.DrawPath(_cachedPath, _fillPaint);
        }

        // 2. Stroke Channel Outline
        _cachedPaint.StrokeJoin = SKStrokeJoin.Round;
        _cachedPaint.StrokeCap = SKStrokeCap.Round;
        canvas.DrawPath(_cachedPath, _cachedPaint);

        // 3. Center Median Line
        _centerPaint.Color = SkiaColor;
        _cachedCenterPath.Rewind();
        _cachedCenterPath.MoveTo(sDoublePrime0);
        _cachedCenterPath.CubicTo(cDoublePrime1, cDoublePrime2, sDoublePrime1);
        canvas.DrawPath(_cachedCenterPath, _centerPaint);

        // 4. Control polygon when selected
        if (IsSelected)
        {
            _controlPolygonPaint.Color = new SKColor(SkiaColor.Red, SkiaColor.Green, SkiaColor.Blue, 120);
            canvas.DrawLine(s0Pt.X, s0Pt.Y, s2X, s2Y, _controlPolygonPaint);
            canvas.DrawLine(s2X, s2Y, s1Pt.X, s1Pt.Y, _controlPolygonPaint);
            float smidX = 0.25f * s0X + 0.5f * s2X + 0.25f * s1X;
            canvas.DrawLine(smidX, smidY, s3X, s3Y, _controlPolygonPaint);
        }
    }

    public override bool HitTest(Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (tolerance < 0.0 || double.IsNaN(tolerance) || double.IsInfinity(tolerance))
            throw new ArgumentOutOfRangeException(nameof(tolerance), "Tolerance must be a non-negative finite number.");

        if (Thickness < 0.0 || double.IsNaN(Thickness) || double.IsInfinity(Thickness))
            throw new ArgumentOutOfRangeException(nameof(Thickness), "Thickness must be a non-negative finite number.");

        if (transform == null || Points == null || Points.Count < 4) return false;

        if (double.IsNaN(screenPoint.X) || double.IsNaN(screenPoint.Y) ||
            double.IsInfinity(screenPoint.X) || double.IsInfinity(screenPoint.Y))
            return false;

        var s0 = transform.ChartToScreen(Points[0]);
        var s1 = transform.ChartToScreen(Points[1]);
        var s2 = transform.ChartToScreen(Points[2]);
        var s3 = transform.ChartToScreen(Points[3]);

        float s0X = (float)s0.X, s0Y = (float)s0.Y;
        float s1X = (float)s1.X, s1Y = (float)s1.Y;
        float s2X = (float)s2.X, s2Y = (float)s2.Y;
        float s3X = (float)s3.X, s3Y = (float)s3.Y;

        // IEEE 754 Safety Guard
        if (float.IsNaN(s0X) || float.IsNaN(s0Y) || float.IsInfinity(s0X) || float.IsInfinity(s0Y) ||
            float.IsNaN(s1X) || float.IsNaN(s1Y) || float.IsInfinity(s1X) || float.IsInfinity(s1Y) ||
            float.IsNaN(s2X) || float.IsNaN(s2Y) || float.IsInfinity(s2X) || float.IsInfinity(s2Y) ||
            float.IsNaN(s3X) || float.IsNaN(s3Y) || float.IsInfinity(s3X) || float.IsInfinity(s3Y))
        {
            return false;
        }

        double effTol = tolerance + (Thickness / 2.0);

        SKPoint s0Pt = new SKPoint(s0X, s0Y);
        SKPoint s1Pt = new SKPoint(s1X, s1Y);
        SKPoint c1 = new SKPoint((s0X + 2f * s2X) / 3f, (s0Y + 2f * s2Y) / 3f);
        SKPoint c2 = new SKPoint((s1X + 2f * s2X) / 3f, (s1Y + 2f * s2Y) / 3f);

        float smidY = 0.25f * s0Y + 0.5f * s2Y + 0.25f * s1Y;
        float deltaY = s3Y - smidY;

        SKPoint sPrime0 = new SKPoint(s0X, s0Y + deltaY);
        SKPoint sPrime1 = new SKPoint(s1X, s1Y + deltaY);
        SKPoint cPrime1 = new SKPoint(c1.X, c1.Y + deltaY);
        SKPoint cPrime2 = new SKPoint(c2.X, c2.Y + deltaY);

        float halfDeltaY = 0.5f * deltaY;
        SKPoint sDoublePrime0 = new SKPoint(s0X, s0Y + halfDeltaY);
        SKPoint sDoublePrime1 = new SKPoint(s1X, s1Y + halfDeltaY);
        SKPoint cDoublePrime1 = new SKPoint(c1.X, c1.Y + halfDeltaY);
        SKPoint cDoublePrime2 = new SKPoint(c2.X, c2.Y + halfDeltaY);

        // Bounding box encompassing both base and parallel curves
        float minX = Math.Min(s0X, Math.Min(s1X, s2X));
        float maxX = Math.Max(s0X, Math.Max(s1X, s2X));
        float minY = Math.Min(Math.Min(s0Y, s1Y), Math.Min(s2Y, Math.Min(s0Y + deltaY, Math.Min(s1Y + deltaY, s2Y + deltaY))));
        float maxY = Math.Max(Math.Max(s0Y, s1Y), Math.Max(s2Y, Math.Max(s0Y + deltaY, Math.Max(s1Y + deltaY, s2Y + deltaY))));

        if (screenPoint.X < minX - effTol || screenPoint.X > maxX + effTol ||
            screenPoint.Y < minY - effTol || screenPoint.Y > maxY + effTol)
        {
            return false;
        }

        // Reconstruct band path for polygon containment check
        _cachedPath.Rewind();
        _cachedPath.FillType = SKPathFillType.Winding;
        _cachedPath.MoveTo(s0Pt);
        _cachedPath.CubicTo(c1, c2, s1Pt);
        _cachedPath.LineTo(sPrime1);
        _cachedPath.CubicTo(cPrime2, cPrime1, sPrime0);
        _cachedPath.Close();

        SKPoint skPt = new SKPoint((float)screenPoint.X, (float)screenPoint.Y);

        // 1. Band Interior Check
        if (_cachedPath.Contains(skPt.X, skPt.Y))
        {
            return true;
        }

        // 2. Base Curve Segment Check
        if (BezierSplineMath.HitTestCubicSegment(skPt, s0Pt, c1, c2, s1Pt, effTol))
        {
            return true;
        }

        // 3. Parallel Curve Segment Check
        if (BezierSplineMath.HitTestCubicSegment(skPt, sPrime0, cPrime1, cPrime2, sPrime1, effTol))
        {
            return true;
        }

        // 4. Center Curve Segment Check
        if (BezierSplineMath.HitTestCubicSegment(skPt, sDoublePrime0, cDoublePrime1, cDoublePrime2, sDoublePrime1, effTol))
        {
            return true;
        }

        return false;
    }

    public override void Dispose()
    {
        _fillPaint?.Dispose();
        _centerPaint?.Dispose();
        _controlPolygonPaint?.Dispose();
        _cachedCenterPath?.Dispose();
        base.Dispose();
    }
}
