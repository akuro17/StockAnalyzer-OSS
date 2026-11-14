using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

public class ParallelChannelObject : IChartObject, IDisposable, IDrawingCalculatedValuesProvider
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.ParallelChannel;
    
    // P1, P2 Define the main trend line. P3 defines the parallel line position.
    public List<ChartPoint> Points { get; private set; }

    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;

    [Category("Style")]
    [DisplayName("Stroke Style")]
    [ParameterTag(DrawingParameterTags.Common)]
    public DrawingStrokeStyle StrokeStyle { get; set; } = DrawingStrokeStyle.Solid;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int ZIndex { get; set; } = 0;
    public int Priority { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;
    public int PanelIndex { get; set; } = -1;
    public bool IsFilled { get; set; } = true;

    /// <summary>Parallel (default): the opposite edge runs parallel to P1-P2 through P3. FlatOpposite: it is horizontal at P3's price.</summary>
    [Category("Parameters")]
    [DisplayName("Channel Mode")]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public ChannelMode ChannelMode { get; set; } = ChannelMode.Parallel;

    // Skia resources are created once and only their per-frame values (colour, width, stroke style) are assigned in Render, so a change
    // of colour / thickness / stroke style in the settings dialog is picked up on the next frame without a new native object.
    private readonly SKPaint _fillPaint = new SKPaint { Style = SKPaintStyle.Fill };
    private readonly SKPaint _strokePaint = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _centerPaint = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = ChartConstants.ChannelCenterLineStrokeWidth };
    private readonly SKPathEffect _centerDash = SKPathEffect.CreateDash(new float[] { ChartConstants.ChannelCenterLineDashInterval, ChartConstants.ChannelCenterLineDashInterval }, 0);
    private readonly SKPath _path = new SKPath();

    // A removed object is disposed at once while a frame that still holds it may render it on another thread (see NativeResourceGate).
    private readonly NativeResourceGate _gate = new NativeResourceGate();

    public ParallelChannelObject(ChartPoint p1, ChartPoint p2, ChartPoint p3)
    {
        Points = new List<ChartPoint> { p1, p2, p3 };
        _centerPaint.PathEffect = _centerDash;
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Dispose()
    {
        lock (_gate.SyncRoot)
        {
            if (!_gate.TryMarkDisposed()) return;
            _fillPaint.Dispose();
            _strokePaint.Dispose();
            _centerPaint.Dispose();
            _centerDash.Dispose();
            _path.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        lock (_gate.SyncRoot)
        {
            if (_gate.IsDisposed || Points.Count < 3) return;
            RenderLocked(canvas, transform);
        }
    }

    /// <summary>The drawing itself; runs inside the <see cref="NativeResourceGate"/> lock so the native objects cannot be freed meanwhile.</summary>
    private void RenderLocked(SKCanvas canvas, ICoordinateTransform transform)
    {
        var s1 = transform.ChartToScreen(Points[0]);
        var s2 = transform.ChartToScreen(Points[1]);
        var s3 = transform.ChartToScreen(Points[2]);

        // Calculate generic polygon
        // Line 1: P1 -> P2
        // Line 2: Parallel to Line 1, passing through P3
        // Vertices: s1, s2, s2', s1'
        // Where s1', s2' are on Line 2 with same X as s1, s2 (Visual Vertical alignment)

        GetOppositeEdge(transform, s1, s2, s3, out var s1_prime, out var s2_prime);

        // Draw Filled Path
        _path.Reset();
        _path.MoveTo((float)s1.X, (float)s1.Y);
        _path.LineTo((float)s2.X, (float)s2.Y);
        _path.LineTo((float)s2_prime.X, (float)s2_prime.Y);
        _path.LineTo((float)s1_prime.X, (float)s1_prime.Y);
        _path.Close();

        var color = SkiaColor;
        if (IsFilled)
        {
            _fillPaint.Color = new SKColor(color.Red, color.Green, color.Blue, ChartConstants.ChannelFillAlpha);
            canvas.DrawPath(_path, _fillPaint);
        }

        _strokePaint.Color = color;
        _strokePaint.StrokeWidth = (float)Thickness;
        _strokePaint.PathEffect = StrokeStyle.ToSkPathEffect();
        canvas.DrawPath(_path, _strokePaint);

        // Center dashed line (optional, user req "Center line"?)
        // Prompt 35-2 says "Band and Center line".
        // Parallel Channel usually has a center line.
        // Midpoints: (s1+s1')/2 ...
        global::Avalonia.Point m1, m2;
        if (ChannelMode == ChannelMode.FlatOpposite)
        {
            // Data-coordinate midpoints (t1, (p1 + p3) / 2) and (t2, (p2 + p3) / 2), projected through the transform.
            decimal p3Price = Points[2].Price;
            m1 = transform.ChartToScreen(new ChartPoint(Points[0].Time, (Points[0].Price + p3Price) / 2m));
            m2 = transform.ChartToScreen(new ChartPoint(Points[1].Time, (Points[1].Price + p3Price) / 2m));
        }
        else
        {
            m1 = new global::Avalonia.Point((s1.X + s1_prime.X) / 2, (s1.Y + s1_prime.Y) / 2);
            m2 = new global::Avalonia.Point((s2.X + s2_prime.X) / 2, (s2.Y + s2_prime.Y) / 2);
        }

        _centerPaint.Color = color;
        canvas.DrawLine((float)m1.X, (float)m1.Y, (float)m2.X, (float)m2.Y, _centerPaint);

        if (IsSelected)
        {
            SelectionHandleRenderer.Draw(canvas, s1, AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null, radius: ChartConstants.SelectedHandleRadius);
            SelectionHandleRenderer.Draw(canvas, s2, AnchorPointIndex == 1 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null, radius: ChartConstants.SelectedHandleRadius);
            SelectionHandleRenderer.Draw(canvas, s3, AnchorPointIndex == 2 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null, radius: ChartConstants.SelectedHandleRadius);
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        // Simple bounding box or Polygon containment
        // Polygon test
        if (Points.Count < 3) return false;
        
        // Re-calculate polygon points (logic duplication, could refactor)
        var s1 = transform.ChartToScreen(Points[0]);
        var s2 = transform.ChartToScreen(Points[1]);
        var s3 = transform.ChartToScreen(Points[2]);

        GetOppositeEdge(transform, s1, s2, s3, out var s1_prime, out var s2_prime);

        // Check if inside Polygon (s1, s2, s2_prime, s1_prime)
        // Ray casting algorithm or SKPath.Contains
        using var path = new SKPath();
        path.MoveTo((float)s1.X, (float)s1.Y);
        path.LineTo((float)s2.X, (float)s2.Y);
        path.LineTo((float)s2_prime.X, (float)s2_prime.Y);
        path.LineTo((float)s1_prime.X, (float)s1_prime.Y);
        path.Close();

        return path.Contains((float)screenPoint.X, (float)screenPoint.Y) || 
               // Or near lines
               IsNearLine(s1, s2, screenPoint, tolerance) ||
               IsNearLine(s2, s2_prime, screenPoint, tolerance) ||
               IsNearLine(s2_prime, s1_prime, screenPoint, tolerance) ||
               IsNearLine(s1_prime, s1, screenPoint, tolerance);
    }

    /// <summary>
    /// Screen end points s1' / s2' of the opposite edge, at the screen X of s1 / s2 (shared by <see cref="Render"/> and <see cref="HitTest"/>).
    /// <see cref="ChannelMode.Parallel"/>: on the line through s3 with the slope of s1-s2 (screen-space, unchanged original behaviour).
    /// <see cref="ChannelMode.FlatOpposite"/>: the data points (t1, p3) and (t2, p3) projected through the transform.
    /// </summary>
    private void GetOppositeEdge(ICoordinateTransform transform, global::Avalonia.Point s1, global::Avalonia.Point s2, global::Avalonia.Point s3,
        out global::Avalonia.Point s1Prime, out global::Avalonia.Point s2Prime)
    {
        if (ChannelMode == ChannelMode.FlatOpposite)
        {
            decimal p3Price = Points[2].Price;
            s1Prime = transform.ChartToScreen(new ChartPoint(Points[0].Time, p3Price));
            s2Prime = transform.ChartToScreen(new ChartPoint(Points[1].Time, p3Price));
            return;
        }

        // Slope m = dy / dx
        float dx = (float)(s2.X - s1.X);
        float dy = (float)(s2.Y - s1.Y);

        // Avoid divide by zero (vertical line)
        if (Math.Abs(dx) < 0.001)
        {
            // If vertical, parallel line is just shifted by (s3.X - s1.X)
            float xShift = (float)(s3.X - s1.X);
            s2Prime = new global::Avalonia.Point(s2.X + xShift, s2.Y);
            s1Prime = new global::Avalonia.Point(s1.X + xShift, s1.Y);
        }
        else
        {
            float m = dy / dx;
            // Line 2 equation: y - y3 = m(x - x3) => y = m(x - x3) + y3
            float y2Prime = m * (float)(s2.X - s3.X) + (float)s3.Y;
            s2Prime = new global::Avalonia.Point(s2.X, y2Prime);

            float y1Prime = m * (float)(s1.X - s3.X) + (float)s3.Y;
            s1Prime = new global::Avalonia.Point(s1.X, y1Prime);
        }
    }

    private bool IsNearLine(global::Avalonia.Point p1, global::Avalonia.Point p2, global::Avalonia.Point test, double tolerance)
    {
        // Distance from point to line segment
        double dx = p2.X - p1.X;
        double dy = p2.Y - p1.Y;
        if (dx == 0 && dy == 0) return false;

        double t = ((test.X - p1.X) * dx + (test.Y - p1.Y) * dy) / (dx * dx + dy * dy);
        
        if (t < 0) return PointDistance(p1, test) < tolerance;
        if (t > 1) return PointDistance(p2, test) < tolerance;
        global::Avalonia.Point projection = new global::Avalonia.Point(p1.X + t * dx, p1.Y + t * dy);
        return PointDistance(projection, test) < tolerance;
    }

    private double PointDistance(global::Avalonia.Point a, global::Avalonia.Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price + priceDelta);
        }
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        if (Points.Count < 3) return Array.Empty<DrawingCalculatedValue>();

        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);

        return ParallelChannelValues.Calculate(Points[0].ToAnchor(), Points[1].ToAnchor(), Points[2].ToAnchor(), timestamp, color, ChannelMode);
    }
}

