using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Chart object representing a translucent highlighter marker with Multiply blending.
/// Accentuates underlying candles and drawings without washing out contrast.
/// Guarantees zero smoothing, zero point elimination, and zero-allocation rendering.
/// </summary>
public class HighlighterObject : RelativeGeometricRenderer, IAabbDeformableObject
{
    public override ChartObjectType Type => ChartObjectType.Highlighter;

    private byte _alpha = 100;

    [Category("Style")]
    [DisplayName("Alpha")]
    [Range(20, 200)]
    [ParameterTag(DrawingParameterTags.Common)]
    public byte Alpha
    {
        get => _alpha;
        set => _alpha = (byte)Math.Clamp((int)value, 20, 200);
    }

    private SKPaint? _cachedFillPaint;

    private static SKBlendMode GetEffectiveBlendMode()
    {
        // Multiply blend mode works only on light/white paper. On dark backgrounds, Multiply results in 0 (black).
        // Adaptively fallback to standard alpha blend (SrcOver) on dark backgrounds to guarantee visibility.
        var bg = DrawingThemeContext.ChartBackground;
        return (bg.Red + bg.Green + bg.Blue) > 384 ? SKBlendMode.Multiply : SKBlendMode.SrcOver;
    }

    private SKPaint EnsureFillPaint()
    {
        var fillPaint = _cachedFillPaint;
        if (fillPaint == null || fillPaint.Handle == IntPtr.Zero)
        {
            fillPaint = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                IsAntialias = true,
                BlendMode = GetEffectiveBlendMode()
            };
            _cachedFillPaint = fillPaint;
        }
        else
        {
            fillPaint.BlendMode = GetEffectiveBlendMode();
        }
        return fillPaint;
    }

    public HighlighterObject() : base()
    {
        Color = DrawingThemeContext.DefaultColor;
        Thickness = 12.0;

        var paint = EnsurePaint();
        paint.StrokeCap = SKStrokeCap.Square;
        paint.StrokeJoin = SKStrokeJoin.Bevel;
        paint.BlendMode = GetEffectiveBlendMode();

        _cachedFillPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            BlendMode = GetEffectiveBlendMode()
        };
    }

    public HighlighterObject(IEnumerable<ChartPoint> points) : this()
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

        byte effectiveAlpha = (byte)Math.Clamp((int)Alpha, 20, 200);
        var paint = EnsurePaint();
        paint.Color = new SKColor(Color.R, Color.G, Color.B, effectiveAlpha);
        paint.StrokeCap = SKStrokeCap.Square;
        paint.StrokeJoin = SKStrokeJoin.Bevel;
        paint.BlendMode = GetEffectiveBlendMode();
        paint.StrokeWidth = (float)Thickness;
        paint.PathEffect = StrokeStyle.ToSkPathEffect();

        if (Points.Count == 1)
        {
            DrawSinglePoint(canvas, transform, Points[0], paint);
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
            DrawSinglePoint(canvas, transform, first, paint);
            return;
        }

        var path = EnsurePath();
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

    private void DrawSinglePoint(SKCanvas canvas, ICoordinateTransform transform, ChartPoint chartPoint, SKPaint paint)
    {
        var screenPt = transform.ChartToScreen(chartPoint);
        float halfW = (float)(Thickness * 0.5);
        var fillPaint = EnsureFillPaint();
        fillPaint.Color = paint.Color;
        canvas.DrawRect(new SKRect((float)screenPt.X - halfW, (float)screenPt.Y - halfW, (float)screenPt.X + halfW, (float)screenPt.Y + halfW), fillPaint);
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

    /// <summary>
    /// Gets the axis-aligned bounding box of the highlighter stroke in chart coordinates.
    /// </summary>
    public void GetDataBounds(out DateTime minTime, out DateTime maxTime, out decimal minPrice, out decimal maxPrice)
    {
        EnsureDataBounds();
        minTime = _minTime;
        maxTime = _maxTime;
        minPrice = _minPrice;
        maxPrice = _maxPrice;
    }

    /// <summary>
    /// Computes the 8 deformation handle positions in screen coordinates:
    /// 0=TopLeft, 1=TopCenter, 2=TopRight, 3=RightCenter,
    /// 4=BottomRight, 5=BottomCenter, 6=BottomLeft, 7=LeftCenter.
    /// </summary>
    public global::Avalonia.Point[] GetAabbHandlePositions(ICoordinateTransform transform)
    {
        EnsureDataBounds();
        var pMin = transform.ChartToScreen(new ChartPoint(_minTime, _maxPrice));
        var pMax = transform.ChartToScreen(new ChartPoint(_maxTime, _minPrice));
        double left = Math.Min(pMin.X, pMax.X);
        double right = Math.Max(pMin.X, pMax.X);
        double top = Math.Min(pMin.Y, pMax.Y);
        double bottom = Math.Max(pMin.Y, pMax.Y);
        double midX = (left + right) * 0.5;
        double midY = (top + bottom) * 0.5;

        return new global::Avalonia.Point[]
        {
            new global::Avalonia.Point(left, top),     // 0: TopLeft
            new global::Avalonia.Point(midX, top),     // 1: TopCenter
            new global::Avalonia.Point(right, top),    // 2: TopRight
            new global::Avalonia.Point(right, midY),   // 3: RightCenter
            new global::Avalonia.Point(right, bottom), // 4: BottomRight
            new global::Avalonia.Point(midX, bottom),  // 5: BottomCenter
            new global::Avalonia.Point(left, bottom),  // 6: BottomLeft
            new global::Avalonia.Point(left, midY)     // 7: LeftCenter
        };
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

        // Fast AABB screen-space rejection before per-segment check
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
            var curScreen = transform.ChartToScreen(Points[i]);
            SKPoint skCur = new SKPoint((float)curScreen.X, (float)curScreen.Y);

            if (DistanceToLineSegment(target, skPrev, skCur) <= (float)effectiveTolerance)
            {
                return true;
            }

            skPrev = skCur;
        }

        return false;
    }

    private static float DistanceToLineSegment(SKPoint p, SKPoint a, SKPoint b)
    {
        float abX = b.X - a.X;
        float abY = b.Y - a.Y;
        float lengthSquared = abX * abX + abY * abY;

        if (lengthSquared == 0f)
        {
            float dx = p.X - a.X;
            float dy = p.Y - a.Y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        float t = ((p.X - a.X) * abX + (p.Y - a.Y) * abY) / lengthSquared;
        t = Math.Clamp(t, 0f, 1f);

        float projX = a.X + t * abX;
        float projY = a.Y + t * abY;
        float diffX = p.X - projX;
        float diffY = p.Y - projY;

        return (float)Math.Sqrt(diffX * diffX + diffY * diffY);
    }

    public override void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price + priceDelta);
        }
        _boundsPointCount = -1; // Invalidate cached data bounds
    }

    /// <summary>
    /// Performs normalized affine deformation of the highlighter stroke coordinates to match the new bounding box.
    /// Handles boundary degeneracy where delta is zero by translating points rigidly.
    /// </summary>
    public void TransformAabb(DateTime newMinTime, DateTime newMaxTime, decimal newMinPrice, decimal newMaxPrice)
    {
        if (Points.Count == 0) return;

        EnsureDataBounds();

        long origDeltaTicks = (_maxTime - _minTime).Ticks;
        decimal origDeltaPrice = _maxPrice - _minPrice;

        long newDeltaTicks = (newMaxTime - newMinTime).Ticks;
        decimal newDeltaPrice = newMaxPrice - newMinPrice;

        for (int i = 0; i < Points.Count; i++)
        {
            var p = Points[i];

            // Time mapping (preserve UTC kind)
            DateTime mappedTime;
            if (origDeltaTicks > 0)
            {
                double fraction = (double)(p.Time - _minTime).Ticks / (double)origDeltaTicks;
                long mappedTicks = (long)Math.Round(fraction * newDeltaTicks);
                mappedTime = DateTime.SpecifyKind(newMinTime.AddTicks(mappedTicks), DateTimeKind.Utc);
            }
            else
            {
                mappedTime = DateTime.SpecifyKind(newMinTime, DateTimeKind.Utc);
            }

            // Price mapping (decimal precision contract)
            decimal mappedPrice;
            if (origDeltaPrice > 0m)
            {
                decimal fraction = (p.Price - _minPrice) / origDeltaPrice;
                mappedPrice = newMinPrice + fraction * newDeltaPrice;
            }
            else
            {
                mappedPrice = newMinPrice;
            }

            Points[i] = new ChartPoint(mappedTime, mappedPrice);
        }

        _boundsPointCount = -1; // Invalidate cached bounds
    }

    public override void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (!IsVisible) return;

        DrawGeometry(canvas, transform);

        if (IsSelected && Points.Count > 0)
        {
            EnsureDataBounds();
            var pMin = transform.ChartToScreen(new ChartPoint(_minTime, _maxPrice));
            var pMax = transform.ChartToScreen(new ChartPoint(_maxTime, _minPrice));
            double left = Math.Min(pMin.X, pMax.X);
            double right = Math.Max(pMin.X, pMax.X);
            double top = Math.Min(pMin.Y, pMax.Y);
            double bottom = Math.Max(pMin.Y, pMax.Y);

            var screenRect = new global::Avalonia.Rect(left, top, Math.Max(1.0, right - left), Math.Max(1.0, bottom - top));
            SelectionHandleRenderer.DrawAabbHandles(
                canvas,
                screenRect,
                handleColor: DrawingThemeContext.HandleColor,
                anchorColor: DrawingThemeContext.AnchorPointColor,
                frameColor: DrawingThemeContext.DefaultSkColor,
                anchorHandleIndex: AnchorPointIndex);
        }
    }

    public override void Dispose()
    {
        _cachedFillPaint?.Dispose();
        _cachedFillPaint = null;
        base.Dispose();
    }
}
