using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;

namespace StockAnalyzer.Avalonia.Drawing;

public class ArrowObject : IChartObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.Arrow;
    public List<ChartPoint> Points { get; } = new List<ChartPoint>();
    public Color Color { get; set; } = Colors.Red;
    public double Thickness { get; set; } = 2.0;
    public bool IsSelected { get; set; }

    public ArrowObject(ChartPoint tail, ChartPoint head)
    {
        Points.Add(tail);
        Points.Add(head);
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Points.Count < 2) return;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        using var paint = new SKPaint
        {
            Color = SkiaColor,
            StrokeWidth = (float)Thickness,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };

        // Draw Line
        canvas.DrawLine((float)p1.X, (float)p1.Y, (float)p2.X, (float)p2.Y, paint);

        // Draw Arrow Head
        DrawArrowHead(canvas, p1, p2, paint);

        if (IsSelected)
        {
            using var handlePaint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill };
            canvas.DrawCircle((float)p1.X, (float)p1.Y, ChartConstants.DefaultHandleRadius, handlePaint);
            canvas.DrawCircle((float)p2.X, (float)p2.Y, ChartConstants.DefaultHandleRadius, handlePaint);
        }
    }

    private void DrawArrowHead(SKCanvas canvas, global::Avalonia.Point p1, global::Avalonia.Point p2, SKPaint paint)
    {
        float arrowSize = ChartConstants.ArrowHeadSize;
        double angle = Math.Atan2(p2.Y - p1.Y, p2.X - p1.X);

        // Arrow Head Points
        float x1 = (float)(p2.X - arrowSize * Math.Cos(angle - Math.PI / 6));
        float y1 = (float)(p2.Y - arrowSize * Math.Sin(angle - Math.PI / 6));
        float x2 = (float)(p2.X - arrowSize * Math.Cos(angle + Math.PI / 6));
        float y2 = (float)(p2.Y - arrowSize * Math.Sin(angle + Math.PI / 6));

        using var path = new SKPath();
        path.MoveTo((float)p2.X, (float)p2.Y);
        path.LineTo(x1, y1);
        path.LineTo(x2, y2);
        path.Close();

        using var fillPaint = new SKPaint
        {
            Color = paint.Color,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        canvas.DrawPath(path, fillPaint);
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 2) return false;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);
        
        // Distance from point to line segment
        double dist = DistancePointToSegment(screenPoint, p1, p2);
        return dist <= tolerance;
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price + priceDelta);
        }
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
