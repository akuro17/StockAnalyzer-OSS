using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Disjoint channel: two independent edges. Edge 1 is P1-P2, edge 2 is P3-P4, the center line joins the midpoint of P1/P3
/// to the midpoint of P2/P4 (data coordinates), and the fill is the polygon P1, P2, P4, P3. Edges are not extended.
/// </summary>
public class DisjointChannelObject : IChartObject, IDisposable, IDrawingCalculatedValuesProvider
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.DisjointChannel;

    /// <summary>P1, P2 = edge 1; P3, P4 = edge 2 (click order).</summary>
    public List<ChartPoint> Points { get; } = new List<ChartPoint>();

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

    private readonly SKPaint _fillPaint = new SKPaint { Style = SKPaintStyle.Fill };
    private readonly SKPaint _strokePaint = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _centerPaint = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = ChartConstants.ChannelCenterLineStrokeWidth };
    private readonly SKPathEffect _centerDash = SKPathEffect.CreateDash(new float[] { ChartConstants.ChannelCenterLineDashInterval, ChartConstants.ChannelCenterLineDashInterval }, 0);
    private readonly SKPath _fillPath = new SKPath();

    /// <summary>Number of control points: P1, P2 (edge 1) and P3, P4 (edge 2). Also the number of clicks of the tool.</summary>
    public const int PointCount = 4;

    public DisjointChannelObject(ChartPoint p1, ChartPoint p2, ChartPoint p3, ChartPoint p4)
    {
        Points.Add(p1);
        Points.Add(p2);
        Points.Add(p3);
        Points.Add(p4);
        _centerPaint.PathEffect = _centerDash;
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    // A removed object is disposed at once while a frame that still holds it may render it on another thread (see NativeResourceGate).
    private readonly NativeResourceGate _gate = new NativeResourceGate();

    public void Dispose()
    {
        lock (_gate.SyncRoot)
        {
            if (!_gate.TryMarkDisposed()) return;
            _fillPaint.Dispose();
            _strokePaint.Dispose();
            _centerPaint.Dispose();
            _centerDash.Dispose();
            _fillPath.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Center line end points in data coordinates: the midpoint of P1/P3 and of P2/P4. The time is the integer mean of the two
    /// tick counts, the price the mean of the two prices.
    /// </summary>
    internal static ChartPoint Midpoint(ChartPoint a, ChartPoint b)
        => new ChartPoint(new DateTime((a.Time.Ticks + b.Time.Ticks) / 2, a.Time.Kind), (a.Price + b.Price) / 2m);

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        lock (_gate.SyncRoot)
        {
            if (_gate.IsDisposed || !IsVisible || Points.Count < PointCount) return;
            RenderLocked(canvas, transform);
        }
    }

    /// <summary>The drawing itself; runs inside the <see cref="NativeResourceGate"/> lock so the native paints cannot be freed meanwhile.</summary>
    private void RenderLocked(SKCanvas canvas, ICoordinateTransform transform)
    {
        var s1 = transform.ChartToScreen(Points[0]);
        var s2 = transform.ChartToScreen(Points[1]);
        var s3 = transform.ChartToScreen(Points[2]);
        var s4 = transform.ChartToScreen(Points[3]);
        var c1 = transform.ChartToScreen(Midpoint(Points[0], Points[2]));
        var c2 = transform.ChartToScreen(Midpoint(Points[1], Points[3]));

        var color = SkiaColor;

        if (IsFilled)
        {
            _fillPath.Reset();
            _fillPath.MoveTo((float)s1.X, (float)s1.Y);
            _fillPath.LineTo((float)s2.X, (float)s2.Y);
            _fillPath.LineTo((float)s4.X, (float)s4.Y);
            _fillPath.LineTo((float)s3.X, (float)s3.Y);
            _fillPath.Close();

            _fillPaint.Color = new SKColor(color.Red, color.Green, color.Blue, ChartConstants.ChannelFillAlpha);
            canvas.DrawPath(_fillPath, _fillPaint);
        }

        _strokePaint.Color = color;
        _strokePaint.StrokeWidth = (float)Thickness;
        _strokePaint.PathEffect = StrokeStyle.ToSkPathEffect();
        canvas.DrawLine((float)s1.X, (float)s1.Y, (float)s2.X, (float)s2.Y, _strokePaint);
        canvas.DrawLine((float)s3.X, (float)s3.Y, (float)s4.X, (float)s4.Y, _strokePaint);

        _centerPaint.Color = color;
        canvas.DrawLine((float)c1.X, (float)c1.Y, (float)c2.X, (float)c2.Y, _centerPaint);

        if (IsSelected)
        {
            for (int i = 0; i < PointCount; i++)
            {
                var handle = i == 0 ? s1 : i == 1 ? s2 : i == 2 ? s3 : s4;
                SelectionHandleRenderer.Draw(canvas, handle, AnchorPointIndex == i ? DrawingThemeContext.AnchorPointColor : (SKColor?)null, radius: ChartConstants.SelectedHandleRadius);
            }
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < PointCount) return false;

        var s1 = transform.ChartToScreen(Points[0]);
        var s2 = transform.ChartToScreen(Points[1]);
        var s3 = transform.ChartToScreen(Points[2]);
        var s4 = transform.ChartToScreen(Points[3]);
        var c1 = transform.ChartToScreen(Midpoint(Points[0], Points[2]));
        var c2 = transform.ChartToScreen(Midpoint(Points[1], Points[3]));

        // Distance to a segment: the shared BezierSplineMath definition (one definition for all drawing objects).
        var target = ToSkPoint(screenPoint);
        if (BezierSplineMath.DistancePointToSegment(target, ToSkPoint(s1), ToSkPoint(s2)) <= tolerance) return true;
        if (BezierSplineMath.DistancePointToSegment(target, ToSkPoint(s3), ToSkPoint(s4)) <= tolerance) return true;
        if (BezierSplineMath.DistancePointToSegment(target, ToSkPoint(c1), ToSkPoint(c2)) <= tolerance) return true;

        return IsFilled && PolygonContains(screenPoint, s1, s2, s4, s3);
    }

    private static SKPoint ToSkPoint(global::Avalonia.Point p) => new SKPoint((float)p.X, (float)p.Y);

    /// <summary>Even-odd point-in-quadrilateral test (no allocation; also correct for a crossed quadrilateral).</summary>
    internal static bool PolygonContains(global::Avalonia.Point p, global::Avalonia.Point a, global::Avalonia.Point b, global::Avalonia.Point c, global::Avalonia.Point d)
    {
        bool inside = false;
        if (EdgeCrossesRay(p, d, a)) inside = !inside;
        if (EdgeCrossesRay(p, a, b)) inside = !inside;
        if (EdgeCrossesRay(p, b, c)) inside = !inside;
        if (EdgeCrossesRay(p, c, d)) inside = !inside;
        return inside;
    }

    private static bool EdgeCrossesRay(global::Avalonia.Point p, global::Avalonia.Point from, global::Avalonia.Point to)
    {
        if ((from.Y > p.Y) == (to.Y > p.Y)) return false;
        double xAtY = from.X + (p.Y - from.Y) / (to.Y - from.Y) * (to.X - from.X);
        return p.X < xAtY;
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price + priceDelta);
        }
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        if (Points.Count < PointCount) return Array.Empty<DrawingCalculatedValue>();

        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);

        return DisjointChannelValues.Calculate(Points[0].ToAnchor(), Points[1].ToAnchor(), Points[2].ToAnchor(), Points[3].ToAnchor(), timestamp, color);
    }
}
