using System;
using System.Collections.Generic;
using global::Avalonia;
using global::Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Fibonacci Ellipse (Phi-Ellipse) drawing object based on Robert Fischer's 3-point model.
/// P0: Trend wave origin (start point).
/// P1: Trend wave terminal (end point). P0-P1 defines the major axis and rotation angle.
/// P2: Amplitude / minor radius reference point (distance to P0-P1 line defines semi-minor axis).
/// Inherits RelativeGeometricRenderer for Zero-Allocation rendering.
/// </summary>
public class FibonacciEllipseObject : RelativeGeometricRenderer
{
    public override ChartObjectType Type => ChartObjectType.FibonacciEllipse;

    public static readonly double[] DefaultFibLevels = { 0.236, 0.382, 0.5, 0.618, 0.786, 1.0, 1.618, 2.618, 4.236 };

    private readonly double[] _fibLevels = DefaultFibLevels;

    // Visual options
    public bool IsFilled { get; set; } = false;
    public byte FillAlpha { get; set; } = 30;
    public bool ShowMedianLine { get; set; } = true;
    public bool ShowLabels { get; set; } = true;

    // Cached paints for Zero-Allocation rendering
    private readonly SKPaint _dashPaint;
    private readonly SKPaint _fillPaint;
    private readonly SKPaint _textPaint;

    public FibonacciEllipseObject() : base()
    {
        Color = DrawingThemeContext.DefaultColor;
        Thickness = DrawingThemeContext.DefaultStrokeThickness;

        _dashPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0)
        };

        _fillPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        _textPaint = new SKPaint
        {
            IsAntialias = true,
            TextSize = DrawingThemeContext.DrawingFontSize
        };
    }

    public FibonacciEllipseObject(ChartPoint p0, ChartPoint p1, ChartPoint p2) : this()
    {
        Points.Add(p0);
        Points.Add(p1);
        Points.Add(p2);
    }

    public FibonacciEllipseObject(IEnumerable<ChartPoint> points) : this()
    {
        if (points != null)
        {
            Points.AddRange(points);
        }
    }

    protected override void DrawGeometry(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count < 2) return;

        var s0 = transform.ChartToScreen(Points[0]);
        var s1 = transform.ChartToScreen(Points[1]);
        var s2 = Points.Count >= 3 ? transform.ChartToScreen(Points[2]) : s1;

        if (double.IsNaN(s0.X) || double.IsNaN(s0.Y) ||
            double.IsNaN(s1.X) || double.IsNaN(s1.Y) ||
            double.IsNaN(s2.X) || double.IsNaN(s2.Y) ||
            double.IsInfinity(s0.X) || double.IsInfinity(s0.Y) ||
            double.IsInfinity(s1.X) || double.IsInfinity(s1.Y) ||
            double.IsInfinity(s2.X) || double.IsInfinity(s2.Y)) return;

        float cX = (float)((s0.X + s1.X) / 2.0);
        float cY = (float)((s0.Y + s1.Y) / 2.0);

        float dx = (float)(s1.X - s0.X);
        float dy = (float)(s1.Y - s0.Y);
        float majorDiameter = MathF.Sqrt(dx * dx + dy * dy);

        if (majorDiameter < 1.0f) return; // Degenerate line segment

        float a0 = majorDiameter / 2.0f;
        float theta = MathF.Atan2(dy, dx);

        // Calculate minor radius b0 as perpendicular distance from S2 to line S0-S1
        float b0;
        if (Points.Count >= 3 && (Math.Abs(s2.X - s1.X) > 0.001 || Math.Abs(s2.Y - s1.Y) > 0.001))
        {
            float numerator = MathF.Abs(dx * (float)(s0.Y - s2.Y) - (float)(s0.X - s2.X) * dy);
            b0 = numerator / majorDiameter;
            if (b0 < 0.5f) b0 = 0.5f;
        }
        else
        {
            // Default preview proportion (0.382) before point 3 is placed
            b0 = MathF.Max(1.0f, a0 * (float)_fibLevels[1]);
        }

        canvas.Save();
        canvas.Translate(cX, cY);
        canvas.RotateRadians(theta);

        // 1. Optional Semi-Transparent Base Ellipse Fill
        if (IsFilled)
        {
            _fillPaint.Color = new SKColor(Color.R, Color.G, Color.B, FillAlpha);
            canvas.DrawOval(new SKRect(-a0, -b0, a0, b0), _fillPaint);
        }

        // 2. Draw Concentric Fibonacci Ellipses
        _cachedPaint.Color = SkiaColor;
        _cachedPaint.StrokeWidth = (float)Thickness;

        _textPaint.Color = SkiaColor;
        _textPaint.TextSize = (float)DrawingThemeContext.DrawingFontSize;

        var ovalRect = new SKRect();
        for (int i = 0; i < _fibLevels.Length; i++)
        {
            float level = (float)_fibLevels[i];
            float ar = a0 * level;
            float br = b0 * level;

            if (ar < 0.5f || br < 0.5f) continue;

            ovalRect.Left = -ar;
            ovalRect.Top = -br;
            ovalRect.Right = ar;
            ovalRect.Bottom = br;

            canvas.DrawOval(ovalRect, _cachedPaint);

            if (ShowLabels)
            {
                string label = Math.Abs(level - 1.0f) < 0.001f ? "1.0 (100%)" : $"{level:0.###}";
                canvas.DrawText(label, 4f, -br - 2f, _textPaint);
            }
        }

        // 3. Draw Central Median Line (Trend Axis)
        if (ShowMedianLine)
        {
            _dashPaint.Color = SkiaColor.WithAlpha(160);
            _dashPaint.StrokeWidth = Math.Max(1f, (float)Thickness * 0.75f);
            float maxAr = a0 * (float)_fibLevels[^1];
            canvas.DrawLine(-maxAr, 0, maxAr, 0, _dashPaint);
        }

        canvas.Restore();
    }

    public override bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (transform == null || Points.Count < 2) return false;

        var s0 = transform.ChartToScreen(Points[0]);
        var s1 = transform.ChartToScreen(Points[1]);
        var s2 = Points.Count >= 3 ? transform.ChartToScreen(Points[2]) : s1;

        // 1. Check handles hit test (P0, P1, P2)
        if (GetDistance(screenPoint, s0) <= tolerance * 2) return true;
        if (GetDistance(screenPoint, s1) <= tolerance * 2) return true;
        if (Points.Count >= 3 && GetDistance(screenPoint, s2) <= tolerance * 2) return true;

        double cX = (s0.X + s1.X) / 2.0;
        double cY = (s0.Y + s1.Y) / 2.0;

        double dx = s1.X - s0.X;
        double dy = s1.Y - s0.Y;
        double majorDiameter = Math.Sqrt(dx * dx + dy * dy);
        if (majorDiameter < 1.0) return false;

        double a0 = majorDiameter / 2.0;
        double theta = Math.Atan2(dy, dx);

        double b0;
        if (Points.Count >= 3 && (Math.Abs(s2.X - s1.X) > 0.001 || Math.Abs(s2.Y - s1.Y) > 0.001))
        {
            double numerator = Math.Abs(dx * (s0.Y - s2.Y) - (s0.X - s2.X) * dy);
            b0 = numerator / majorDiameter;
            if (b0 < 0.5) b0 = 0.5;
        }
        else
        {
            b0 = Math.Max(1.0, a0 * _fibLevels[1]);
        }

        // Transform screen point to ellipse-local coordinate space
        double relX = screenPoint.X - cX;
        double relY = screenPoint.Y - cY;

        double cosT = Math.Cos(theta);
        double sinT = Math.Sin(theta);

        double locX = relX * cosT + relY * sinT;
        double locY = -relX * sinT + relY * cosT;

        // 2. Check Fill Hit Test
        if (IsFilled)
        {
            double normDistSq = (locX * locX) / (a0 * a0) + (locY * locY) / (b0 * b0);
            if (normDistSq <= 1.0) return true;
        }

        // 3. Check Concentric Ellipse Perimeter Hit Test
        double effectiveTol = tolerance + (Thickness / 2.0);

        for (int i = 0; i < _fibLevels.Length; i++)
        {
            double ar = a0 * _fibLevels[i];
            double br = b0 * _fibLevels[i];
            if (ar < 0.5 || br < 0.5) continue;

            double normX = locX / ar;
            double normY = locY / br;
            double normDist = Math.Sqrt(normX * normX + normY * normY);

            double minR = Math.Min(ar, br);
            double pixelDiff = Math.Abs(normDist - 1.0) * minR;

            if (pixelDiff <= effectiveTol) return true;
        }

        // 4. Check Median Line Hit Test
        if (ShowMedianLine)
        {
            double maxAr = a0 * _fibLevels[^1];
            if (Math.Abs(locY) <= effectiveTol && Math.Abs(locX) <= maxAr) return true;
        }

        return false;
    }

    private static double GetDistance(global::Avalonia.Point a, global::Avalonia.Point b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public override void Dispose()
    {
        _dashPaint?.Dispose();
        _fillPaint?.Dispose();
        _textPaint?.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
