using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

public class ArrowObject : RelativeGeometricRenderer
{
    public override ChartObjectType Type => ChartObjectType.Arrow;

    private readonly SKPaint _fillPaint;

    public ArrowObject(ChartPoint tail, ChartPoint head) : base()
    {
        Points.Add(tail);
        Points.Add(head);

        _fillPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
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

        // Draw Arrow Head using base _cachedPath and _fillPaint
        DrawArrowHead(canvas, p1, p2);
    }

    private void DrawArrowHead(SKCanvas canvas, global::Avalonia.Point p1, global::Avalonia.Point p2)
    {
        float arrowSize = ChartConstants.ArrowHeadSize;
        double angle = Math.Atan2(p2.Y - p1.Y, p2.X - p1.X);

        // Arrow Head Points
        float x1 = (float)(p2.X - arrowSize * Math.Cos(angle - Math.PI / 6));
        float y1 = (float)(p2.Y - arrowSize * Math.Sin(angle - Math.PI / 6));
        float x2 = (float)(p2.X - arrowSize * Math.Cos(angle + Math.PI / 6));
        float y2 = (float)(p2.Y - arrowSize * Math.Sin(angle + Math.PI / 6));

        _cachedPath.Reset();
        _cachedPath.MoveTo((float)p2.X, (float)p2.Y);
        _cachedPath.LineTo(x1, y1);
        _cachedPath.LineTo(x2, y2);
        _cachedPath.Close();

        _fillPaint.Color = _cachedPaint.Color;
        canvas.DrawPath(_cachedPath, _fillPaint);
    }

    public override bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 2) return false;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);
        
        // Distance from point to line segment
        double dist = DistancePointToSegment(screenPoint, p1, p2);
        return dist <= tolerance;
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
        _fillPaint.Dispose();
        base.Dispose();
    }
}
