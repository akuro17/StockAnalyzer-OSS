namespace StockAnalyzer.Avalonia.Drawing;

using System;
using System.Collections.Generic;
using global::Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

/// <summary>
/// NURBS Conic / Circle drawing object using exact 9-point quadratic rational B-spline.
/// Uses 2 anchor points: Points[0] = Center, Points[1] = Edge/Radius control point.
/// </summary>
public class NurbsConicObject : RelativeGeometricRenderer, INurbsConicShapeObject
{
    public override ChartObjectType Type => ChartObjectType.NurbsConic;

    public bool IsFilled { get; set; } = false;
    public Color FillColor { get; set; } = Color.FromArgb(40, Colors.Green.R, Colors.Green.G, Colors.Green.B);

    private readonly SKPaint _fillPaint;

    public NurbsConicObject() : base()
    {
        Color = Colors.Green;
        _fillPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
    }

    public NurbsConicObject(ChartPoint center, ChartPoint edge) : this()
    {
        Points.Add(center);
        Points.Add(edge);
    }

    public NurbsConicObject(IEnumerable<ChartPoint> points) : this()
    {
        if (points != null)
        {
            Points.AddRange(points);
        }
    }

    protected override void DrawGeometry(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count < 2) return;

        var s0 = transform.ChartToScreen(Points[0]); // Center
        var s1 = transform.ChartToScreen(Points[1]); // Edge

        float dx = (float)(s1.X - s0.X);
        float dy = (float)(s1.Y - s0.Y);
        float radius = MathF.Sqrt(dx * dx + dy * dy);
        if (radius < 0.5f) return;

        Span<SKPoint> ctrlPts = stackalloc SKPoint[9];
        NurbsConicFactory.CalculateCircleControlPoints(new SKPoint((float)s0.X, (float)s0.Y), radius, ctrlPts);

        _cachedPath.Rewind();
        NurbsMath.BuildNurbsPath(_cachedPath, 2, ctrlPts, NurbsConicFactory.CircleWeights, NurbsConicFactory.CircleKnots);

        if (IsFilled)
        {
            var fill = FillColor.A > 0 ? FillColor : Color.FromArgb(40, Color.R, Color.G, Color.B);
            _fillPaint.Color = new SKColor(fill.R, fill.G, fill.B, fill.A);
            canvas.DrawPath(_cachedPath, _fillPaint);
        }

        _cachedPaint.StrokeJoin = SKStrokeJoin.Round;
        _cachedPaint.StrokeCap = SKStrokeCap.Round;
        canvas.DrawPath(_cachedPath, _cachedPaint);
    }

    public override bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (transform == null || Points.Count < 2) return false;

        var s0 = transform.ChartToScreen(Points[0]);
        var s1 = transform.ChartToScreen(Points[1]);

        double dx = s1.X - s0.X;
        double dy = s1.Y - s0.Y;
        double radius = Math.Sqrt(dx * dx + dy * dy);
        if (radius < 0.5) return false;

        double pdx = screenPoint.X - s0.X;
        double pdy = screenPoint.Y - s0.Y;
        double distFromCenter = Math.Sqrt(pdx * pdx + pdy * pdy);

        double effectiveTolerance = tolerance + (Thickness / 2.0);

        if (IsFilled)
        {
            return distFromCenter <= radius + effectiveTolerance;
        }

        return Math.Abs(distFromCenter - radius) <= effectiveTolerance;
    }

    public override void Dispose()
    {
        _fillPaint?.Dispose();
        base.Dispose();
    }
}
