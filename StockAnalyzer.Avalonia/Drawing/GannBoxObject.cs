using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Views.Chart;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Gann Box Tool
/// Draws a grid structure based on Price and Time ranges defined by start and end points.
/// Typically divides the range into 8ths or other harmonic ratios.
/// </summary>
public class GannBoxObject : IChartObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.GannBox;
    
    public List<ChartPoint> Points { get; private set; }
    public Color Color { get; set; } = Colors.Green;
    public double Thickness { get; set; } = 1.0;
    public bool IsSelected { get; set; }

    // Ratios for grid lines (Standard Gann 8ths)
    private readonly double[] _gridRatios = { 0.125, 0.25, 0.375, 0.5, 0.625, 0.75, 0.875 };

    public GannBoxObject(ChartPoint start, ChartPoint end)
    {
        Points = new List<ChartPoint> { start, end };
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count < 2) return;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        float x = (float)Math.Min(p1.X, p2.X);
        float y = (float)Math.Min(p1.Y, p2.Y);
        float w = (float)Math.Abs(p2.X - p1.X);
        float h = (float)Math.Abs(p2.Y - p1.Y);

        if (w < 1 || h < 1) return;

        using var borderPaint = new SKPaint
        {
            Color = SkiaColor,
            StrokeWidth = (float)Thickness,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };
        
        using var gridPaint = new SKPaint
        {
            Color = SkiaColor.WithAlpha(100), // Original: 128?
            StrokeWidth = 1,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            PathEffect = SKPathEffect.CreateDash(new float[] { 2, 2 }, 0)
        };

        // Draw Outer Box
        canvas.DrawRect(x, y, w, h, borderPaint);

        // Draw Internal Grid
        foreach (var ratio in _gridRatios)
        {
            float rx = x + w * (float)ratio;
            float ry = y + h * (float)ratio;

            // Vertical
            canvas.DrawLine(rx, y, rx, y + h, gridPaint);
            // Horizontal
            canvas.DrawLine(x, ry, x + w, ry, gridPaint);
        }

        // Draw Diagonals (optional but common in Gann Box)
        // 1x1 Cross (Main Diagonals)
        canvas.DrawLine(x, y, x + w, y + h, borderPaint); // TopLeft to BottomRight
        canvas.DrawLine(x + w, y, x, y + h, borderPaint); // TopRight to BottomLeft

        if (IsSelected)
        {
            using var selPaint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill };
            canvas.DrawCircle((float)p1.X, (float)p1.Y, 5, selPaint);
            canvas.DrawCircle((float)p2.X, (float)p2.Y, 5, selPaint);
        }
    }

    private double Distance(global::Avalonia.Point p1, global::Avalonia.Point p2)
    {
        return Math.Sqrt(Math.Pow(p1.X - p2.X, 2) + Math.Pow(p1.Y - p2.Y, 2));
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 2) return false;
        
        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        if (Distance(screenPoint, p1) <= tolerance * 2) return true;
        if (Distance(screenPoint, p2) <= tolerance * 2) return true;
        
        var rect = new global::Avalonia.Rect(p1, p2);
        return rect.Contains(screenPoint) || rect.Inflate(tolerance).Contains(screenPoint); // Simple hit test for box area
        // Or edge hit test? Let's use Area for Box is easier for selection.
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price + priceDelta);
        }
    }
}
