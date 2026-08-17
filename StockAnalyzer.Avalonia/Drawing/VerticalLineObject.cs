using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

public class VerticalLineObject : IChartObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.VerticalLine;
    public List<ChartPoint> Points { get; } = new List<ChartPoint>();
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }

    // Sync ID for cross-chart sync (Future proofing)
    public string SyncId { get; set; } = Guid.NewGuid().ToString();

    public VerticalLineObject(ChartPoint p1)
    {
        Points.Add(p1);
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Points.Count < 1) return;

        var p1 = transform.ChartToScreen(Points[0]);
        // Only X matters
        float x = (float)p1.X;

        // Draw full height line
        var clip = canvas.LocalClipBounds;
        
        using var paint = new SKPaint
        {
            Color = SkiaColor,
            StrokeWidth = (float)Thickness,
            Style = SKPaintStyle.Stroke,
            IsAntialias = false // Sharp line for vertical
        };

        canvas.DrawLine(x, clip.Top, x, clip.Bottom, paint);

        if (IsSelected)
        {
            // Draw handle near top and bottom or center?
            // Center is safer
            float midY = clip.MidY;
            SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(x, midY), radius: ChartConstants.SelectedHandleRadius);
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 1) return false;

        var p1 = transform.ChartToScreen(Points[0]);
        // Distance is just abs(X - X)
        return Math.Abs(screenPoint.X - p1.X) <= tolerance;
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            // Vertical line only cares about Time
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price); 
        }
    }
}
