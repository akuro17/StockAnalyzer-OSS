using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

public class TriangleObject : IChartObject
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.Triangle;
    public List<ChartPoint> Points { get; } = new List<ChartPoint>();
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int ZIndex { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;

    public TriangleObject(ChartPoint p1, ChartPoint p2, ChartPoint p3)
    {
        Points.Add(p1);
        Points.Add(p2);
        Points.Add(p3);
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Points.Count < 3) return;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);
        var p3 = transform.ChartToScreen(Points[2]);

        using var paint = new SKPaint
        {
            Color = SkiaColor,
            StrokeWidth = (float)Thickness,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };

        using var path = new SKPath();
        path.MoveTo((float)p1.X, (float)p1.Y);
        path.LineTo((float)p2.X, (float)p2.Y);
        path.LineTo((float)p3.X, (float)p3.Y);
        path.Close();

        canvas.DrawPath(path, paint);

        if (IsSelected)
        {
            SelectionHandleRenderer.Draw(canvas, p1, AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
            SelectionHandleRenderer.Draw(canvas, p2, AnchorPointIndex == 1 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
            SelectionHandleRenderer.Draw(canvas, p3, AnchorPointIndex == 2 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 3) return false;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);
        var p3 = transform.ChartToScreen(Points[2]);

        // Check distance to any of the 3 segments
        if (DistancePointToSegment(screenPoint, p1, p2) <= tolerance) return true;
        if (DistancePointToSegment(screenPoint, p2, p3) <= tolerance) return true;
        if (DistancePointToSegment(screenPoint, p3, p1) <= tolerance) return true;

        return false;
    }

    private double DistancePointToSegment(global::Avalonia.Point p, global::Avalonia.Point v, global::Avalonia.Point w)
    {
        double l2 = (v.X - w.X) * (v.X - w.X) + (v.Y - w.Y) * (v.Y - w.Y);
        if (l2 == 0) return Math.Sqrt((p.X - v.X) * (p.X - v.X) + (p.Y - v.Y) * (p.Y - v.Y));
        double t = ((p.X - v.X) * (w.X - v.X) + (p.Y - v.Y) * (w.Y - v.Y)) / l2;
        t = Math.Max(0, Math.Min(1, t));
        global::Avalonia.Point projection = new global::Avalonia.Point(v.X + t * (w.X - v.X), v.Y + t * (w.Y - v.Y));
        return Math.Sqrt((p.X - projection.X) * (p.X - projection.X) + (p.Y - projection.Y) * (p.Y - projection.Y));
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price + priceDelta);
        }
    }
}

