using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

public class TrendLineObject : RelativeGeometricRenderer
{
    public override ChartObjectType Type => ChartObjectType.TrendLine;

    // FR-39-9-04, FR-39-9-05: User-configurable projection settings
    public bool ShowProjection { get; set; } = false; // Default OFF as requested
    public int ProjectionColumns { get; set; } = 10;

    // FR-39-9-03: ZeroAllocation cached paints for projection and labeling
    private readonly SKPaint _projectionPaint;

    public TrendLineObject(ChartPoint p1, ChartPoint p2) : base()
    {
        Points.Add(p1);
        Points.Add(p2);

        // Pre-allocate projection paint (Dash effect)
        _projectionPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash(new float[] { ChartConstants.DefaultTrendProjectionDashInterval, ChartConstants.DefaultTrendProjectionDashInterval }, 0)
        };
    }

    // SkiaColor is provided by base RelativeGeometricRenderer

    protected override void DrawGeometry(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Points.Count < 2) return;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        // 1. Draw Base TrendLine using base class _cachedPaint
        canvas.DrawLine((float)p1.X, (float)p1.Y, (float)p2.X, (float)p2.Y, _cachedPaint);
        
        // 2. Predictive Forward Projection (FR-39-9-01)
        if (!ShowProjection) return;

        // Determine which point is chronologically later to project forward
        bool p2IsLater = Points[1].Time >= Points[0].Time;
        var startPoint = p2IsLater ? p2 : p1;
        var prevPoint = p2IsLater ? p1 : p2;

        double dx = startPoint.X - prevPoint.X;
        double dy = startPoint.Y - prevPoint.Y;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        if (dist > 0)
        {
            // Calculate logic-based "column width" for projection length
            double columnWidth = (transform.TimeMap != null) 
                ? Math.Abs(transform.NumericToScreen(1, 0).X - transform.NumericToScreen(0, 0).X)
                : transform.ViewportWidth / 80.0;
            
            double projectionLen = columnWidth * ProjectionColumns;

            float ux = (float)(dx / dist);
            float uy = (float)(dy / dist);

            float projEndX = (float)startPoint.X + ux * (float)projectionLen;
            float projEndY = (float)startPoint.Y + uy * (float)projectionLen;

            // Draw dashed projection line
            _projectionPaint.Color = SkiaColor.WithAlpha(ChartConstants.DefaultTrendProjectionAlpha);
            _projectionPaint.StrokeWidth = (float)Thickness;
            canvas.DrawLine((float)startPoint.X, (float)startPoint.Y, projEndX, projEndY, _projectionPaint);
        }
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

    public override void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price + priceDelta);
        }
    }

    public override void Dispose()
    {
        _projectionPaint.Dispose();
        base.Dispose();
    }

    private double DistancePointToSegment(global::Avalonia.Point p, global::Avalonia.Point v, global::Avalonia.Point w)
    {
        double l2 = Math.Pow(v.X - w.X, 2) + Math.Pow(v.Y - w.Y, 2);
        if (l2 == 0) return Math.Sqrt(Math.Pow(p.X - v.X, 2) + Math.Pow(p.Y - v.Y, 2));
        double t = ((p.X - v.X) * (w.X - v.X) + (p.Y - v.Y) * (w.Y - v.Y)) / l2;
        t = Math.Max(0, Math.Min(1, t));
        global::Avalonia.Point projection = new global::Avalonia.Point(v.X + t * (w.X - v.X), v.Y + t * (w.Y - v.Y));
        return Math.Sqrt(Math.Pow(p.X - projection.X, 2) + Math.Pow(p.Y - projection.Y, 2));
    }
}
