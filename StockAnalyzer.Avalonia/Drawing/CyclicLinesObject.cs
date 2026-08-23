using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Views.Chart;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Cyclic Lines Tool
/// Draws vertical lines at regular time intervals.
/// </summary>
public class CyclicLinesObject : IChartObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.CyclicLines;

    public List<ChartPoint> Points { get; private set; }
    private double GetDistance(global::Avalonia.Point a, global::Avalonia.Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int ZIndex { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;

    public CyclicLinesObject(ChartPoint start, ChartPoint end)
    {
        Points = new List<ChartPoint> { start, end };
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count < 2) return;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        using var paint = new SKPaint
        {
            Color = SkiaColor,
            StrokeWidth = (float)Thickness,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0), // Dashed for style? Or Solid? Prompt says "Cyclic Lines", typically vertical lines. Solid is fine, or Dotted. Let's use dashed for now to differentiate.
            Style = SKPaintStyle.Stroke
        };

        // Calculate Pixel Interval
        float startX = (float)p1.X;
        float intervalX = Math.Abs((float)(p2.X - p1.X));

        if (intervalX < 5f) return; // Prevent too dense

        var bounds = canvas.LocalClipBounds;
        
        // Draw Forward
        float x = startX;
        int count = 0;
        while (count < 1000) // Safety break
        {
            if (x >= bounds.Left && x <= bounds.Right)
            {
                canvas.DrawLine(x, bounds.Top, x, bounds.Bottom, paint);
            }
            else if (x > bounds.Right)
            {
                break;
            }
            x += intervalX;
            count++;
        }

        // Draw Backward
        x = startX - intervalX;
        count = 0;
        while (count < 1000)
        {
             if (x >= bounds.Left && x <= bounds.Right)
            {
                canvas.DrawLine(x, bounds.Top, x, bounds.Bottom, paint);
            }
            else if (x < bounds.Left)
            {
                break;
            }
            x -= intervalX;
            count++;
        }

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

        float startX = (float)p1.X;
        float intervalX = Math.Abs((float)(p2.X - p1.X));

        if (intervalX < 1f) return false;

        // Check if screenPoint.X is close to any startX + k * intervalX
        float diff = (float)screenPoint.X - startX;
        
        // k = round(diff / interval)
        double k = Math.Round(diff / intervalX);
        float nearestX = startX + (float)k * intervalX;

        return Math.Abs(screenPoint.X - nearestX) <= tolerance;
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price + priceDelta);
        }
    }
}

