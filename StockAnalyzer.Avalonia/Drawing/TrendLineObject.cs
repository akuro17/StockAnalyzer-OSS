using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

public class TrendLineObject : RelativeGeometricRenderer, IDrawingCalculatedValuesProvider
{
    public override ChartObjectType Type => ChartObjectType.TrendLine;

    // FR-39-9-04, FR-39-9-05: User-configurable projection settings
    [Category("Projection")]
    [DisplayName("Show Projection")]
    [ParameterTag(DrawingParameterTags.Projection)]
    public bool ShowProjection { get; set; } = false; // Default OFF as requested

    [Category("Projection")]
    [DisplayName("Projection Columns")]
    [Range(1, 100)]
    [ParameterTag(DrawingParameterTags.Projection)]
    public int ProjectionColumns { get; set; } = 10;

    /// <summary>
    /// Initial <see cref="ExtendMode"/> of the "Extended Line" toolbar tool. Applied through the per-tool preset prototype
    /// (<c>DrawingToolPresetService</c>), so a user-edited preset wins; the plain Trend Line tool stays <see cref="LineExtendMode.None"/>.
    /// </summary>
    public const LineExtendMode ExtendedLineToolDefaultMode = LineExtendMode.Both;

    /// <summary>Which ends of the line run on to the border of the drawing area (click order: Right = beyond P2, Left = beyond P1).</summary>
    [Category("Parameters")]
    [DisplayName("Extend")]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public LineExtendMode ExtendMode { get; set; } = LineExtendMode.None;

    // FR-39-9-03: ZeroAllocation cached paints for projection and labeling
    private readonly SKPaint _projectionPaint;

    public TrendLineObject(ChartPoint p1, ChartPoint p2) : base()
    {
        Points.Add(p1);
        Points.Add(p2);

        // Pre-allocate projection paint (Dash effect)
        _projectionPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash(new float[] { ChartConstants.DefaultTrendProjectionDashInterval, ChartConstants.DefaultTrendProjectionDashInterval }, 0)
        };
    }

    // SkiaColor is provided by base RelativeGeometricRenderer

    protected override void DrawGeometry(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Points.Count < 2) return;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        // 1. Draw Base TrendLine using base class _cachedPaint (an extended end runs on to the border of the drawing area)
        var (start, end) = GetExtendedSegment(p1, p2, transform.ScreenRect, ExtendMode);
        canvas.DrawLine((float)start.X, (float)start.Y, (float)end.X, (float)end.Y, _cachedPaint);

        // 2. Predictive Forward Projection (FR-39-9-01)
        if (!ShowProjection) return;

        // Determine which point is chronologically later to project forward
        bool p2IsLater = Points[1].Time >= Points[0].Time;
        var startPoint = p2IsLater ? p2 : p1;
        var prevPoint = p2IsLater ? p1 : p2;

        double dx = startPoint.X - prevPoint.X;
        double dy = startPoint.Y - prevPoint.Y;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        if (dist > 0)
        {
            // Calculate logic-based "column width" for projection length
            double columnWidth = (transform.TimeMap != null) 
                ? Math.Abs(transform.NumericToScreen(1, 0).X - transform.NumericToScreen(0, 0).X)
                : transform.ViewportWidth / 80.0;
            
            double projectionLen = columnWidth * ProjectionColumns;

            float ux = (float)(dx / dist);
            float uy = (float)(dy / dist);

            float projEndX = (float)startPoint.X + ux * (float)projectionLen;
            float projEndY = (float)startPoint.Y + uy * (float)projectionLen;

            // Draw dashed projection line
            _projectionPaint.Color = SkiaColor.WithAlpha(ChartConstants.DefaultTrendProjectionAlpha);
            _projectionPaint.StrokeWidth = (float)Thickness;
            canvas.DrawLine((float)startPoint.X, (float)startPoint.Y, projEndX, projEndY, _projectionPaint);
        }
    }

    public override bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 2) return false;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);
        
        // Distance from point to line segment (the extended part counts like the body)
        var (start, end) = GetExtendedSegment(p1, p2, transform.ScreenRect, ExtendMode);
        double dist = DistancePointToSegment(screenPoint, start, end);
        return dist <= tolerance;
    }

    /// <summary>
    /// The screen segment to draw / hit-test for the screen anchors <paramref name="s1"/> (P1) and <paramref name="s2"/> (P2):
    /// the segment itself for <see cref="LineExtendMode.None"/>, otherwise the segment prolonged on the chosen end(s) to where
    /// the infinite line leaves <paramref name="clip"/>. An end whose anchor is already outside the clip is not moved, and a
    /// line that misses the clip or has anchors closer than <see cref="ChartConstants.ExtendedLineMinSpanPixels"/> is not extended.
    /// </summary>
    internal static (global::Avalonia.Point Start, global::Avalonia.Point End) GetExtendedSegment(
        global::Avalonia.Point s1, global::Avalonia.Point s2, global::Avalonia.Rect clip, LineExtendMode mode)
    {
        if (mode == LineExtendMode.None || clip.Width <= 0 || clip.Height <= 0) return (s1, s2);

        double dx = s2.X - s1.X;
        double dy = s2.Y - s1.Y;
        if (Math.Sqrt(dx * dx + dy * dy) < ChartConstants.ExtendedLineMinSpanPixels) return (s1, s2);

        // Liang-Barsky on the infinite line P(t) = s1 + t * (s2 - s1): t = 0 is P1, t = 1 is P2.
        double tEnter = double.NegativeInfinity;
        double tExit = double.PositiveInfinity;
        if (!ClipParameter(-dx, s1.X - clip.Left, ref tEnter, ref tExit) ||
            !ClipParameter(dx, clip.Right - s1.X, ref tEnter, ref tExit) ||
            !ClipParameter(-dy, s1.Y - clip.Top, ref tEnter, ref tExit) ||
            !ClipParameter(dy, clip.Bottom - s1.Y, ref tEnter, ref tExit))
        {
            return (s1, s2);
        }

        var start = s1;
        var end = s2;
        if ((mode == LineExtendMode.Left || mode == LineExtendMode.Both) && tEnter < 0.0)
        {
            start = new global::Avalonia.Point(s1.X + tEnter * dx, s1.Y + tEnter * dy);
        }
        if ((mode == LineExtendMode.Right || mode == LineExtendMode.Both) && tExit > 1.0)
        {
            end = new global::Avalonia.Point(s1.X + tExit * dx, s1.Y + tExit * dy);
        }
        return (start, end);
    }

    /// <summary>One Liang-Barsky edge test; false when the infinite line lies fully outside this edge.</summary>
    private static bool ClipParameter(double p, double q, ref double tEnter, ref double tExit)
    {
        if (p == 0.0) return q >= 0.0;

        double r = q / p;
        if (p < 0.0)
        {
            if (r > tEnter) tEnter = r;
        }
        else if (r < tExit)
        {
            tExit = r;
        }
        return tEnter <= tExit;
    }

    public override void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price + priceDelta);
        }
    }

    // A removed object is disposed at once while a frame that still holds it may render it on another thread (see NativeResourceGate).
    private readonly NativeResourceGate _gate = new NativeResourceGate();

    public override void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        lock (_gate.SyncRoot)
        {
            if (_gate.IsDisposed) return;
            base.Render(canvas, transform);
        }
    }

    public override void Dispose()
    {
        lock (_gate.SyncRoot)
        {
            if (!_gate.TryMarkDisposed()) return;
            _projectionPaint.Dispose();
            base.Dispose();
        }
    }

    private double DistancePointToSegment(global::Avalonia.Point p, global::Avalonia.Point v, global::Avalonia.Point w)
    {
        double l2 = Math.Pow(v.X - w.X, 2) + Math.Pow(v.Y - w.Y, 2);
        if (l2 == 0) return Math.Sqrt(Math.Pow(p.X - v.X, 2) + Math.Pow(p.Y - v.Y, 2));
        double t = ((p.X - v.X) * (w.X - v.X) + (p.Y - v.Y) * (w.Y - v.Y)) / l2;
        t = Math.Max(0, Math.Min(1, t));
        global::Avalonia.Point projection = new global::Avalonia.Point(v.X + t * (w.X - v.X), v.Y + t * (w.Y - v.Y));
        return Math.Sqrt(Math.Pow(p.X - projection.X, 2) + Math.Pow(p.Y - projection.Y, 2));
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        if (Points.Count < 2) return Array.Empty<DrawingCalculatedValue>();

        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);

        return TrendLineValues.Calculate(Points[0].ToAnchor(), Points[1].ToAnchor(), timestamp, color);
    }
}
