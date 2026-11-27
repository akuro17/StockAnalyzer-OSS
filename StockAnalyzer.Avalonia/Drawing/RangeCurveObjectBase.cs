using System;
using SkiaSharp;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// A range-analysis tool whose result is a curve through the bars of the range (and, dashed, beyond its end) with an optional band of constant half width around it: the polynomial and nonlinear
/// regression tools and the range regression lines after them. The subclass calculates a <see cref="RangeCurve"/> and hands it over with <see cref="SetCurve"/>; this class draws it, hit-tests it
/// and owns the native resources of the drawing. The curve is kept in chart coordinates (time, price) and converted per frame, so panning and zooming need no recalculation.
/// </summary>
public abstract class RangeCurveObjectBase : RangeAnalysisObjectBase
{
    /// <summary>Alpha of the band fill.</summary>
    private const byte BandFillAlpha = 28;

    /// <summary>Alpha of the band edges.</summary>
    private const byte BandEdgeAlpha = 170;

    private const float ProjectionDashOn = 5f;
    private const float ProjectionDashOff = 4f;
    private const float BandDashOn = 3f;
    private const float BandDashOff = 3f;
    private const float BandEdgeMinWidth = 1f;
    private const double BandEdgeWidthFactor = 0.75;

    private RangeCurve _curve = RangeCurve.Empty;
    private string _overlay = string.Empty;
    private SKPoint[] _center = Array.Empty<SKPoint>();
    private SKPoint[] _upper = Array.Empty<SKPoint>();
    private SKPoint[] _lower = Array.Empty<SKPoint>();

    private readonly SKPath _fillPath = new();
    private readonly SKPath _linePath = new();
    private readonly SKPathEffect _projectionDash = SKPathEffect.CreateDash(new[] { ProjectionDashOn, ProjectionDashOff }, 0f);
    private readonly SKPathEffect _bandDash = SKPathEffect.CreateDash(new[] { BandDashOn, BandDashOff }, 0f);
    private readonly SKPaint _centerPaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _projectionPaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _bandEdgePaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _bandFillPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };

    protected RangeCurveObjectBase()
    {
        _projectionPaint.PathEffect = _projectionDash;
        _bandEdgePaint.PathEffect = _bandDash;
    }

    /// <summary>The cached curve (tests read it; it is not persisted).</summary>
    internal RangeCurve Curve
    {
        get
        {
            lock (SyncRoot)
            {
                return _curve;
            }
        }
    }

    protected override string? OverlayText => _overlay;

    /// <summary>True when the band around the curve is drawn (the curve must carry a band half width).</summary>
    protected virtual bool IsBandVisible => true;

    /// <summary>The colour of the band fill for the line colour <paramref name="lineColor"/>: the line colour with the constant alpha 28 unless the tool offers its own fill settings.</summary>
    protected virtual SKColor BandFillColor(SKColor lineColor) => lineColor.WithAlpha(BandFillAlpha);

    /// <summary>Stores the result (inside <see cref="RangeAnalysisObjectBase.SyncRoot"/>, from <c>ComputeRange</c>) with the text shown over the range.</summary>
    protected void SetCurve(RangeCurve curve, string overlay)
    {
        _curve = curve;
        _overlay = overlay;
        if (_center.Length != curve.Count)
        {
            _center = new SKPoint[curve.Count];
            _upper = new SKPoint[curve.Count];
            _lower = new SKPoint[curve.Count];
        }
    }

    protected override void ClearResult()
    {
        _curve = RangeCurve.Empty;
        _overlay = string.Empty;
        _center = Array.Empty<SKPoint>();
        _upper = Array.Empty<SKPoint>();
        _lower = Array.Empty<SKPoint>();
    }

    protected override void RenderResult(SKCanvas canvas, ICoordinateTransform transform, float leftX, float rightX, SKColor color)
    {
        int count = _curve.Count;
        if (count < 2 || _center.Length != count)
        {
            return;
        }

        decimal half = _curve.BandHalfWidth;
        bool band = IsBandVisible && half > 0m;
        int usable = 0;
        for (int i = 0; i < count; i++)
        {
            global::Avalonia.Point c = transform.ChartToScreen(new ChartPoint(_curve.Times[i], _curve.Prices[i]));
            if (!double.IsFinite(c.X) || !double.IsFinite(c.Y))
            {
                break;
            }

            _center[i] = new SKPoint((float)c.X, (float)c.Y);
            if (band)
            {
                global::Avalonia.Point u = transform.ChartToScreen(new ChartPoint(_curve.Times[i], _curve.Prices[i] + half));
                global::Avalonia.Point l = transform.ChartToScreen(new ChartPoint(_curve.Times[i], _curve.Prices[i] - half));
                if (!double.IsFinite(u.Y) || !double.IsFinite(l.Y))
                {
                    break;
                }

                _upper[i] = new SKPoint((float)u.X, (float)u.Y);
                _lower[i] = new SKPoint((float)l.X, (float)l.Y);
            }

            usable = i + 1;
        }

        if (usable < 2)
        {
            return;
        }

        float edgeWidth = (float)Math.Max(BandEdgeMinWidth, Thickness * BandEdgeWidthFactor);
        if (band)
        {
            _bandFillPaint.Color = BandFillColor(color);
            _fillPath.Rewind();
            _fillPath.MoveTo(_upper[0]);
            for (int i = 1; i < usable; i++)
            {
                _fillPath.LineTo(_upper[i]);
            }

            for (int i = usable - 1; i >= 0; i--)
            {
                _fillPath.LineTo(_lower[i]);
            }

            _fillPath.Close();
            canvas.DrawPath(_fillPath, _bandFillPaint);

            _bandEdgePaint.Color = color.WithAlpha(BandEdgeAlpha);
            _bandEdgePaint.StrokeWidth = edgeWidth;
            DrawPolyline(canvas, _upper, 0, usable - 1, _bandEdgePaint);
            DrawPolyline(canvas, _lower, 0, usable - 1, _bandEdgePaint);
        }

        _centerPaint.Color = color;
        _centerPaint.StrokeWidth = (float)Thickness;
        int fittedEnd = Math.Min(_curve.FittedCount, usable) - 1;
        DrawPolyline(canvas, _center, 0, fittedEnd, _centerPaint);
        if (usable - 1 > fittedEnd && fittedEnd >= 0)
        {
            _projectionPaint.Color = color;
            _projectionPaint.StrokeWidth = (float)Thickness;
            DrawPolyline(canvas, _center, fittedEnd, usable - 1, _projectionPaint);
        }
    }

    protected override bool HitTestResult(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance)
    {
        int count = _curve.Count;
        if (count < 2)
        {
            return false;
        }

        decimal half = IsBandVisible ? _curve.BandHalfWidth : 0m;
        global::Avalonia.Point previous = transform.ChartToScreen(new ChartPoint(_curve.Times[0], _curve.Prices[0]));
        for (int i = 1; i < count; i++)
        {
            global::Avalonia.Point next = transform.ChartToScreen(new ChartPoint(_curve.Times[i], _curve.Prices[i]));
            double xa = previous.X;
            double xb = next.X;
            if (double.IsFinite(xa) && double.IsFinite(xb) && xb > xa && screenPoint.X >= xa && screenPoint.X <= xb)
            {
                double t = (screenPoint.X - xa) / (xb - xa);
                double centerY = previous.Y + (t * (next.Y - previous.Y));
                if (half <= 0m)
                {
                    return Math.Abs(screenPoint.Y - centerY) <= tolerance;
                }

                // The band: the vertical extent of the half width at the two ends of the segment, interpolated.
                double upperA = transform.ChartToScreen(new ChartPoint(_curve.Times[i - 1], _curve.Prices[i - 1] + half)).Y;
                double upperB = transform.ChartToScreen(new ChartPoint(_curve.Times[i], _curve.Prices[i] + half)).Y;
                double lowerA = transform.ChartToScreen(new ChartPoint(_curve.Times[i - 1], _curve.Prices[i - 1] - half)).Y;
                double lowerB = transform.ChartToScreen(new ChartPoint(_curve.Times[i], _curve.Prices[i] - half)).Y;
                double yUpper = upperA + (t * (upperB - upperA));
                double yLower = lowerA + (t * (lowerB - lowerA));
                return screenPoint.Y >= Math.Min(yUpper, yLower) - tolerance && screenPoint.Y <= Math.Max(yUpper, yLower) + tolerance;
            }

            previous = next;
        }

        return false;
    }

    protected override void DisposeNativeResources()
    {
        _fillPath.Dispose();
        _linePath.Dispose();
        _centerPaint.Dispose();
        _projectionPaint.Dispose();
        _bandEdgePaint.Dispose();
        _bandFillPaint.Dispose();
        _projectionDash.Dispose();
        _bandDash.Dispose();
    }

    /// <summary>Draws the points <paramref name="from"/> .. <paramref name="to"/> (both included) as one polyline with the reused line path.</summary>
    private void DrawPolyline(SKCanvas canvas, SKPoint[] points, int from, int to, SKPaint paint)
    {
        if (to <= from)
        {
            return;
        }

        _linePath.Rewind();
        _linePath.MoveTo(points[from]);
        for (int i = from + 1; i <= to; i++)
        {
            _linePath.LineTo(points[i]);
        }

        canvas.DrawPath(_linePath, paint);
    }
}
