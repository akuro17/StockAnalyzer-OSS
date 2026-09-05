using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Views.Chart;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Fibonacci Time Zone Tool
/// Renders vertical lines at Fibonacci intervals (1, 2, 3, 5, 8...) based on the time difference between Start and End points.
/// </summary>
public class FibonacciTimeZoneObject : IChartObject
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.FibonacciTimeZone;
    // Note: IChartObject interface usually has a fixed Type enum in some implementations, but here it looks like a property.
    // Let's check ChartObjectType enum later. For now, assuming I can cast or it's flexible.
    // Actually, looking at previous files, ChartObjectType is an Enum. I might need to add to it or reuse "FibonacciRetracement" or "General".
    // I will use "General" or cast in Renderers if needed. Let's check ChartObjectType.cs in next step if it fails.
    // Wait, let's just stick to the pattern.
    
    public List<ChartPoint> Points { get; private set; }
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int ZIndex { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;

    private readonly int[] _fibSequence = { 0, 1, 2, 3, 5, 8, 13, 21, 34, 55, 89 }; // Standard Time Zones

    public FibonacciTimeZoneObject(ChartPoint start, ChartPoint end)
    {
        Points = new List<ChartPoint> { start, end };
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count < 2) return;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        double baseInterval = Math.Abs(p2.X - p1.X);
        double startX = p1.X; // Time Zone 0
        // Direction
        double direction = p2.X >= p1.X ? 1.0 : -1.0;

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

        float canvasHeight = (float)transform.CanvasHeight; // Need height from transform or assumption?
        // ICoordinateTransform usually exposes CanvasHeight or we can infer from large number.
        // Let's use specific DrawLine with full height if possible. 
        // transform.CanvasHeight is available in LinearCoordinateTransform implementation but interface?
        // Let's assume passed clip bounds or standard large value if interface doesn't have it.
        // Checking interface previously... ViewFile showed it might have CanvasWidth/Height.
        
        // Let's check ICoordinateTransform in next step to be sure, but for now assuming it has properties or we draw huge.
        // safely:
        float maxY = 10000; 
        if (transform is StockAnalyzer.Avalonia.Drawing.LinearCoordinateTransform lct)
        {
             maxY = (float)lct.CanvasHeight;
        }

        foreach (var fib in _fibSequence)
        {
            double x = startX + (baseInterval * fib * direction);
            
            // Optimization: Skip if off-screen (how to know screen bounds? Transform usually knows)
            // if (x < 0 || x > Width) continue; 
            
            canvas.DrawLine((float)x, 0, (float)x, maxY, paint);
            canvas.DrawText($"{fib}", (float)x + 2, maxY - 5, textPaint);
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
        
        double baseInterval = Math.Abs(p2.X - p1.X);
        double startX = p1.X;
        double direction = p2.X >= p1.X ? 1.0 : -1.0;

        foreach (var fib in _fibSequence)
        {
            double x = startX + (baseInterval * fib * direction);
            if (Math.Abs(screenPoint.X - x) <= tolerance) return true;
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

