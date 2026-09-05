using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Views.Chart;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Fibonacci Arc Tool (Real Arcs)
/// Renders semi-circles (arcs) centered at the second point (or first), 
/// extending based on the distance between P1 and P2.
/// Typical implementation: Center at P2. Radii = Distance(P1, P2) * Ratio.
/// </summary>
public class FibonacciArcObject : IChartObject
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.FibonacciArc;

    public List<ChartPoint> Points { get; private set; }
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int ZIndex { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;

    private readonly double[] _fibLevels = { 0.236, 0.382, 0.5, 0.618, 0.786 };

    public FibonacciArcObject(ChartPoint start, ChartPoint end)
    {
        Points = new List<ChartPoint> { start, end };
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count < 2) return;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]); // Center

        // Draw Trendline
        using var linePaint = new SKPaint { Color = SkiaColor, IsAntialias = true, PathEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0) };
        canvas.DrawLine((float)p1.X, (float)p1.Y, (float)p2.X, (float)p2.Y, linePaint);

        var center = p2;
        
        // Radius Base = Distance(P1, P2)
        double dx = p1.X - p2.X;
        double dy = p1.Y - p2.Y;
        double baseRadius = Math.Sqrt(dx * dx + dy * dy);

        using var paint = new SKPaint
        {
            Color = SkiaColor,
            StrokeWidth = (float)Thickness,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };
        
        using var textPaint = new SKPaint
        {
             Color = SkiaColor,
             IsAntialias = true,
             TextSize = DrawingThemeContext.DrawingFontSize
        };

        // Determine orientation based on vertical direction (Trend)
        // Uptrend (P2 is above P1 visually, P2.Y < P1.Y): "Bowl" at bottom. Draw Bottom Semi-Circle (0 to 180).
        // Downtrend (P2 is below P1 visually, P2.Y > P1.Y): "Dome" at top. Draw Top Semi-Circle (180 to 360).
        
        float startAngle = 0;
        
        if (p2.Y < p1.Y)
        {
            // Uptrend - End is Higher (Lower Y)
            // Draw Bottom Half (0 to 180)
            startAngle = 0;
        }
        else
        {
            // Downtrend - End is Lower (Higher Y)
            // Draw Top Half (180 to 360)
            startAngle = 180;
        }
        
        float sweepAngle = 180;
        
        var rect = new SKRect();
        
        foreach (var level in _fibLevels)
        {
            float r = (float)(baseRadius * level);
            rect.Left = (float)(center.X - r);
            rect.Top = (float)(center.Y - r);
            rect.Right = (float)(center.X + r);
            rect.Bottom = (float)(center.Y + r);
            
            canvas.DrawArc(rect, startAngle, sweepAngle, false, paint);
             
            // Label placement: On the trendline or horizontal axis?
            // "Towards Start" - let's put it on the horizontal level of P2 but offset? 
            // Or on the trendline intersection.
            
            // Intersection with Trendline:
            // Vector P2->P1.
            double dist = baseRadius;
            if (dist > 0)
            {
                 // Vector P2 -> P1 normalized
                 double vx = (p1.X - p2.X) / dist;
                 double vy = (p1.Y - p2.Y) / dist;
                 
                 float lx = (float)(center.X + vx * r);
                 float ly = (float)(center.Y + vy * r);
                 canvas.DrawText($"{level:P1}", lx, ly, textPaint);
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

        var p2 = transform.ChartToScreen(Points[1]); // Center
        var p1 = transform.ChartToScreen(Points[0]);
        
        double baseRadius = Math.Sqrt(Math.Pow(p1.X - p2.X, 2) + Math.Pow(p1.Y - p2.Y, 2));
        double clickDist = Math.Sqrt(Math.Pow(screenPoint.X - p2.X, 2) + Math.Pow(screenPoint.Y - p2.Y, 2));

        foreach (var level in _fibLevels)
        {
            double r = baseRadius * level;
            if (Math.Abs(clickDist - r) <= tolerance) 
            {
                // Check "Side" (Top or Bottom)
                if (p2.Y < p1.Y) // Uptrend, Bottom Arcs
                {
                    // Check if point is below P2 (Greater Y)
                    if (screenPoint.Y >= p2.Y - tolerance) return true;
                }
                else // Downtrend, Top Arcs
                {
                    // Check if point is above P2 (Smaller Y)
                    if (screenPoint.Y <= p2.Y + tolerance) return true;
                }
            }
        }

        return false;
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
         for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price + priceDelta);
        }
    }
}

