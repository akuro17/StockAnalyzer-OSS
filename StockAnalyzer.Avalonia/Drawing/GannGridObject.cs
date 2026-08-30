using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Views.Chart;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Gann Grid Tool
/// Draws a grid of lines based on a 1x1 angle defined by two points.
/// </summary>
public class GannGridObject : IChartObject
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.GannGrid;
    
    public List<ChartPoint> Points { get; private set; }
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int ZIndex { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;

    public GannGridObject(ChartPoint start, ChartPoint end)
    {
        Points = new List<ChartPoint> { start, end };
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count < 2) return;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        // Screen logic for grid
        // Calculate cell size
        float dx = (float)Math.Abs(p2.X - p1.X);
        float dy = (float)Math.Abs(p2.Y - p1.Y);

        if (dx < 1 || dy < 1) return;

        using var paint = new SKPaint
        {
            Color = SkiaColor,
            StrokeWidth = (float)Thickness,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };
        
        using var dimPaint = new SKPaint
        {
            Color = SkiaColor.WithAlpha(100),
            StrokeWidth = 1,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };

        var clip = canvas.LocalClipBounds;

        // Draw Reference Line (P1 to P2 diagonal)
        // Actually, Gann Grid usually means a full grid covering the screen based on this cell.
        // We will draw lines parallel to the diagonal and the anti-diagonal.
        
        // Slope m = dy / dx (in screen coords, Y is down, but let's just use abs offset)
        // Logic:
        // Parallel Ups: y = m(x - x0) + y0 + k*dy
        // Parallel Downs: y = -m(x - x0) + y0 + k*dy
        
        // We need to cover the clip bounds.
        // Let's iterate enough to cover the screen.
        
        // Horizontal and Vertical Grid lines aligned to P1
        // Horizontal: y = p1.y + k * dy
        // Vertical: x = p1.x + k * dx

        // Calculate K range
        // Vertical lines
        int kMinX = (int)((clip.Left - p1.X) / dx) - 1;
        int kMaxX = (int)((clip.Right - p1.X) / dx) + 1;

        for (int k = kMinX; k <= kMaxX; k++)
        {
            float x = (float)(p1.X + k * dx);
            canvas.DrawLine(x, clip.Top, x, clip.Bottom, dimPaint);
        }

        // Horizontal lines
        int kMinY = (int)((clip.Top - p1.Y) / dy) - 1;
        int kMaxY = (int)((clip.Bottom - p1.Y) / dy) + 1;
        
        for (int k = kMinY; k <= kMaxY; k++)
        {
            float y = (float)(p1.Y + k * dy);
            canvas.DrawLine(clip.Left, y, clip.Right, y, dimPaint);
        }
        
        // Diagonal Lines logic could be added here if "Gann Grid" implies the diagonal mesh.
        // Standard Gann Grid is often just the squared grid or diagonal grid.
        // Let's stick to the 45-degree diagonals (based on the rect aspect ratio) crossing the intersections.
        
        // Diagonals: y = (dy/dx)*x + C
        // We iterate K to shift intercepts.
        
        float slope = dy / dx; // Screen slope magnitude
        
        // Up-Right diagonals (screen Y decreases as X increases) -> real slope is negative in screen coords if we assume standard cartesian, but here
        // Let's just draw lines passing through grid intersections.
        // y - y0 = -slope * (x - x0)  => y = -slope*x + (y0 + slope*x0)
        // With offset K*dy:
        
        // We can simplify: Draw line from (x, y) to (x+dx, y-dy) for every grid node? Too expensive.
        // Better: Draw full lines across screen.
        
        // Line type 1: Top-Left to Bottom-Right (Screen coords, Y increases)
        // y = slope * x + C
        // Intercept C varies by K * dy (or projected K * dx).
        // C = y - slope * x
        
        // We iterate offsets relative to P1.
        // k range needs to cover screen.
        int diagKMin = kMinY - kMaxX; // Rough estimate range
        int diagKMax = kMaxY - kMinX;
        
        for (int k = diagKMin * 2; k <= diagKMax * 2; k++)
        {
             // Line passing through (p1.X, p1.Y + k * dy) with slope
             // y = slope * (x - p1.X) + p1.Y + k * dy
             
             // Find intersection with clip bounds
             // Left: x = clip.Left -> y = slope * (clip.Left - p1.X) + p1.Y + k * dy
             // Right: x = clip.Right
             
             float yAtLeft = slope * (clip.Left - (float)p1.X) + (float)p1.Y + k * dy;
             float yAtRight = slope * (clip.Right - (float)p1.X) + (float)p1.Y + k * dy;
             
             canvas.DrawLine(clip.Left, yAtLeft, clip.Right, yAtRight, dimPaint);
        }
        
        // Line type 2: Bottom-Left to Top-Right
        // y = -slope * x + C
        for (int k = diagKMin * 2; k <= diagKMax * 2; k++)
        {
             float yAtLeft = -slope * (clip.Left - (float)p1.X) + (float)p1.Y + k * dy;
             float yAtRight = -slope * (clip.Right - (float)p1.X) + (float)p1.Y + k * dy;
             
             canvas.DrawLine(clip.Left, yAtLeft, clip.Right, yAtRight, dimPaint);
        }

        if (IsSelected)
        {
            SelectionHandleRenderer.Draw(canvas, p1, AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null, radius: ChartConstants.SelectedHandleRadius);
            SelectionHandleRenderer.Draw(canvas, p2, AnchorPointIndex == 1 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null, radius: ChartConstants.SelectedHandleRadius);
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        // Render() draws a repeating grid+diagonal pattern across the ENTIRE visible chart
        // (not just the small P1-P2 box used to define cell size). HitTest must therefore
        // check proximity to the nearest rendered line across the same extent, rather than
        // only accepting clicks inside the original defining rectangle.
        if (Points.Count < 2) return false;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        double dx = Math.Abs(p2.X - p1.X);
        double dy = Math.Abs(p2.Y - p1.Y);
        if (dx < 1 || dy < 1) return false; // Matches Render()'s early-return guard

        // Perpendicular distance from `offset` to the nearest line in a family of
        // parallel lines spaced `spacing` apart.
        double DistanceToNearestLine(double offset, double spacing)
        {
            double remainder = offset % spacing;
            if (remainder < 0) remainder += spacing;
            return Math.Min(remainder, spacing - remainder);
        }

        // Vertical grid lines (x = p1.X + k*dx)
        if (DistanceToNearestLine(screenPoint.X - p1.X, dx) <= tolerance) return true;

        // Horizontal grid lines (y = p1.Y + k*dy)
        if (DistanceToNearestLine(screenPoint.Y - p1.Y, dy) <= tolerance) return true;

        double slope = dy / dx;
        double perpToleranceFactor = Math.Sqrt(1 + slope * slope);

        // Positive-slope diagonals: (y - p1.Y) - slope*(x - p1.X) is spaced dy apart
        double v1 = (screenPoint.Y - p1.Y) - slope * (screenPoint.X - p1.X);
        if (DistanceToNearestLine(v1, dy) <= tolerance * perpToleranceFactor) return true;

        // Negative-slope diagonals: (y - p1.Y) + slope*(x - p1.X) is spaced dy apart
        double v2 = (screenPoint.Y - p1.Y) + slope * (screenPoint.X - p1.X);
        if (DistanceToNearestLine(v2, dy) <= tolerance * perpToleranceFactor) return true;

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

