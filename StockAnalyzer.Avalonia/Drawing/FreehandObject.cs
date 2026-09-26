using System;
using System.Collections.Generic;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Chart object representing a freehand stroke captured as an exact sequence of raw points.
/// Guarantees zero smoothing, zero point elimination, and zero-allocation rendering.
/// </summary>
public class FreehandObject : RelativeGeometricRenderer
{
    public override ChartObjectType Type => ChartObjectType.Freehand;

    private SKPaint _cachedFillPaint;

    private SKPaint EnsureFillPaint()
    {
        var fillPaint = _cachedFillPaint;
        if (fillPaint == null || fillPaint.Handle == IntPtr.Zero)
        {
            fillPaint = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };
            _cachedFillPaint = fillPaint;
        }
        return fillPaint;
    }

    public FreehandObject()
    {
        Color = DrawingThemeContext.DefaultColor;
        Thickness = DrawingThemeContext.DefaultStrokeThickness;

        var paint = EnsurePaint();
        paint.StrokeCap = SKStrokeCap.Round;
        paint.StrokeJoin = SKStrokeJoin.Round;

        _cachedFillPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
    }

    public FreehandObject(IEnumerable<ChartPoint> points) : this()
    {
        if (points == null)
        {
            throw new ArgumentNullException(nameof(points));
        }
        Points.AddRange(points);
    }

    protected override void DrawGeometry(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Points.Count == 0)
        {
            return;
        }

        if (Points.Count == 1)
        {
            DrawSinglePoint(canvas, transform, Points[0]);
            return;
        }

        // Check if all points are identical
        bool allIdentical = true;
        var first = Points[0];
        for (int i = 1; i < Points.Count; i++)
        {
            if (Points[i].Time != first.Time || Points[i].Price != first.Price)
            {
                allIdentical = false;
                break;
            }
        }

        if (allIdentical)
        {
            DrawSinglePoint(canvas, transform, first);
            return;
        }

        var paint = EnsurePaint();
        var path = EnsurePath();

        // Draw line segments connecting adjacent points sequentially
        paint.StrokeCap = SKStrokeCap.Round;
        paint.StrokeJoin = SKStrokeJoin.Round;

        path.Reset();
        var startPt = transform.ChartToScreen(Points[0]);
        path.MoveTo((float)startPt.X, (float)startPt.Y);

        for (int i = 1; i < Points.Count; i++)
        {
            var nextPt = transform.ChartToScreen(Points[i]);
            path.LineTo((float)nextPt.X, (float)nextPt.Y);
        }

        canvas.DrawPath(path, paint);
    }

    public override void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (!IsVisible) return;

        var paint = EnsurePaint();
        var path = EnsurePath();

        paint.Color = SkiaColor;
        paint.StrokeWidth = (float)Thickness;
        path.Reset();

        DrawGeometry(canvas, transform);
    }

    private void DrawSinglePoint(SKCanvas canvas, ICoordinateTransform transform, ChartPoint chartPoint)
    {
        var fillPaint = EnsureFillPaint();
        fillPaint.Color = SkiaColor;
        var screenPt = transform.ChartToScreen(chartPoint);
        float radius = (float)(Thickness / 2.0);
        canvas.DrawCircle((float)screenPt.X, (float)screenPt.Y, radius, fillPaint);
    }

    private DateTime _minTime = DateTime.MaxValue;
    private DateTime _maxTime = DateTime.MinValue;
    private decimal _minPrice = decimal.MaxValue;
    private decimal _maxPrice = decimal.MinValue;
    private int _boundsPointCount = -1;

    private void EnsureDataBounds()
    {
        if (_boundsPointCount == Points.Count) return;
        if (Points.Count == 0)
        {
            _minTime = _maxTime = DateTime.MinValue;
            _minPrice = _maxPrice = 0m;
            _boundsPointCount = 0;
            return;
        }

        DateTime minT = Points[0].Time;
        DateTime maxT = Points[0].Time;
        decimal minP = Points[0].Price;
        decimal maxP = Points[0].Price;

        for (int i = 1; i < Points.Count; i++)
        {
            var p = Points[i];
            if (p.Time < minT) minT = p.Time;
            if (p.Time > maxT) maxT = p.Time;
            if (p.Price < minP) minP = p.Price;
            if (p.Price > maxP) maxP = p.Price;
        }

        _minTime = minT;
        _maxTime = maxT;
        _minPrice = minP;
        _maxPrice = maxP;
        _boundsPointCount = Points.Count;
    }

    public override bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count == 0 || !IsVisible)
        {
            return false;
        }

        double effectiveTolerance = Math.Max(tolerance, Thickness / 2.0);

        if (Points.Count == 1)
        {
            var p = transform.ChartToScreen(Points[0]);
            double dx = screenPoint.X - p.X;
            double dy = screenPoint.Y - p.Y;
            return (dx * dx + dy * dy) <= (effectiveTolerance * effectiveTolerance);
        }

        // Fast AABB (Axis-Aligned Bounding Box) screen-space rejection before per-segment check
        EnsureDataBounds();
        var pMin = transform.ChartToScreen(new ChartPoint(_minTime, _maxPrice));
        var pMax = transform.ChartToScreen(new ChartPoint(_maxTime, _minPrice));
        double left = Math.Min(pMin.X, pMax.X) - effectiveTolerance;
        double right = Math.Max(pMin.X, pMax.X) + effectiveTolerance;
        double top = Math.Min(pMin.Y, pMax.Y) - effectiveTolerance;
        double bottom = Math.Max(pMin.Y, pMax.Y) + effectiveTolerance;

        if (screenPoint.X < left || screenPoint.X > right || screenPoint.Y < top || screenPoint.Y > bottom)
        {
            return false;
        }

        SKPoint target = new SKPoint((float)screenPoint.X, (float)screenPoint.Y);
        var prevScreen = transform.ChartToScreen(Points[0]);
        SKPoint skPrev = new SKPoint((float)prevScreen.X, (float)prevScreen.Y);

        for (int i = 1; i < Points.Count; i++)
        {
            var currScreen = transform.ChartToScreen(Points[i]);
            SKPoint skCurr = new SKPoint((float)currScreen.X, (float)currScreen.Y);

            if (BezierSplineMath.DistancePointToSegment(target, skPrev, skCurr) <= effectiveTolerance)
            {
                return true;
            }

            skPrev = skCurr;
        }

        return false;
    }

    /// <summary>
    /// Freehand points are permanently fixed in chart coordinates (Time and Price) once committed.
    /// Whole-object translation is strictly prohibited.
    /// </summary>
    public override void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        // Fixed position: suppress translation
    }

    public override void Dispose()
    {
        var fill = _cachedFillPaint;
        _cachedFillPaint = null!;
        fill?.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
