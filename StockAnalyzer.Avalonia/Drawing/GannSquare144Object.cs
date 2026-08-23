using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Views.Chart;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Gann Square of 144 Tool
/// Represents a time/price square (typically 144 units).
/// Renders diagonals and harmonic divisions within the box defined by two points.
/// </summary>
public class GannSquare144Object : IChartObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.GannSquare144;
    
    public List<ChartPoint> Points { get; private set; }
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int ZIndex { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;

    public GannSquare144Object(ChartPoint start, ChartPoint end)
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
        
        using var innerPaint = new SKPaint
        {
            Color = SkiaColor.WithAlpha(100),
            StrokeWidth = 1,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            PathEffect = SKPathEffect.CreateDash(new float[] { 4, 4 }, 0)
        };

        // Draw Box
        canvas.DrawRect(x, y, w, h, borderPaint);

        // Draw Diagonals (The "X")
        canvas.DrawLine(x, y, x + w, y + h, innerPaint);
        canvas.DrawLine(x + w, y, x, y + h, innerPaint);

        // Draw Mid-lines (The "+")
        float midX = x + w / 2;
        float midY = y + h / 2;
        canvas.DrawLine(midX, y, midX, y + h, innerPaint);
        canvas.DrawLine(x, midY, x + w, midY, innerPaint);

        // Draw Internal Harmonics/Arcs (Specific to Square 144)
        // Usually involves an inscribed circle or arcs from corners
        // Let's draw an inscribed ellipse to represent the cycle
        using var arcPaint = new SKPaint
        {
            Color = SkiaColor.WithAlpha(80),
            StrokeWidth = 1,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };
        canvas.DrawOval(new SKRect(x, y, x + w, y + h), arcPaint);

        // 1/3 and 2/3 lines (Gann loves thirds)
        float oneThirdW = w / 3;
        float twoThirdsW = w * 2 / 3;
        float oneThirdH = h / 3;
        float twoThirdsH = h * 2 / 3;

        canvas.DrawLine(x + oneThirdW, y, x + oneThirdW, y + h, innerPaint);
        canvas.DrawLine(x + twoThirdsW, y, x + twoThirdsW, y + h, innerPaint);
        canvas.DrawLine(x, y + oneThirdH, x + w, y + oneThirdH, innerPaint);
        canvas.DrawLine(x, y + twoThirdsH, x + w, y + twoThirdsH, innerPaint);

        if (IsSelected)
        {
            SelectionHandleRenderer.Draw(canvas, p1, AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null, radius: ChartConstants.SelectedHandleRadius);
            SelectionHandleRenderer.Draw(canvas, p2, AnchorPointIndex == 1 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null, radius: ChartConstants.SelectedHandleRadius);
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 2) return false;
        
        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);
        
        var rect = new global::Avalonia.Rect(
            new global::Avalonia.Point(Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y)),
            new global::Avalonia.Size(Math.Abs(p2.X - p1.X), Math.Abs(p2.Y - p1.Y)));
        return rect.Contains(screenPoint) || rect.Inflate(tolerance).Contains(screenPoint);
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price + priceDelta);
        }
    }
}

