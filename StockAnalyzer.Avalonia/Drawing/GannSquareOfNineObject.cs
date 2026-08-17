using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Views.Chart;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Gann Square of Nine Tool
/// Projects horizontal support/resistance levels based on the Square of Nine formula.
/// Formula: Target = (Sqrt(Price) +/- Factor)^2
/// </summary>
public class GannSquareOfNineObject : IChartObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.GannSquareOfNine;
    
    public List<ChartPoint> Points { get; private set; }
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }

    // Factors corresponding to angles: 45, 90, 135, 180, 225, 270, 315, 360
    // 360 degrees = factor 2.0
    // 180 = 1.0
    // 90 = 0.5
    // 45 = 0.25
    private readonly double[] _factors = { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0 };
    private readonly string[] _labels = { "45°", "90°", "135°", "180°", "225°", "270°", "315°", "360°" };

    public GannSquareOfNineObject(ChartPoint start)
    {
        Points = new List<ChartPoint> { start };
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count < 1) return;

        var startPoint = Points[0];
        double startPrice = (double)startPoint.Price;
        if (startPrice < 0) return; // Price cannot be negative for Sqrt

        double baseSqrt = Math.Sqrt(startPrice);
        var p1 = transform.ChartToScreen(startPoint);
        
        using var paint = new SKPaint
        {
            Color = SkiaColor,
            StrokeWidth = (float)Thickness,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };
        
        using var textPaint = new SKPaint
        {
            Color = SkiaColor,
            TextSize = DrawingThemeContext.DrawingFontSize,
            IsAntialias = true,
            TextAlign = SKTextAlign.Right
        };

        var clip = canvas.LocalClipBounds;

        // Draw Base Line
        float yBase = (float)p1.Y;
        canvas.DrawLine(clip.Left, yBase, clip.Right, yBase, paint);
        canvas.DrawText("Base", clip.Right - 5, yBase - 5, textPaint);

        // Calculate and Draw Levels
        // We draw both Support (Down) and Resistance (Up) if applicable?
        // Usually, if we select a Low, we want Resistance (Up). If High, Support (Down).
        // Let's draw BOTH for versatility, or prioritize Up if no second point to define direction.
        // Or assume Up for now as it's "Square of Nine" usually for projections.
        
        for (int i = 0; i < _factors.Length; i++)
        {
            double factor = _factors[i];
            
            // Resistance (Up)
            double priceUp = Math.Pow(baseSqrt + factor, 2);
            var pUp = transform.ChartToScreen(new ChartPoint(startPoint.Time, (decimal)priceUp));
            
            if (pUp.Y >= clip.Top - 20 && pUp.Y <= clip.Bottom + 20)
            {
                canvas.DrawLine(clip.Left, (float)pUp.Y, clip.Right, (float)pUp.Y, paint);
                canvas.DrawText($"+{_labels[i]} ({priceUp:F2})", clip.Right - 5, (float)pUp.Y - 5, textPaint);
            }

            // Support (Down)
            double priceDown = Math.Pow(baseSqrt - factor, 2);
            if (priceDown > 0)
            {
                var pDown = transform.ChartToScreen(new ChartPoint(startPoint.Time, (decimal)priceDown));
                 if (pDown.Y >= clip.Top - 20 && pDown.Y <= clip.Bottom + 20)
                {
                    canvas.DrawLine(clip.Left, (float)pDown.Y, clip.Right, (float)pDown.Y, paint);
                    canvas.DrawText($"-{_labels[i]} ({priceDown:F2})", clip.Right - 5, (float)pDown.Y - 5, textPaint);
                }
            }
        }

        if (IsSelected)
        {
            SelectionHandleRenderer.Draw(canvas, p1, radius: ChartConstants.SelectedHandleRadius);
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 1) return false;
        
        // Hit test near the base line or the start point
        var p1 = transform.ChartToScreen(Points[0]);
        // Simple hit test on the start point
        if (Math.Abs(screenPoint.X - p1.X) < tolerance && Math.Abs(screenPoint.Y - p1.Y) < tolerance) return true;
        
        // Also hit test on the base line
        // The base line spans the entire width of the chart
        var lineStart = new global::Avalonia.Point(0, p1.Y);
        var lineEnd = new global::Avalonia.Point(transform.ScreenRect.Width, p1.Y);
        if (PointToSegmentDistance(screenPoint, lineStart, lineEnd) < tolerance) return true;

        return false;
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price + priceDelta);
        }
    }
    private static double GetDistance(global::Avalonia.Point a, global::Avalonia.Point b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private static double PointToSegmentDistance(global::Avalonia.Point p, global::Avalonia.Point a, global::Avalonia.Point b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lenSq = dx * dx + dy * dy;

        if (lenSq < 1e-10) return GetDistance(p, a);

        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq, 0, 1);
        var proj = new global::Avalonia.Point(a.X + t * dx, a.Y + t * dy);
        return GetDistance(p, proj);
    }
}
