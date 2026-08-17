using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

public struct ProjectionTarget
{
    public string Name { get; set; }
    public decimal Price { get; set; }
    public ChartPoint Anchor { get; set; }
}

/// <summary>
/// Interactive drawing tool for projecting target prices based on past price movements.
/// Calculates Equal Distance (N/E), Equal Percentage, and Double Return (V) simultaneously.
/// Uses 3 anchor points for EqualDistance/EqualPercentage, 2 anchor points for DoubleReturn.
/// </summary>
public class TargetPriceProjectionObject : IChartObject, IDisposable
{
    private readonly SKPaint _connectPaint;
    private readonly SKPaint _projectionPaint;
    private readonly SKPaint _textPaint;
    private readonly SKPaint _markerPaint;
    private readonly List<ProjectionTarget> _targetsBuffer = new(5);
    private bool _disposed;

    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.TargetPriceProjection;
    public List<ChartPoint> Points { get; } = new List<ChartPoint>();
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }

    [DisplayName("Font Size")]
    [Category("Appearance")]
    public double FontSize { get; set; } = ChartConstants.ProjectionTextSize;

    [DisplayName("Font Color")]
    [Category("Appearance")]
    public Color FontColor { get; set; } = Colors.Black;

    [DisplayName("Show Equal Distance (N)")]
    [Category("Projection Targets")]
    public bool ShowEqualDistance { get; set; } = true;

    [DisplayName("Show Equal Percentage (%)")]
    [Category("Projection Targets")]
    public bool ShowEqualPercentage { get; set; } = true;

    [DisplayName("Show Double Return (V)")]
    [Category("Projection Targets")]
    public bool ShowDoubleReturn { get; set; } = true;

    [DisplayName("Show E Projection (E)")]
    [Category("Projection Targets")]
    public bool ShowEProjection { get; set; } = true;

    [DisplayName("Show NT Projection (NT)")]
    [Category("Projection Targets")]
    public bool ShowNTProjection { get; set; } = true;

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public TargetPriceProjectionObject()
    {
        _connectPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };

        _projectionPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };

        _textPaint = new SKPaint
        {
            IsAntialias = true
        };

        _markerPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
    }

    public TargetPriceProjectionObject(ChartPoint p1, ChartPoint p2, ChartPoint p3)
        : this()
    {
        Points.Add(p1);
        Points.Add(p2);
        Points.Add(p3);
    }

    /// <summary>
    /// Calculates all enabled target prices based on the anchor points.
    /// Reuses internal buffer to avoid allocations.
    /// </summary>
    public List<ProjectionTarget> CalculateTargetPrices()
    {
        _targetsBuffer.Clear();
        if (Points.Count < 2) return _targetsBuffer;

        if (Points.Count >= 3)
        {
            if (ShowDoubleReturn)
            {
                // V Calculation (Double Return): Target = P2 + (P2 - P3)
                // A=P1, B=P2, C=P3. V = B + (B - C)
                // Anchors from P3 and points in the main trend direction.
                _targetsBuffer.Add(new ProjectionTarget
                {
                    Name = "V",
                    Price = Points[1].Price + (Points[1].Price - Points[2].Price),
                    Anchor = Points[2]
                });
            }
            if (ShowEqualDistance)
            {
                // N: Target = P3.Price + (P2.Price - P1.Price)
                _targetsBuffer.Add(new ProjectionTarget
                {
                    Name = "N",
                    Price = Points[2].Price + (Points[1].Price - Points[0].Price),
                    Anchor = Points[2]
                });
            }

            if (ShowEqualPercentage && Points[0].Price != 0)
            {
                // Percentage: Target = P3.Price * (P2.Price / P1.Price)
                _targetsBuffer.Add(new ProjectionTarget
                {
                    Name = "%",
                    Price = Points[2].Price * (Points[1].Price / Points[0].Price),
                    Anchor = Points[2]
                });
            }

            if (ShowEProjection)
            {
                // E Calculation: Target = P2 + (P2 - P1)
                // Anchors from P2
                _targetsBuffer.Add(new ProjectionTarget
                {
                    Name = "E",
                    Price = Points[1].Price + (Points[1].Price - Points[0].Price),
                    Anchor = Points[1]
                });
            }

            if (ShowNTProjection)
            {
                // NT Calculation: Target = P3 + (P3 - P1)
                // Anchors from P3
                _targetsBuffer.Add(new ProjectionTarget
                {
                    Name = "NT",
                    Price = Points[2].Price + (Points[2].Price - Points[0].Price),
                    Anchor = Points[2]
                });
            }
        }

        return _targetsBuffer;
    }

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Points.Count < 2) return;

        var p1Screen = transform.ChartToScreen(Points[0]);
        var p2Screen = transform.ChartToScreen(Points[1]);

        // Anchor connecting line paint (solid, semi-transparent)
        _connectPaint.Color = SkiaColor.WithAlpha(160);
        _connectPaint.StrokeWidth = (float)Thickness;

        // Solid line paint for projection line
        _projectionPaint.Color = SkiaColor;
        _projectionPaint.StrokeWidth = (float)Thickness + 0.5f;

        // Text paint for labels.
        _textPaint.Color = DrawingThemeContext.TextColor;
        _textPaint.TextSize = DrawingThemeContext.DrawingFontSize;

        _markerPaint.Color = SkiaColor;

        // Draw P1-P2 connecting line
        canvas.DrawLine(
            (float)p1Screen.X, (float)p1Screen.Y,
            (float)p2Screen.X, (float)p2Screen.Y,
            _connectPaint);

        // Draw P2-P3 connecting line (if 3 points exist)
        if (Points.Count >= 3)
        {
            var p3Screen = transform.ChartToScreen(Points[2]);
            canvas.DrawLine(
                (float)p2Screen.X, (float)p2Screen.Y,
                (float)p3Screen.X, (float)p3Screen.Y,
                _connectPaint);
        }

        // Calculate and draw target projections
        var targets = CalculateTargetPrices();
        for (int i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            var anchorScreen = transform.ChartToScreen(target.Anchor);
            
            // Create a virtual chart point at the target price to get the screen Y
            var targetChartPoint = new ChartPoint(target.Anchor.Time, target.Price);
            var targetScreen = transform.ChartToScreen(targetChartPoint);

            // Draw horizontal projection line from anchor X to the right
            float projLineStartX = (float)anchorScreen.X;
            float projLineEndX = projLineStartX + ChartConstants.ProjectionLineExtension;
            float targetY = (float)targetScreen.Y;

            canvas.DrawLine(projLineStartX, targetY, projLineEndX, targetY, _projectionPaint);

            // Draw vertical connector from anchor to target level
            canvas.DrawLine(
                projLineStartX, (float)anchorScreen.Y,
                projLineStartX, targetY,
                _connectPaint);

            // Draw target price label
            string labelText = $"{target.Name}: {target.Price:F2}";
            float labelWidth = _textPaint.MeasureText(labelText);

            // Label text (no background)
            float labelX = projLineStartX - labelWidth - ChartConstants.ProjectionTextOffsetX;
            float labelY = targetY + ChartConstants.ProjectionTextOffsetY;
            canvas.DrawText(labelText, labelX, labelY, _textPaint);

            // Draw small marker at target price point
            canvas.DrawCircle(projLineStartX, targetY, ChartConstants.DefaultHandleRadius - 1, _markerPaint);
        }

        // Draw anchor handles when selected
        if (IsSelected)
        {
            for (int i = 0; i < Points.Count; i++)
            {
                var sp = transform.ChartToScreen(Points[i]);
                SelectionHandleRenderer.Draw(canvas, sp, radius: ChartConstants.SelectedHandleRadius);
            }
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 2) return false;

        double tol2 = tolerance * 2;

        // Check proximity to anchor points
        for (int i = 0; i < Points.Count; i++)
        {
            var sp = transform.ChartToScreen(Points[i]);
            if (GetDistance(screenPoint, sp) < tol2) return true;
        }

        // Check proximity to connecting lines
        var p1s = transform.ChartToScreen(Points[0]);
        var p2s = transform.ChartToScreen(Points[1]);
        if (PointToSegmentDistance(screenPoint, p1s, p2s) < tolerance) return true;

        if (Points.Count >= 3)
        {
            var p3s = transform.ChartToScreen(Points[2]);
            if (PointToSegmentDistance(screenPoint, p2s, p3s) < tolerance) return true;
        }

        // Check proximity to projection lines
        var targets = CalculateTargetPrices();
        for (int i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            var anchorScreen = transform.ChartToScreen(target.Anchor);
            var targetChartPoint = new ChartPoint(target.Anchor.Time, target.Price);
            var targetScreen = transform.ChartToScreen(targetChartPoint);

            float projStartX = (float)anchorScreen.X;
            float projEndX = projStartX + ChartConstants.ProjectionLineExtension;
            float targetY = (float)targetScreen.Y;

            // Check horizontal projection line
            var projStart = new global::Avalonia.Point(projStartX, targetY);
            var projEnd = new global::Avalonia.Point(projEndX, targetY);
            if (PointToSegmentDistance(screenPoint, projStart, projEnd) < tolerance) return true;

            // Check vertical connector
            var connStart = new global::Avalonia.Point(projStartX, anchorScreen.Y);
            var connEnd = new global::Avalonia.Point(projStartX, targetY);
            if (PointToSegmentDistance(screenPoint, connStart, connEnd) < tolerance) return true;
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

    private static double GetDistance(global::Avalonia.Point a, global::Avalonia.Point b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    /// <summary>
    /// Calculates the minimum distance from a point to a line segment.
    /// </summary>
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connectPaint.Dispose();
        _projectionPaint.Dispose();
        _textPaint.Dispose();
        _markerPaint.Dispose();
    }
}
