using System;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Catenary Curve Trendline drawing object.
/// Subclasses RelativeGeometricRenderer for Zero-Allocation rendering in the hot path.
/// Uses 3 anchor points: Points[0]=P0 (start), Points[1]=P1 (end), Points[2]=P2 (sag control).
/// Automatically maintains P2 at the midpoint between P0 and P1.
/// </summary>
public class CatenaryCurveObject : RelativeGeometricRenderer
{
    public override ChartObjectType Type => ChartObjectType.CatenaryCurve;

    public bool ShowProjection { get; set; } = false;
    public int ProjectionBars { get; set; } = 10;

    // Zero-Allocation cached paints & pre-allocated resources
    private readonly SKPaint _projectionPaint;
    private readonly SKPath _cachedProjectionPath;

    // Cache of solved parameters for HitTest and fast re-evaluation
    private CatenaryParams? _cachedParams;

    public CatenaryCurveObject() : base()
    {
        _projectionPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash(new float[] { ChartConstants.DefaultTrendProjectionDashInterval, ChartConstants.DefaultTrendProjectionDashInterval }, 0)
        };
        _cachedProjectionPath = new SKPath();
    }

    public CatenaryCurveObject(ChartPoint p0, ChartPoint p1, ChartPoint p2) : this()
    {
        Points.Add(p0);
        Points.Add(p1);
        Points.Add(p2);
        SynchronizeMidpoint();
    }

    /// <summary>
    /// Synchronizes P2's timestamp to the exact midpoint between P0 and P1.
    /// </summary>
    public void SynchronizeMidpoint()
    {
        if (Points.Count >= 3)
        {
            long midTicks = (Points[0].Time.Ticks + Points[1].Time.Ticks) / 2;
            Points[2] = new ChartPoint(new DateTime(midTicks), Points[2].Price);
        }
    }

    protected override void DrawGeometry(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Points.Count < 3) return;

        var s0 = transform.ChartToScreen(Points[0]);
        var s1 = transform.ChartToScreen(Points[1]);
        var s2 = transform.ChartToScreen(Points[2]);

        // Auto-center sag point X between s0.X and s1.X for mathematical stability
        var centeredSag = new global::Avalonia.Point((s0.X + s1.X) / 2.0, s2.Y);

        _cachedParams = CatenaryMath.Solve(s0, s1, centeredSag);
        if (_cachedParams == null) return;

        var p = _cachedParams.Value;
        _cachedPath.Rewind();

        if (p.IsLinear)
        {
            // Linear straight segment
            _cachedPath.MoveTo((float)p.X1, (float)p.Y1);
            _cachedPath.LineTo((float)p.X2, (float)p.Y2);
            canvas.DrawPath(_cachedPath, _cachedPaint);
            return;
        }

        // Subdivide curve into N segments based on screen width
        double width = p.X2 - p.X1;
        int nSegments = Math.Clamp((int)(width / 4.0), 20, 120);
        double dx = width / nSegments;

        _cachedPath.MoveTo((float)p.X1, (float)p.EvaluateY(p.X1));
        for (int i = 1; i <= nSegments; i++)
        {
            double x = p.X1 + i * dx;
            double y = p.EvaluateY(x);
            _cachedPath.LineTo((float)x, (float)y);
        }

        canvas.DrawPath(_cachedPath, _cachedPaint);

        // Forward Projection (if enabled)
        if (ShowProjection && ProjectionBars > 0)
        {
            double columnWidth = GetColumnWidth(transform);
            double projLength = columnWidth * ProjectionBars;
            double xEnd = p.X2 + projLength;
            int nProjSegments = Math.Clamp((int)(projLength / 4.0), 10, 60);
            double projDx = projLength / nProjSegments;

            _projectionPaint.Color = SkiaColor.WithAlpha(ChartConstants.DefaultTrendProjectionAlpha);
            _projectionPaint.StrokeWidth = (float)Thickness;

            _cachedProjectionPath.Rewind();
            _cachedProjectionPath.MoveTo((float)p.X2, (float)p.EvaluateY(p.X2));
            for (int i = 1; i <= nProjSegments; i++)
            {
                double x = p.X2 + i * projDx;
                double y = p.EvaluateY(x);
                _cachedProjectionPath.LineTo((float)x, (float)y);
            }

            canvas.DrawPath(_cachedProjectionPath, _projectionPaint);
        }
    }

    public override bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 3) return false;

        var s0 = transform.ChartToScreen(Points[0]);
        var s1 = transform.ChartToScreen(Points[1]);
        var s2 = transform.ChartToScreen(Points[2]);
        var centeredSag = new global::Avalonia.Point((s0.X + s1.X) / 2.0, s2.Y);

        var pNullable = CatenaryMath.Solve(s0, s1, centeredSag);
        if (pNullable == null) return false;

        var p = pNullable.Value;
        double effectiveTolerance = tolerance + (Thickness / 2.0);

        // Check bounding X interval with tolerance
        if (screenPoint.X < p.X1 - effectiveTolerance || screenPoint.X > p.X2 + effectiveTolerance)
        {
            // Check projection range if ShowProjection is true
            if (ShowProjection && ProjectionBars > 0)
            {
                double columnWidth = GetColumnWidth(transform);
                double xEnd = p.X2 + (columnWidth * ProjectionBars);
                if (screenPoint.X < p.X1 - effectiveTolerance || screenPoint.X > xEnd + effectiveTolerance)
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        double dist = p.DistanceToPoint(screenPoint.X, screenPoint.Y);
        return dist <= effectiveTolerance;
    }

    public override void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        base.Translate(timeDelta, priceDelta);
        SynchronizeMidpoint();
    }

    public override void Dispose()
    {
        _projectionPaint?.Dispose();
        _cachedProjectionPath?.Dispose();
        base.Dispose();
    }

    private static double GetColumnWidth(ICoordinateTransform transform)
    {
        double width = (transform.TimeMap != null)
            ? Math.Abs(transform.NumericToScreen(1, 0).X - transform.NumericToScreen(0, 0).X)
            : (transform.ViewportWidth > 0 ? transform.ViewportWidth / 80.0 : 10.0);

        return width > 0 ? width : 10.0;
    }
}
