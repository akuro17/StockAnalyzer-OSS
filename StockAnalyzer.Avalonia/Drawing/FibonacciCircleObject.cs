using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Views.Chart;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Fibonacci Circle Tool
/// Renders concentric circles based on the screen distance between Start and End points.
/// </summary>
public class FibonacciCircleObject : IChartObject
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.FibonacciCircle;

    public List<ChartPoint> Points { get; private set; }
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int ZIndex { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;

    private readonly double[] _fibLevels = { 0.236, 0.382, 0.5, 0.618, 0.786, 1.0, 1.618, 2.618 };

    public FibonacciCircleObject(ChartPoint center, ChartPoint radiusEdge)
    {
        Points = new List<ChartPoint> { center, radiusEdge };
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count < 2) return;

        var center = transform.ChartToScreen(Points[0]);
        var edge = transform.ChartToScreen(Points[1]);

        // Calculate radius in Screen Space (Pixels)
        double dx = edge.X - center.X;
        double dy = edge.Y - center.Y;
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

        foreach (var level in _fibLevels)
        {
            float r = (float)(baseRadius * level);
            canvas.DrawCircle((float)center.X, (float)center.Y, r, paint);
             
            // Draw Label near the arc
            double dist = Math.Sqrt(dx*dx + dy*dy);
            if (dist > 0)
            {
                 float lx = (float)(center.X + (dx / dist) * r);
                 float ly = (float)(center.Y + (dy / dist) * r);
                 canvas.DrawText($"{level:P1}", lx, ly, textPaint);
            }
        }

        if (IsSelected)
        {
            SelectionHandleRenderer.Draw(canvas, center, AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
            SelectionHandleRenderer.Draw(canvas, edge, AnchorPointIndex == 1 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 2) return false;

        var center = transform.ChartToScreen(Points[0]);
        var edge = transform.ChartToScreen(Points[1]);
        
        double dx = edge.X - center.X;
        double dy = edge.Y - center.Y;
        double baseRadius = Math.Sqrt(dx * dx + dy * dy);

        double clickDist = Math.Sqrt(Math.Pow(screenPoint.X - center.X, 2) + Math.Pow(screenPoint.Y - center.Y, 2));

        foreach (var level in _fibLevels)
        {
            double r = baseRadius * level;
            if (Math.Abs(clickDist - r) <= tolerance) return true;
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

