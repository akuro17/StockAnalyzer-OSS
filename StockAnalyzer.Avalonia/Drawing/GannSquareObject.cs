using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Views.Chart;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Gann Square Tool
/// Draws a square with internal crosses and diamonds.
/// Often used to square price and time.
/// </summary>
public class GannSquareObject : IChartObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.GannSquare;
    
    public List<ChartPoint> Points { get; private set; }
    public Color Color { get; set; } = Colors.Green;
    public double Thickness { get; set; } = 1.0;
    public bool IsSelected { get; set; }

    public GannSquareObject(ChartPoint start, ChartPoint end)
    {
        Points = new List<ChartPoint> { start, end };
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count < 2) return;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        // Gann Square is usually forced to be a SQUARE in pixels? 
        // Or specific Ratio?
        // Let's implement as a resizable box but visually emphasize the center cross and diamond.
        
        float x = (float)Math.Min(p1.X, p2.X);
        float y = (float)Math.Min(p1.Y, p2.Y);
        float w = (float)Math.Abs(p2.X - p1.X);
        float h = (float)Math.Abs(p2.Y - p1.Y); 
        // If we want a perfect square based on width:
        // h = w; 
        // But user dragging usually defines range. Standard Gann Square fits the range.

        if (w < 1 || h < 1) return;

        using var borderPaint = new SKPaint
        {
            Color = SkiaColor,
            StrokeWidth = (float)Thickness,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };
        
        using var thinPaint = new SKPaint
        {
            Color = SkiaColor.WithAlpha(150),
            StrokeWidth = 1,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };

        using var gridPaint = new SKPaint
        {
            Color = SkiaColor.WithAlpha(80),
            StrokeWidth = 1,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };

        // Draw Outer Box
        canvas.DrawRect(x, y, w, h, borderPaint);

        // Draw Internal Grid (Standard Gann Square often uses 4x4 or 8x8)
        // Let's use 4x4 for visibility combined with Diamond
        int gridDivisions = 4;
        for (int i = 1; i < gridDivisions; i++)
        {
            float ratio = (float)i / gridDivisions;
            float rx = x + w * ratio;
            float ry = y + h * ratio;
            canvas.DrawLine(rx, y, rx, y + h, gridPaint);
            canvas.DrawLine(x, ry, x + w, ry, gridPaint);
        }

        // Main Diagonals (Cross)
        canvas.DrawLine(x, y, x + w, y + h, borderPaint);
        canvas.DrawLine(x + w, y, x, y + h, borderPaint);

        // Mid Points (Cross +)
        float midX = x + w / 2;
        float midY = y + h / 2;
        canvas.DrawLine(midX, y, midX, y + h, thinPaint);
        canvas.DrawLine(x, midY, x + w, midY, thinPaint);

        // Diamond (connecting midpoints of sides)
        canvas.DrawLine(midX, y, x + w, midY, thinPaint);
        canvas.DrawLine(x + w, midY, midX, y + h, thinPaint);
        canvas.DrawLine(midX, y + h, x, midY, thinPaint);
        canvas.DrawLine(x, midY, midX, y, thinPaint);

        if (IsSelected)
        {
            using var selPaint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill };
            canvas.DrawCircle((float)p1.X, (float)p1.Y, 5, selPaint);
            canvas.DrawCircle((float)p2.X, (float)p2.Y, 5, selPaint);
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 2) return false;
        
        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);
        
        float x = (float)Math.Min(p1.X, p2.X);
        float y = (float)Math.Min(p1.Y, p2.Y);
        float w = (float)Math.Abs(p2.X - p1.X);
        float h = (float)Math.Abs(p2.Y - p1.Y);
        
        var rect = new global::Avalonia.Rect(x, y, w, h);
        return rect.Contains(screenPoint); 
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price + priceDelta);
        }
    }
}
