using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Views.Chart;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Fibonacci Fan Tool
/// Renders 3 lines from StartPoint.
/// 1. EndPoint associated line (The 1/1 line?? Actually no, Speed lines are based on Retracement of the Box).
/// Standard Speed Lines: 1/3 and 2/3 and 1/1.
/// Definition: A Trendline is drawn from Low to High.
/// Vertical line at High date.
/// Divided into 3 segments (0.33, 0.66).
/// Lines drawn from Low through these intersection points.
/// </summary>
public class FibonacciFanObject : IChartObject
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.FibonacciFan;

    public List<ChartPoint> Points { get; private set; }
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int ZIndex { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;

    private readonly double[] _ratios = { 0.382, 0.5, 0.618, 1.0 }; 

    public FibonacciFanObject(ChartPoint start, ChartPoint end)
    {
        Points = new List<ChartPoint> { start, end };
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count < 2) return;

        var p1 = transform.ChartToScreen(Points[0]); // Start
        var p2 = transform.ChartToScreen(Points[1]); // End (defines box corner)

        // Box: P1(x1,y1) to (x2,y2).
        // Vertical line is at x2.
        // Height range is y2 - y1.
        // Intersections at x2 with y values: y1 + (y2-y1)*ratio ??
        // In ScreenCoords, Y increases downwards usually.
        // Logic: Price range. 
        // 1.0 line connects P1 and P2.
        // 1/3 line connects P1 and point(x2, y_at_1/3_range).
        
        using var paint = new SKPaint
        {
            Color = SkiaColor,
            StrokeWidth = (float)Thickness,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };

        // Screen Y Difference
        double totalDy = p2.Y - p1.Y;
        
        // Ray Casting: We want to draw BEYOND P2.
        // Bounds of drawing area? 
        // Let's extend to Screen Width/Height or just past P2 significantly.
        // For visual, drawing segment P1->P2 is minimum.
        float maxX = 10000; // Far right
        
        foreach (var ratio in _ratios)
        {
            // Target Y at x2
            double targetY = p1.Y + (totalDy * ratio);
            
            // Draw Line P1 -> (x2, targetY)
            // Extend?
            // Slope m = (targetY - p1.Y) / (p2.X - p1.X) = (totalDy * ratio) / dx
            // y = m(x - x1) + y1
            
            if (Math.Abs(p2.X - p1.X) > 0.1)
            {
                float slope = (float)((targetY - p1.Y) / (p2.X - p1.X));
                float yAtMaxX = (float)(p1.Y + slope * (maxX - p1.X));
                canvas.DrawLine((float)p1.X, (float)p1.Y, maxX, yAtMaxX, paint);
            }
            else
            {
               // Vertical? Just draw line
               canvas.DrawLine((float)p1.X, (float)p1.Y, (float)p2.X, (float)targetY, paint);
            }
        }

        if (IsSelected)
        {
            SelectionHandleRenderer.Draw(canvas, p1, AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
            SelectionHandleRenderer.Draw(canvas, p2, AnchorPointIndex == 1 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
         if (Points.Count < 2) return false;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);
        double totalDy = p2.Y - p1.Y;

        foreach (var ratio in _ratios)
        {
             double targetY = p1.Y + (totalDy * ratio);
             // Segment P1 -> (p2.x, targetY)
             // Check DistanceToSegment
             if (Math.Abs(p2.X - p1.X) < 1) continue; 
             
             // Simplest: Check line equation distance
             // ...
             // For minimal impl: Check midpoint or end point proximity not sufficient for line.
             // Need full line distance logic.
             // Implementing approximate hit test: Check if point is near the line segment.
             
             global::Avalonia.Point tp = new global::Avalonia.Point(p2.X, targetY);
             if (DistancePointToSegment(screenPoint, p1, tp) <= tolerance) return true;
        }

        return false;
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

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price + priceDelta);
        }
    }
}

