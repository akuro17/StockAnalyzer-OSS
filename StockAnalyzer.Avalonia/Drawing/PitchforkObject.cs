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

public enum PitchforkType
{
    Andrews,
    Schiff,
    ModifiedSchiff
}

public class PitchforkObject : IChartObject, IDisposable, IDrawingCalculatedValuesProvider
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.Pitchfork;

    // P1: Handle (Anchor)
    // P2: High
    // P3: Low
    public List<ChartPoint> Points { get; private set; }

    [Category("Parameters")]
    [DisplayName("Pitchfork Type")]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public PitchforkType PitchforkType { get; set; } = PitchforkType.Andrews;

    [Browsable(false)]
    public bool IsInside { get; set; } = false; // Internal Median Line (legacy unused)

    [Category("Parameters")]
    [DisplayName("Show Half Lines")]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public bool ShowHalfLines { get; set; } = false;

    [Category("Parameters")]
    [DisplayName("Show Warning Lines")]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public bool ShowWarningLines { get; set; } = false;

    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int PanelIndex { get; set; } = -1;
    public int ZIndex { get; set; } = 0;
    public int Priority { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;

    // Ratios for parallel lines (Standard Pitchfork often uses 1.0, maybe 0.5, 1.5, etc.)
    // For now, we draw the standard channel: Median Line (0), and Outer Lines passing through P2/P3.
    // We can add intermediate lines later.

    private static readonly decimal[] HalfLineMultipliers = { 0.5m, -0.5m };
    private static readonly decimal[] WarningLineMultipliers = { 1.5m, -1.5m, 2.0m, -2.0m };

    private readonly SKPaint _paint = new SKPaint
    {
        Style = SKPaintStyle.Stroke,
        IsAntialias = true
    };

    public PitchforkObject(ChartPoint p1, ChartPoint p2, ChartPoint p3)
    {
        Points = new List<ChartPoint> { p1, p2, p3 };
    }

    public void Dispose()
    {
        _paint.Dispose();
        GC.SuppressFinalize(this);
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    /// <summary>Screen positions of the three control points, the median-line anchor and the P2-P3 midpoint.</summary>
    internal readonly record struct PitchforkScreenShape(
        global::Avalonia.Point P1,
        global::Avalonia.Point P2,
        global::Avalonia.Point P3,
        global::Avalonia.Point Anchor,
        global::Avalonia.Point Median);

    // PitchforkKind (Core) has the same members with the same numeric values as PitchforkType; a test pins that.
    internal static PitchforkKind ToKind(PitchforkType type) => (PitchforkKind)(int)type;

    /// <summary>
    /// The single screen-geometry definition used by Render and HitTest: anchor and midpoint are computed in chart coordinates
    /// (<see cref="PitchforkValues"/>) and then projected with the chart transform.
    /// </summary>
    internal static PitchforkScreenShape GetScreenShape(PitchforkType type, ChartPoint p1, ChartPoint p2, ChartPoint p3, ICoordinateTransform transform)
    {
        ResolveDataAnchors(type, p1, p2, p3, out _, out DrawingAnchor anchor, out DrawingAnchor median);

        return new PitchforkScreenShape(
            transform.ChartToScreen(p1),
            transform.ChartToScreen(p2),
            transform.ChartToScreen(p3),
            transform.ChartToScreen(new ChartPoint(anchor.Time, anchor.Price)),
            transform.ChartToScreen(new ChartPoint(median.Time, median.Price)));
    }

    internal static (global::Avalonia.Point Origin, global::Avalonia.Point DirectionTarget) GetParallelLineScreenPoints(
        PitchforkType type,
        ChartPoint p1,
        ChartPoint p2,
        ChartPoint p3,
        decimal factor,
        ICoordinateTransform transform)
    {
        ResolveDataAnchors(type, p1, p2, p3, out DrawingAnchor a2, out DrawingAnchor anchor, out DrawingAnchor median);
        return ProjectParallelLine(a2, anchor, median, factor, transform);
    }

    private static void ResolveDataAnchors(
        PitchforkType type,
        ChartPoint p1,
        ChartPoint p2,
        ChartPoint p3,
        out DrawingAnchor a2,
        out DrawingAnchor anchor,
        out DrawingAnchor median)
    {
        DrawingAnchor a1 = p1.ToAnchor();
        a2 = p2.ToAnchor();
        DrawingAnchor a3 = p3.ToAnchor();
        anchor = PitchforkValues.GetAnchor(ToKind(type), a1, a2);
        median = PitchforkValues.GetMedianPoint(a2, a3);
    }

    private static (global::Avalonia.Point Origin, global::Avalonia.Point DirectionTarget) ProjectParallelLine(
        DrawingAnchor a2,
        DrawingAnchor anchor,
        DrawingAnchor median,
        decimal factor,
        ICoordinateTransform transform)
    {
        DrawingAnchor q = PitchforkValues.GetParallelAnchor(a2, median, factor);
        DrawingAnchor qNext = PitchforkValues.OffsetByMedianVector(q, anchor, median);
        return (
            transform.ChartToScreen(new ChartPoint(q.Time, q.Price)),
            transform.ChartToScreen(new ChartPoint(qNext.Time, qNext.Price)));
    }

    private static bool IsValidScreenPoint(global::Avalonia.Point point)
        => double.IsFinite(point.X) && double.IsFinite(point.Y);

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Points.Count < 3) return;

        // The anchor and the P2-P3 midpoint are decided in chart coordinates (PitchforkValues) and only then projected to the screen,
        // so the drawn lines agree with the Data-tab values on a logarithmic axis and across non-trading days.
        var shape = GetScreenShape(PitchforkType, Points[0], Points[1], Points[2], transform);
        global::Avalonia.Point s1 = shape.P1;
        global::Avalonia.Point s2 = shape.P2;
        global::Avalonia.Point s3 = shape.P3;
        global::Avalonia.Point anchor = shape.Anchor;
        global::Avalonia.Point visibleMidpoint = shape.Median;

        // 3. Draw Median Line (Anchor -> through VisibleMidpoint -> Extend)
        // Vector V = VisibleMidpoint - Anchor
        global::Avalonia.Vector v = visibleMidpoint - anchor;

        _paint.Color = SkiaColor;
        _paint.StrokeWidth = (float)Thickness;
        _paint.PathEffect = null;

        // Clip/Bounds checks would be good, but Skia handles drawing outside canvas fine.
        DrawRay(canvas, _paint, anchor, visibleMidpoint, isRay: true);

        // 4. Draw Parallel Lines (through P2 and P3)
        // They are parallel to the Median Line.
        // P2 Line: Passes through P2, Slope same as Median Line.
        // P3 Line: Passes through P3, Slope same as Median Line.
        // Actually, in Andrews Pitchfork, P2 and P3 ARE the points the outer lines pass through.
        
        DrawRay(canvas, _paint, s2, s2 + v, isRay: true);
        DrawRay(canvas, _paint, s3, s3 + v, isRay: true);

        // 5. Additional parallel lines (half lines ±0.5 and warning lines ±1.5, ±2.0) in dashed style.
        if (ShowHalfLines || ShowWarningLines)
        {
            _paint.PathEffect = DrawingStrokeStyle.Dash.ToSkPathEffect();
            ResolveDataAnchors(PitchforkType, Points[0], Points[1], Points[2], out DrawingAnchor a2, out DrawingAnchor dataAnchor, out DrawingAnchor dataMedian);

            if (ShowHalfLines)
            {
                DrawParallelGroup(canvas, _paint, a2, dataAnchor, dataMedian, HalfLineMultipliers, transform);
            }

            if (ShowWarningLines)
            {
                DrawParallelGroup(canvas, _paint, a2, dataAnchor, dataMedian, WarningLineMultipliers, transform);
            }
        }

        if (IsSelected)
        {
            SelectionHandleRenderer.Draw(canvas, s1, AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null, radius: ChartConstants.SelectedHandleRadius);
            SelectionHandleRenderer.Draw(canvas, s2, AnchorPointIndex == 1 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null, radius: ChartConstants.SelectedHandleRadius);
            SelectionHandleRenderer.Draw(canvas, s3, AnchorPointIndex == 2 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null, radius: ChartConstants.SelectedHandleRadius);

            // Draw Anchor highlight for Schiff
            if (PitchforkType != PitchforkType.Andrews)
            {
                 SelectionHandleRenderer.DrawOutline(canvas, anchor, radius: ChartConstants.PitchforkAnchorHighlightRadius, strokeWidth: ChartConstants.PitchforkAnchorHighlightStrokeWidth);
            }
        }
    }

    private void DrawRay(SKCanvas canvas, SKPaint paint, global::Avalonia.Point p1, global::Avalonia.Point p2, bool isRay)
    {
        if (isRay)
        {
            global::Avalonia.Vector diff = p2 - p1;
            double len = Math.Sqrt(diff.X * diff.X + diff.Y * diff.Y);
            
            if (len > 0.0001)
            {
                global::Avalonia.Vector dir = diff / len;
                
                var bounds = canvas.LocalClipBounds;
                var padding = ChartConstants.PitchforkBoundsPadding; 
                var clipRect = new global::Avalonia.Rect(
                    bounds.Left - padding, 
                    bounds.Top - padding, 
                    bounds.Width + padding * 2, 
                    bounds.Height + padding * 2);

                global::Avalonia.Point endPoint = GetRayIntersection(p1, dir, clipRect);
                canvas.DrawLine((float)p1.X, (float)p1.Y, (float)endPoint.X, (float)endPoint.Y, paint);
            }
            else
            {
                 canvas.DrawLine((float)p1.X, (float)p1.Y, (float)p2.X, (float)p2.Y, paint);
            }
        }
        else
        {
            canvas.DrawLine((float)p1.X, (float)p1.Y, (float)p2.X, (float)p2.Y, paint);
        }
    }

    private global::Avalonia.Point GetRayIntersection(global::Avalonia.Point origin, global::Avalonia.Vector dir, global::Avalonia.Rect bounds)
    {
        // Liang-Barsky-like or slab intersection for a Ray against AABB
        // We want the furthest point "t" that is within bounds? 
        // No, we want the FIRST exit point from inside (or origin) to outside.
        // Assuming Origin is usually inside or near.
        
        // Ray: P = O + t*D. Find smallest t > 0 that hits a boundary.

        // x = origin.X + t * dir.X
        // t = (x - origin.X) / dir.X
        


        // We want the smallest t > 0 that puts us ON the boundary.
        // But actually we just want to draw FAR ENOUGH. 
        // If we pick the largest valid t, we cross the screen.
        // The artifacts came from t being TOO LARGE (100,000).
        // So we want the t that hits the FURTHEST VISIBLE edge in the direction.
        
        double bestT = 20000; // Default large
        
        // Find smallest t > 0
        // Wait, if origin is inside, we want the exit t.
        // If we are looking for intersection with any of the 4 lines...
        
        // Let's rely on the fact that we just need a point OUTSIDE the box.
        // So finding the MAX distance to any corner?
        // No.
        
        // Simplify: Just clamp t to a reasonable value if it exceeds safe limits (e.g. 5000).
        // A 4K screen is < 5000px.
        // Does 20,000 work? My previous 100,000 broke it.
        // Let's try 10,000. It's safe for floats.
        
        bestT = 10000;
        return origin + dir * bestT;
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 3) return false;

        var shape = GetScreenShape(PitchforkType, Points[0], Points[1], Points[2], transform);
        global::Avalonia.Point s1 = shape.P1;
        global::Avalonia.Point s2 = shape.P2;
        global::Avalonia.Point s3 = shape.P3;
        global::Avalonia.Point anchor = shape.Anchor;
        global::Avalonia.Point mid = shape.Median;
        global::Avalonia.Vector v = mid - anchor;

        // Check if point is near Median Line
        if (IsNearRay(anchor, v, screenPoint, tolerance)) return true;

        // Check if point is near Outer Lines
        if (IsNearRay(s2, v, screenPoint, tolerance)) return true;
        if (IsNearRay(s3, v, screenPoint, tolerance)) return true;

        // Check visible additional lines (U-10=B': only currently enabled additional lines participate in HitTest)
        if (ShowHalfLines || ShowWarningLines)
        {
            ResolveDataAnchors(PitchforkType, Points[0], Points[1], Points[2], out DrawingAnchor a2, out DrawingAnchor dataAnchor, out DrawingAnchor dataMedian);

            if (ShowHalfLines && IsNearParallelGroup(a2, dataAnchor, dataMedian, HalfLineMultipliers, screenPoint, transform, tolerance))
            {
                return true;
            }

            if (ShowWarningLines && IsNearParallelGroup(a2, dataAnchor, dataMedian, WarningLineMultipliers, screenPoint, transform, tolerance))
            {
                return true;
            }
        }

        // Handles
        if (PointDistance(s1, screenPoint) < tolerance) return true;
        if (PointDistance(s2, screenPoint) < tolerance) return true;
        if (PointDistance(s3, screenPoint) < tolerance) return true;

        return false;
    }

    private void DrawParallelGroup(
        SKCanvas canvas,
        SKPaint paint,
        DrawingAnchor a2,
        DrawingAnchor dataAnchor,
        DrawingAnchor dataMedian,
        decimal[] multipliers,
        ICoordinateTransform transform)
    {
        for (int i = 0; i < multipliers.Length; i++)
        {
            var (origin, target) = ProjectParallelLine(a2, dataAnchor, dataMedian, multipliers[i], transform);
            if (IsValidScreenPoint(origin) && IsValidScreenPoint(target))
            {
                DrawRay(canvas, paint, origin, target, isRay: true);
            }
        }
    }

    private bool IsNearParallelGroup(
        DrawingAnchor a2,
        DrawingAnchor dataAnchor,
        DrawingAnchor dataMedian,
        decimal[] multipliers,
        global::Avalonia.Point screenPoint,
        ICoordinateTransform transform,
        double tolerance)
    {
        for (int i = 0; i < multipliers.Length; i++)
        {
            var (origin, target) = ProjectParallelLine(a2, dataAnchor, dataMedian, multipliers[i], transform);
            if (IsValidScreenPoint(origin) && IsValidScreenPoint(target) && IsNearRay(origin, target - origin, screenPoint, tolerance))
            {
                return true;
            }
        }
        return false;
    }

    private bool IsNearRay(global::Avalonia.Point origin, global::Avalonia.Vector direction, global::Avalonia.Point test, double tolerance)
    {
        double len = Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
        if (len == 0) return PointDistance(origin, test) < tolerance;

        global::Avalonia.Vector dirNorm = direction / len;
        global::Avalonia.Vector w = test - origin;
        double t = w.X * dirNorm.X + w.Y * dirNorm.Y;

        if (t < 0) return PointDistance(origin, test) < tolerance;

        global::Avalonia.Point proj = origin + dirNorm * t;
        return PointDistance(proj, test) < tolerance;
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
        return PitchforkValues.Calculate(ToKind(PitchforkType), Points[0].ToAnchor(), Points[1].ToAnchor(), Points[2].ToAnchor(), timestamp, color);
    }
}

