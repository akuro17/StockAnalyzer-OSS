using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

public class AngleObject : RelativeGeometricRenderer
{
    public override ChartObjectType Type => ChartObjectType.AngleTool;

    private readonly SKPaint _dashPaint;
    private readonly SKPaint _textPaint;
    private readonly SKPaint _arcPaint;

    public AngleObject(ChartPoint p1, ChartPoint p2) : base()
    {
        Points.Add(p1);
        Points.Add(p2);

        _dashPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            PathEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0),
            IsAntialias = true
        };

        _textPaint = new SKPaint
        {
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
        };

        _arcPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = true
        };
    }

    protected override void DrawGeometry(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Points.Count < 2) return;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        // Draw Line using base _cachedPaint
        canvas.DrawLine((float)p1.X, (float)p1.Y, (float)p2.X, (float)p2.Y, _cachedPaint);

        // Draw Horizontal Reference
        _dashPaint.Color = SkiaColor.WithAlpha(128);
        canvas.DrawLine((float)p1.X, (float)p1.Y, (float)p1.X + ChartConstants.AngleHorizontalRefLength, (float)p1.Y, _dashPaint);

        // Calculate Angle
        double deltaX = p2.X - p1.X;
        double deltaY = -(p2.Y - p1.Y);
        double angleRad = Math.Atan2(deltaY, deltaX);
        double angleDeg = angleRad * (180.0 / Math.PI);

        // Draw Angle Text
        string text = $"{angleDeg:F1}°";
        _textPaint.Color = SkiaColor;
        _textPaint.TextSize = DrawingThemeContext.DrawingFontSize;

        float textX = (float)p1.X + ChartConstants.AngleTextOffset;
        float textY = (float)p1.Y - ChartConstants.AngleTextOffset;
        canvas.DrawText(text, textX, textY, _textPaint);

        // Draw Arc
        float radius = ChartConstants.AngleArcRadius;
        _arcPaint.Color = SkiaColor.WithAlpha(100);
        var rect = new SKRect((float)p1.X - radius, (float)p1.Y - radius, (float)p1.X + radius, (float)p1.Y + radius);
        canvas.DrawArc(rect, 0, (float)(-angleDeg), false, _arcPaint);
    }

    public override bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 2) return false;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        return DistancePointToSegment(screenPoint, p1, p2) <= tolerance;
    }

    private static double DistancePointToSegment(global::Avalonia.Point p, global::Avalonia.Point v, global::Avalonia.Point w)
    {
        double l2 = Math.Pow(v.X - w.X, 2) + Math.Pow(v.Y - w.Y, 2);
        if (l2 == 0) return Math.Sqrt(Math.Pow(p.X - v.X, 2) + Math.Pow(p.Y - v.Y, 2));
        double t = ((p.X - v.X) * (w.X - v.X) + (p.Y - v.Y) * (w.Y - v.Y)) / l2;
        t = Math.Max(0, Math.Min(1, t));
        var projection = new global::Avalonia.Point(v.X + t * (w.X - v.X), v.Y + t * (w.Y - v.Y));
        return Math.Sqrt(Math.Pow(p.X - projection.X, 2) + Math.Pow(p.Y - projection.Y, 2));
    }

    public override void Dispose()
    {
        _dashPaint.Dispose();
        _textPaint.Dispose();
        _arcPaint.Dispose();
        base.Dispose();
    }
}
