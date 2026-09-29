namespace StockAnalyzer.Avalonia.Drawing;

using System;
using System.Collections.Generic;
using global::Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

/// <summary>
/// NURBS Ellipse drawing object using exact 9-point quadratic rational B-spline.
/// Uses 2 anchor points: Points[0] = Center, Points[1] = Corner (Semi-axes extent).
/// Default color matches Arrow tool (Colors.Green).
/// </summary>
public class NurbsEllipseObject : RelativeGeometricRenderer, INurbsConicShapeObject
{
    public override ChartObjectType Type => ChartObjectType.NurbsEllipse;

    public bool IsFilled { get; set; } = false;
    public Color FillColor { get; set; } = Color.FromArgb(40, Colors.Green.R, Colors.Green.G, Colors.Green.B);

    private readonly SKPaint _fillPaint;

    public NurbsEllipseObject() : base()
    {
        Color = Colors.Green;
        _fillPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
    }

    public NurbsEllipseObject(ChartPoint center, ChartPoint corner) : this()
    {
        Points.Add(center);
        Points.Add(corner);
    }

    public NurbsEllipseObject(IEnumerable<ChartPoint> points) : this()
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
        var s1 = transform.ChartToScreen(Points[1]); // Corner

        float rx = MathF.Abs((float)(s1.X - s0.X));
        float ry = MathF.Abs((float)(s1.Y - s0.Y));
        if (rx < 0.5f || ry < 0.5f) return;

        Span<SKPoint> ctrlPts = stackalloc SKPoint[9];
        NurbsConicFactory.CalculateEllipseControlPoints(new SKPoint((float)s0.X, (float)s0.Y), rx, ry, ctrlPts);

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

        double rx = Math.Abs(s1.X - s0.X);
        double ry = Math.Abs(s1.Y - s0.Y);
        if (rx < 0.5 || ry < 0.5) return false;

        double pdx = screenPoint.X - s0.X;
        double pdy = screenPoint.Y - s0.Y;

        double normX = pdx / rx;
        double normY = pdy / ry;
        double normDist = Math.Sqrt(normX * normX + normY * normY);

        double avgRadius = (rx + ry) / 2.0;
        double effectiveTolerance = (tolerance + (Thickness / 2.0)) / Math.Max(1.0, avgRadius);

        if (IsFilled)
        {
            return normDist <= 1.0 + effectiveTolerance;
        }

        return Math.Abs(normDist - 1.0) <= effectiveTolerance;
    }

    public override void Dispose()
    {
        _fillPaint?.Dispose();
        base.Dispose();
    }
}
