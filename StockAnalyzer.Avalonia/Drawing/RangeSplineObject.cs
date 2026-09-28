namespace StockAnalyzer.Avalonia.Drawing;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using global::Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

/// <summary>
/// Range Data Interpolation Spline Drawing Object (Prompt 61-7 / Range Spline).
/// Extracts price series for a user-specified time range [T_start, T_end] via O(log N) binary search
/// and renders a uniform Catmull-Rom Cubic Bézier spline with Zero-Allocation.
/// Supports opt-in auto-extrema horizontal levels and Y-axis projection (Prompt 61-8).
/// </summary>
public class RangeSplineObject : RelativeGeometricRenderer, IExtremaLevelSupport, IAxisProjectable
{
    public override ChartObjectType Type => ChartObjectType.RangeSpline;

    /// <summary>
    /// Price source selector used for range spline extraction (SSoT: PriceType).
    /// </summary>
    public PriceType PriceSource { get; set; } = PriceType.Close;

    /// <summary>
    /// Backward-compatibility property for PriceField.
    /// </summary>
    public PriceField PriceField
    {
        get => PriceSource switch
        {
            PriceType.Close => PriceField.Close,
            PriceType.Open => PriceField.Open,
            PriceType.High => PriceField.High,
            PriceType.Low => PriceField.Low,
            PriceType.Median => PriceField.MedianHL,
            PriceType.Typical => PriceField.TypicalHLC,
            PriceType.Weighted => PriceField.WeightedHLCC,
            _ => PriceField.Close
        };
        set => PriceSource = value switch
        {
            PriceField.Close => PriceType.Close,
            PriceField.Open => PriceType.Open,
            PriceField.High => PriceType.High,
            PriceField.Low => PriceType.Low,
            PriceField.MedianHL => PriceType.Median,
            PriceField.TypicalHLC => PriceType.Typical,
            PriceField.WeightedHLCC => PriceType.Weighted,
            _ => PriceType.Close
        };
    }

    public double Tension { get; set; } = BezierSplineMath.DefaultTension;

    public double MinSwingPercent { get; set; } = 2.0;

    public int MaxLevels { get; set; } = 5;

    public double ClusterTolerancePx { get; set; } = 15.0;

    public bool ShowResistanceLevels { get; set; } = false;
    public bool ShowSupportLevels { get; set; } = false;

    public bool ShowHorizontalLevels
    {
        get => ShowResistanceLevels || ShowSupportLevels;
        set
        {
            ShowResistanceLevels = value;
            ShowSupportLevels = value;
        }
    }

    private readonly List<ChartPoint> _extractedPoints = new();
    public IReadOnlyList<ChartPoint> ExtractedPoints => _extractedPoints;

    // Time range [_lastRecalcStart, _lastRecalcEnd] that _extractedPoints was computed
    // from. Used by DrawGeometry() to detect when Points[] has moved since the last
    // Recalculate() (e.g. mid-drag on an existing object, where
    // ChartInteractionController.HandleObjectDrag intentionally defers Recalculate() to
    // HandlePointerReleased for performance) so it can fall back to a live raw-point
    // preview instead of rendering a frozen, stale spline.
    private DateTime _lastRecalcStart;
    private DateTime _lastRecalcEnd;

    private readonly List<ExtremaLevel> _cachedExtrema = new(32);
    public IReadOnlyList<ExtremaLevel> ExtremaLevels => _cachedExtrema;

    private readonly SKPaint _extremaHighPaint;
    private readonly SKPaint _extremaLowPaint;

    public RangeSplineObject() : base()
    {
        Color = Colors.Green;

        _extremaHighPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.0f,
            IsAntialias = true,
            Color = new SKColor(239, 83, 80, 180), // Resistance Red (Alpha 180)
            PathEffect = SKPathEffect.CreateDash(new float[] { 4f, 4f }, 0f)
        };

        _extremaLowPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.0f,
            IsAntialias = true,
            Color = new SKColor(38, 166, 154, 180), // Support Green (Alpha 180)
            PathEffect = SKPathEffect.CreateDash(new float[] { 4f, 4f }, 0f)
        };
    }

    public RangeSplineObject(ChartPoint start, ChartPoint end) : this()
    {
        Points.Add(start);
        Points.Add(end);
    }

    public void InvalidateExtrema()
    {
        _cachedExtrema.Clear();
    }

    public void UpdateExtremaCache(ICoordinateTransform transform)
    {
        _cachedExtrema.Clear();
        int count = _extractedPoints.Count;
        if (count < 2 || transform == null) return;

        SKPoint[]? rentedPts = null;
        Span<SKPoint> screenPoints = count <= 128
            ? stackalloc SKPoint[count]
            : (rentedPts = ArrayPool<SKPoint>.Shared.Rent(count)).AsSpan(0, count);

        try
        {
            for (int i = 0; i < count; i++)
            {
                var pt = transform.ChartToScreen(_extractedPoints[i]);
                screenPoints[i] = new SKPoint((float)pt.X, (float)pt.Y);
            }

            int maxRoots = (count - 1) * 2;
            BezierExtremum[]? rentedExtrema = null;
            Span<BezierExtremum> tempLevels = maxRoots <= 64
                ? stackalloc BezierExtremum[maxRoots]
                : (rentedExtrema = ArrayPool<BezierExtremum>.Shared.Rent(maxRoots)).AsSpan(0, maxRoots);

            try
            {
                int levelCount = BezierSplineMath.ExtractSplineExtrema(
                    screenPoints, tempLevels, Tension, isTimeDecoupled: true, mergeTolerancePx: 0.0);

                ExtremaLevel[]? rentedRaw = null;
                Span<ExtremaLevel> rawLevels = levelCount <= 64
                    ? stackalloc ExtremaLevel[levelCount]
                    : (rentedRaw = ArrayPool<ExtremaLevel>.Shared.Rent(levelCount)).AsSpan(0, levelCount);

                try
                {
                    decimal minPrice = decimal.MaxValue, maxPrice = decimal.MinValue;
                    for (int i = 0; i < count; i++)
                    {
                        var p = _extractedPoints[i].Price;
                        if (p < minPrice) minPrice = p;
                        if (p > maxPrice) maxPrice = p;
                    }

                    for (int i = 0; i < levelCount; i++)
                    {
                        var be = tempLevels[i];
                        float snappedY = MathF.Floor((float)be.ScreenY) + 0.5f;

                        // Unsnapped ScreenY is used for exact price reconstruction
                        var chartCoord = transform.ScreenToChart(new global::Avalonia.Point(0, be.ScreenY));
                        rawLevels[i] = new ExtremaLevel(chartCoord.Price, be.ScreenY, snappedY, be.Type, be.SegmentIndex, be.ParameterT);
                    }

                    BezierSplineMath.FilterAndClusterExtrema(
                        rawLevels.Slice(0, levelCount),
                        _cachedExtrema,
                        minSwingPercent: MinSwingPercent,
                        clusterTolerancePx: ClusterTolerancePx,
                        maxLevels: MaxLevels,
                        totalSpanMin: minPrice,
                        totalSpanMax: maxPrice);
                }
                finally
                {
                    if (rentedRaw != null) ArrayPool<ExtremaLevel>.Shared.Return(rentedRaw);
                }
            }
            finally
            {
                if (rentedExtrema != null) ArrayPool<BezierExtremum>.Shared.Return(rentedExtrema);
            }
        }
        finally
        {
            if (rentedPts != null) ArrayPool<SKPoint>.Shared.Return(rentedPts);
        }
    }

    /// <summary>
    /// Extracts candles in range [Points[0].Time, Points[1].Time] using binary search and computes price values.
    /// </summary>
    public void Recalculate(IEnumerable<CoreCandleData>? candles)
    {
        _extractedPoints.Clear();
        InvalidateExtrema();
        if (candles == null || Points.Count < 2) return;

        var t0 = Points[0].Time;
        var t1 = Points[1].Time;
        var tMin = t0 < t1 ? t0 : t1;
        var tMax = t0 < t1 ? t1 : t0;
        _lastRecalcStart = tMin;
        _lastRecalcEnd = tMax;

        if (candles is IReadOnlyList<CoreCandleData> list)
        {
            int count = list.Count;
            if (count == 0) return;

            // Binary search lower bound (first candle with Timestamp >= tMin)
            int low = 0, high = count - 1;
            int startIndex = -1;
            while (low <= high)
            {
                int mid = low + ((high - low) >> 1);
                if (list[mid].Timestamp >= tMin)
                {
                    startIndex = mid;
                    high = mid - 1;
                }
                else
                {
                    low = mid + 1;
                }
            }

            // Binary search upper bound (last candle with Timestamp <= tMax)
            low = 0; high = count - 1;
            int endIndex = -1;
            while (low <= high)
            {
                int mid = low + ((high - low) >> 1);
                if (list[mid].Timestamp <= tMax)
                {
                    endIndex = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            if (startIndex != -1 && endIndex != -1 && startIndex <= endIndex)
            {
                for (int i = startIndex; i <= endIndex; i++)
                {
                    var c = list[i];
                    _extractedPoints.Add(new ChartPoint(c.Timestamp, ExtractPrice(c, PriceSource)));
                }
            }
        }
        else
        {
            foreach (var c in candles)
            {
                if (c.Timestamp >= tMin && c.Timestamp <= tMax)
                {
                    _extractedPoints.Add(new ChartPoint(c.Timestamp, ExtractPrice(c, PriceSource)));
                }
            }
        }

        // Sync Points[]'s price to the extracted curve's actual start/end price (the
        // user's raw click price is otherwise never touched again after creation, so a
        // click that landed even slightly off the real candle data leaves the drawn
        // handle markers -- and the raw Points[]-based hit-test used to start a handle
        // drag -- permanently offset from where the curve is actually rendered). Mirrors
        // RegressionTrendObject.Recalculate()'s equivalent sync. Each point keeps its own
        // chronological identity (earlier vs. later time) so a handle already being
        // dragged doesn't jump to the other end mid-drag.
        if (_extractedPoints.Count > 0)
        {
            decimal priceStart = _extractedPoints[0].Price;
            decimal priceEnd = _extractedPoints[_extractedPoints.Count - 1].Price;
            if (t0 <= t1)
            {
                Points[0] = new ChartPoint(t0, priceStart);
                Points[1] = new ChartPoint(t1, priceEnd);
            }
            else
            {
                Points[0] = new ChartPoint(t0, priceEnd);
                Points[1] = new ChartPoint(t1, priceStart);
            }
        }
    }

    /// <summary>
    /// Extracts price with decimal precision based on the specified PriceType (SSoT).
    /// </summary>
    public static decimal ExtractPrice(CoreCandleData candle, PriceType source)
        => PriceDataHelper.ExtractPrice(candle, source);

    /// <summary>
    /// Extracts price with decimal precision based on the specified PriceField (FR-61-7-02 backward compatibility).
    /// </summary>
    public static decimal ExtractPrice(CoreCandleData candle, PriceField field)
    {
        return field switch
        {
            PriceField.Close => candle.Close,
            PriceField.High => candle.High,
            PriceField.Low => candle.Low,
            PriceField.Open => candle.Open,
            PriceField.MedianHL => (candle.High + candle.Low) / 2m,
            PriceField.TypicalHLC => (candle.High + candle.Low + candle.Close) / 3m,
            PriceField.WeightedHLCC => (candle.High + candle.Low + 2m * candle.Close) / 4m,
            _ => candle.Close
        };
    }

    protected override void DrawGeometry(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null) return;

        if (_extractedPoints.Count < 2)
        {
            // Not enough candles in the [Points[0].Time, Points[1].Time] range yet to
            // extract a spline (e.g. right after the first click, before the user has
            // dragged/moved far enough). Still show the anchor point so the user gets
            // immediate feedback of where point 1 is, consistent with other 2-point
            // drawing tools whose raw line/shape geometry is visible immediately.
            if (Points.Count > 0)
            {
                SelectionHandleRenderer.Draw(canvas, transform.ChartToScreen(Points[0]), radius: ChartConstants.SelectedHandleRadius);
            }
            return;
        }

        if (Points.Count >= 2)
        {
            var p0Time = Points[0].Time;
            var p1Time = Points[1].Time;
            var currentStart = p0Time < p1Time ? p0Time : p1Time;
            var currentEnd = p0Time < p1Time ? p1Time : p0Time;
            bool isStale = currentStart != _lastRecalcStart || currentEnd != _lastRecalcEnd;

            if (isStale)
            {
                // Points[] has moved since _extractedPoints was last computed by
                // Recalculate() (see the field comment above). Draw a lightweight raw
                // straight-line preview directly from Points[] so the curve visibly
                // follows the cursor instead of staying frozen at its pre-drag shape
                // until release, when Recalculate() runs again and the real spline
                // (rendered below) takes over.
                var previewStart = transform.ChartToScreen(Points[0]);
                var previewEnd = transform.ChartToScreen(Points[1]);
                canvas.DrawLine((float)previewStart.X, (float)previewStart.Y, (float)previewEnd.X, (float)previewEnd.Y, _cachedPaint);
                DrawControlPointHandles(canvas, transform);
                return;
            }
        }

        // 1. Render Background Horizontal Levels (Lower Z-Order)
        if (ShowResistanceLevels || ShowSupportLevels)
        {
            UpdateExtremaCache(transform);
            float canvasWidth = (float)transform.CanvasWidth;

            for (int i = 0; i < _cachedExtrema.Count; i++)
            {
                var level = _cachedExtrema[i];
                if (level.Type == ExtremaType.High && !ShowResistanceLevels) continue;
                if (level.Type == ExtremaType.Low && !ShowSupportLevels) continue;

                var paint = level.Type == ExtremaType.High ? _extremaHighPaint : _extremaLowPaint;
                canvas.DrawLine(0f, level.SnappedY, canvasWidth, level.SnappedY, paint);
            }
        }

        // 2. Render Main Spline (Higher Z-Order)
        int count = _extractedPoints.Count;
        if (count == 2)
        {
            var s0 = transform.ChartToScreen(_extractedPoints[0]);
            var s1 = transform.ChartToScreen(_extractedPoints[1]);
            _cachedPath.Rewind();
            _cachedPath.MoveTo((float)s0.X, (float)s0.Y);
            _cachedPath.LineTo((float)s1.X, (float)s1.Y);
            canvas.DrawPath(_cachedPath, _cachedPaint);
            DrawControlPointHandles(canvas, transform);
            return;
        }

        SKPoint[]? rentedPts = null;
        Span<SKPoint> screenPoints = count <= 128
            ? stackalloc SKPoint[count]
            : (rentedPts = ArrayPool<SKPoint>.Shared.Rent(count)).AsSpan(0, count);

        try
        {
            for (int i = 0; i < count; i++)
            {
                var pt = transform.ChartToScreen(_extractedPoints[i]);
                screenPoints[i] = new SKPoint((float)pt.X, (float)pt.Y);
            }

            _cachedPaint.StrokeJoin = SKStrokeJoin.Round;
            _cachedPaint.StrokeCap = SKStrokeCap.Round;
            _cachedPath.Rewind();

            BezierSplineMath.BuildTimeDecoupledCatmullRomSplinePath(_cachedPath, screenPoints, Tension);

            canvas.DrawPath(_cachedPath, _cachedPaint);
        }
        finally
        {
            if (rentedPts != null) ArrayPool<SKPoint>.Shared.Return(rentedPts);
        }

        DrawControlPointHandles(canvas, transform);
    }

    /// <summary>
    /// Draws Points[] handle markers unconditionally (not gated by IsSelected, unlike the
    /// base RelativeGeometricRenderer.Render() loop). While placing point 2 during
    /// two-click creation the object is never IsSelected (that only becomes true once it
    /// joins ChartObjectManager on finish), so relying on the base class's gated loop made
    /// the handles vanish the moment enough candles existed to extract a spline -- only
    /// the curve stayed visible.
    /// </summary>
    private void DrawControlPointHandles(SKCanvas canvas, ICoordinateTransform transform)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            SelectionHandleRenderer.Draw(canvas, transform.ChartToScreen(Points[i]), radius: ChartConstants.SelectedHandleRadius);
        }
    }

    public override bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (transform == null || _extractedPoints.Count == 0) return false;

        int count = _extractedPoints.Count;
        if (count == 1)
        {
            var p = transform.ChartToScreen(_extractedPoints[0]);
            double dx = p.X - screenPoint.X;
            double dy = p.Y - screenPoint.Y;
            return Math.Sqrt(dx * dx + dy * dy) <= tolerance;
        }

        SKPoint[]? rentedPts = null;
        Span<SKPoint> screenPoints = count <= 128
            ? stackalloc SKPoint[count]
            : (rentedPts = ArrayPool<SKPoint>.Shared.Rent(count)).AsSpan(0, count);

        try
        {
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;

            for (int i = 0; i < count; i++)
            {
                var pt = transform.ChartToScreen(_extractedPoints[i]);
                screenPoints[i] = new SKPoint((float)pt.X, (float)pt.Y);

                if (screenPoints[i].X < minX) minX = screenPoints[i].X;
                if (screenPoints[i].X > maxX) maxX = screenPoints[i].X;
                if (screenPoints[i].Y < minY) minY = screenPoints[i].Y;
                if (screenPoints[i].Y > maxY) maxY = screenPoints[i].Y;
            }

            double effectiveTolerance = tolerance + (Thickness / 2.0);

            // Bounding box pre-check
            if (screenPoint.X < minX - effectiveTolerance || screenPoint.X > maxX + effectiveTolerance ||
                screenPoint.Y < minY - effectiveTolerance || screenPoint.Y > maxY + effectiveTolerance)
            {
                return false;
            }

            SKPoint skPt = new SKPoint((float)screenPoint.X, (float)screenPoint.Y);
            if (count == 2)
            {
                return BezierSplineMath.DistancePointToSegment(skPt, screenPoints[0], screenPoints[1]) <= effectiveTolerance;
            }

            for (int i = 0; i < count - 1; i++)
            {
                SKPoint pCurr = screenPoints[i];
                SKPoint pNext = screenPoints[i + 1];

                if (BezierSplineMath.DistanceSquared(pCurr, pNext) < BezierSplineMath.EpsilonSquared)
                    continue;

                SKPoint pPrev = (i == 0)
                    ? new SKPoint(2f * screenPoints[0].X - screenPoints[1].X, 2f * screenPoints[0].Y - screenPoints[1].Y)
                    : screenPoints[i - 1];

                SKPoint pNextNext = (i == count - 2)
                    ? new SKPoint(2f * screenPoints[count - 1].X - screenPoints[count - 2].X, 2f * screenPoints[count - 1].Y - screenPoints[count - 2].Y)
                    : screenPoints[i + 2];

                BezierSplineMath.CalculateTimeDecoupledControlPoints(pPrev, pCurr, pNext, pNextNext, Tension, out var c1, out var c2);

                if (BezierSplineMath.HitTestCubicSegment(skPt, pCurr, c1, c2, pNext, effectiveTolerance))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            if (rentedPts != null) ArrayPool<SKPoint>.Shared.Return(rentedPts);
        }
    }

    public override void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        base.Translate(timeDelta, priceDelta);
        InvalidateExtrema();
        for (int i = 0; i < _extractedPoints.Count; i++)
        {
            _extractedPoints[i] = new ChartPoint(
                _extractedPoints[i].Time.Add(timeDelta),
                _extractedPoints[i].Price + priceDelta);
        }
    }

    public IEnumerable<AxisLabelRequest> GetAxisProjections(ChartDataSnapshot snapshot, IChartRenderConfig config)
    {
        if ((!ShowResistanceLevels && !ShowSupportLevels) || !IsVisible || _cachedExtrema.Count == 0)
            yield break;

        for (int i = 0; i < _cachedExtrema.Count; i++)
        {
            var level = _cachedExtrema[i];
            if (level.Type == ExtremaType.High && !ShowResistanceLevels) continue;
            if (level.Type == ExtremaType.Low && !ShowSupportLevels) continue;

            SKColor labelColor = level.Type == ExtremaType.High
                ? new SKColor(239, 83, 80)
                : new SKColor(38, 166, 154);

            yield return new AxisLabelRequest(
                Value: level.Price,
                Color: labelColor,
                Label: level.Price.ToString("F2", CultureInfo.InvariantCulture),
                Style: AxisLabelStyle.Default
            );
        }
    }

    public override void Dispose()
    {
        _extremaHighPaint?.Dispose();
        _extremaLowPaint?.Dispose();
        base.Dispose();
    }
}
