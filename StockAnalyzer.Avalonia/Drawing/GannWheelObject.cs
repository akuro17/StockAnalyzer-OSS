using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Views.Chart;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Gann Wheel Tool (Hexagon Chart / Circle Chart)
/// Renders Time/Price cycles as concentric circles and radial angles.
/// </summary>
public class GannWheelObject : IChartObject
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.GannWheel;
    
    public List<ChartPoint> Points { get; private set; }
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int ZIndex { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;

    public GannWheelObject(ChartPoint center, ChartPoint radiusPoint)
    {
        Points = new List<ChartPoint> { center, radiusPoint };
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count < 2) return;

        var center = transform.ChartToScreen(Points[0]);
        var radiusP = transform.ChartToScreen(Points[1]);

        // Calculate Radius in screen coordinates
        float rx = (float)Math.Abs(radiusP.X - center.X);
        float ry = (float)Math.Abs(radiusP.Y - center.Y);

        if (rx < 1 || ry < 1) return;

        using var paint = new SKPaint
        {
            Color = SkiaColor,
            StrokeWidth = (float)Thickness,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };

        using var radialPaint = new SKPaint
        {
            Color = SkiaColor.WithAlpha(100),
            StrokeWidth = 1,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            PathEffect = SKPathEffect.CreateDash(new float[] { 2, 2 }, 0)
        };

        // Draw Concentric Circles/Ellipses
        // Subdivisions: 0.382, 0.5, 0.618, 1.0, 1.618 (Fibonacci/Gann style)
        // Or specific Gann integers? Let's use simple geometric divisions for now: 1/3, 1/2, 2/3, 1.0
        double[] ratios = { 0.333, 0.5, 0.666, 1.0 }; 

        foreach (var r in ratios)
        {
            float currentRx = rx * (float)r;
            float currentRy = ry * (float)r;
            var rect = new SKRect((float)center.X - currentRx, (float)center.Y - currentRy, (float)center.X + currentRx, (float)center.Y + currentRy);
            canvas.DrawOval(rect, paint);
        }

        // Draw Radial Lines (Angles)
        // 0, 30, 60, 90, ...
        // We need to draw from Center to the outer ellipse edge.
        // x = cx + rx * cos(theta)
        // y = cy + ry * sin(theta)
        
        for (int angle = 0; angle < 360; angle += 30)
        {
            double rad = angle * Math.PI / 180.0;
            float endX = (float)(center.X + rx * Math.Cos(rad));
            float endY = (float)(center.Y + ry * Math.Sin(rad));
            
            canvas.DrawLine((float)center.X, (float)center.Y, endX, endY, radialPaint);
        }
        
        // Emphasize Main Cross (0, 90, 180, 270)
        canvas.DrawLine((float)center.X - rx, (float)center.Y, (float)center.X + rx, (float)center.Y, paint);
        canvas.DrawLine((float)center.X, (float)center.Y - ry, (float)center.X, (float)center.Y + ry, paint);


        if (IsSelected)
        {
            SelectionHandleRenderer.Draw(canvas, center, AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null, radius: ChartConstants.SelectedHandleRadius);
            SelectionHandleRenderer.Draw(canvas, radiusP, AnchorPointIndex == 1 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null, radius: ChartConstants.SelectedHandleRadius);
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 2) return false;
        
        var center = transform.ChartToScreen(Points[0]);
        var radiusP = transform.ChartToScreen(Points[1]);

        float rx = (float)Math.Abs(radiusP.X - center.X);
        float ry = (float)Math.Abs(radiusP.Y - center.Y);

        // Ellipse hit test is complex, but checking if point is roughly on the ellipse or inside is easier.
        // Let's check bounding box for simplicity first, or just distance from center normalized.
        // (dx/rx)^2 + (dy/ry)^2 <= 1 ?

        double dx = screenPoint.X - center.X;
        double dy = screenPoint.Y - center.Y;

        double normalizedDist = (dx * dx) / (rx * rx) + (dy * dy) / (ry * ry);

        // Hit if inside or near edge? Usually select by clicking edge or inside.
        // Let's accept if inside the 1.0 ellipse
        return normalizedDist <= 1.0;
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price + priceDelta);
        }
    }
}

