using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Two-point vertical-range distribution drawing tool displaying a histogram, optional Gaussian KDE,
/// fitted continuous reference PDF, and empirical moment overlay (<c>N</c>, Skewness, Excess Kurtosis).
/// </summary>
public sealed class DistributionRangeObject : IChartObject, IDisposable, IDrawingCalculatedValuesProvider
{
    public const float DefaultPlotHeightRatio = 0.45f;
    public const double MinPlotHeightRatio = 0.15;
    public const double MaxPlotHeightRatio = 0.90;
    public const int MinOpacityPercent = 0;
    public const int MaxOpacityPercent = 100;
    public const int DefaultFillOpacityPercent = 10;
    public const double DefaultBarOpacity = 0.35;
    public const int DefaultBarOpacityPercent = 35;
    public const float PlotBottomPaddingPx = 4f;
    public const float PlotTopLabelMarginPx = 6f;
    public const float MinPlotHeightPx = 24f;

    public static readonly Color DefaultBarColor = Color.Parse("#90A4AE");
    public static readonly Color DefaultKdeColor = Color.Parse("#2196F3");
    public static readonly Color DefaultReferenceCurveColor = Color.Parse("#E69F00");

    private static readonly string[] IncompatibleModeLines =
    {
        "Horizontal Price Profile requires Data Mode = Price"
    };

    private readonly NativeResourceGate _gate = new();
    private readonly SKPaint _linePaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = false };
    private readonly SKPaint _backgroundPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _barFillPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = false };
    private readonly SKPaint _barStrokePaint = new() { Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = false };
    private readonly SKPaint _kdeCurvePaint = new() { Style = SKPaintStyle.Stroke, StrokeWidth = 1.75f, IsAntialias = true };
    private readonly SKPathEffect _refDashEffect = SKPathEffect.CreateDash(new[] { 6f, 4f }, 0f);
    private readonly SKPaint _refCurvePaint = new() { Style = SKPaintStyle.Stroke, StrokeWidth = 1.75f, IsAntialias = true };
    private readonly SKPaint _labelBgPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _labelBorderPaint = new() { Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };
    private readonly SKPaint _labelTextPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPath _kdePath = new();
    private readonly SKPath _refPath = new();

    private int _rangeBars;
    private int? _pendingRangeBars;
    private IReadOnlyList<CoreCandleData>? _lastCandles;
    private DistributionRangeResult _cachedResult = DistributionRangeResult.Empty;
    private IReadOnlyList<DrawingCalculatedValue> _cachedValues = Array.Empty<DrawingCalculatedValue>();
    private string[] _cachedLabelLines = Array.Empty<string>();
    private DrawingAnchor _cachedP1;
    private DrawingAnchor _cachedP2;
    private IndicatorColor _cachedColor;
    private DistributionRangeParameters? _cachedParameters;
    private bool _hasCalculated;

    public DistributionRangeObject()
    {
        _refCurvePaint.PathEffect = _refDashEffect;
    }

    public DistributionRangeObject(ChartPoint p1, ChartPoint p2)
        : this()
    {
        Points = new List<ChartPoint> { p1, p2 };
    }

    public Guid Id { get; set; } = Guid.NewGuid();
    [JsonIgnore]
    public ChartObjectType Type => ChartObjectType.DistributionRange;
    public string? CustomName { get; set; }
    public List<ChartPoint> Points { get; set; } = new();

    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public Color FillColor { get; set; } = DrawingThemeContext.DefaultColor;
    public int FillOpacity { get; set; } = DefaultFillOpacityPercent;
    public Color BarColor { get; set; } = DefaultBarColor;
    public double BarOpacity { get; set; } = DefaultBarOpacity;
    public Color KdeColor { get; set; } = DefaultKdeColor;
    public Color ReferenceCurveColor { get; set; } = DefaultReferenceCurveColor;

    public DistributionDataMode DataMode { get; set; } = DistributionDataMode.LogReturn;
    public PriceType PriceSource { get; set; } = PriceType.Close;
    public DistributionDisplayStyle DisplayStyle { get; set; } = DistributionDisplayStyle.HistogramWithCurve;
    public DistributionBinningRule BinningRule { get; set; } = DistributionBinningRule.FreedmanDiaconis;
    public int FixedBinCount { get; set; } = DistributionRangeParameters.DefaultFixedBinCount;
    public ReferenceDistributionType ReferenceDistribution { get; set; } = ReferenceDistributionType.Normal;
    public bool OverrideStudentTDegreesOfFreedom { get; set; }
    public decimal StudentTDegreesOfFreedom { get; set; } = DistributionRangeParameters.DefaultStudentTDegreesOfFreedom;
    public decimal GevShapeParameter { get; set; } = DistributionRangeParameters.DefaultGevShapeParameter;
    public double BandwidthMultiplier { get; set; } = DistributionRangeParameters.DefaultBandwidthMultiplier;
    public bool IncludeKde { get; set; } = true;
    public bool ShowStatsLabel { get; set; } = true;
    public double PlotHeightRatio { get; set; } = DefaultPlotHeightRatio;
    public bool RightToLeft { get; set; }
    public bool LockRange { get; set; }

    public int RangeBars
    {
        get => _rangeBars;
        set
        {
            if (_rangeBars != value)
            {
                _rangeBars = value;
                if (value > 0)
                {
                    _pendingRangeBars = value;
                }
            }
        }
    }

    [JsonIgnore]
    public SKColor SkiaColor => ToSkColor(Color);

    private static SKColor ToSkColor(Color c) => new(c.R, c.G, c.B, c.A);

    [JsonIgnore]
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; }
    public int PanelIndex { get; set; } = -1;
    public int ZIndex { get; set; }
    public int Priority { get; set; }
    public int AnchorPointIndex { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; }

    [JsonIgnore]
    public DistributionRangeResult CachedResult
    {
        get
        {
            lock (_gate.SyncRoot)
            {
                return _cachedResult;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate.SyncRoot)
        {
            if (!_gate.TryMarkDisposed())
            {
                return;
            }

            _linePaint.Dispose();
            _backgroundPaint.Dispose();
            _barFillPaint.Dispose();
            _barStrokePaint.Dispose();
            _kdeCurvePaint.Dispose();
            _refCurvePaint.Dispose();
            _refDashEffect.Dispose();
            _labelBgPaint.Dispose();
            _labelBorderPaint.Dispose();
            _labelTextPaint.Dispose();
            _kdePath.Dispose();
            _refPath.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    public void AdjustRightPointToBars(int targetBars, IReadOnlyList<CoreCandleData> candles)
    {
        if (Points.Count < 2 || candles == null || candles.Count == 0) return;

        if (targetBars < 1) targetBars = 1;

        int leftIndex = Points[0].Time <= Points[1].Time ? 0 : 1;
        int rightIndex = 1 - leftIndex;

        int leftBarIdx = FindNearestBarIndex(Points[leftIndex].Time, candles);
        if (leftBarIdx < 0) return;

        int targetRightBarIdx = Math.Clamp(leftBarIdx + targetBars - 1, 0, candles.Count - 1);
        DateTime targetTime = candles[targetRightBarIdx].Timestamp;

        Points[rightIndex] = new ChartPoint(targetTime, Points[rightIndex].Price);
    }

    public void Recalculate(IEnumerable<CoreCandleData>? candles)
    {
        lock (_gate.SyncRoot)
        {
            if (_gate.IsDisposed || Points.Count < 2 || candles == null)
            {
                return;
            }

            var candleList = candles as IReadOnlyList<CoreCandleData> ?? new List<CoreCandleData>(candles);
            _lastCandles = candleList;

            if (candleList.Count == 0)
            {
                _rangeBars = 0;
                _cachedResult = DistributionRangeResult.Empty;
                _cachedValues = Array.Empty<DrawingCalculatedValue>();
                _cachedLabelLines = Array.Empty<string>();
                _hasCalculated = false;
                return;
            }

            if (_pendingRangeBars.HasValue && _pendingRangeBars.Value > 0)
            {
                int targetBars = _pendingRangeBars.Value;
                _pendingRangeBars = null;
                AdjustRightPointToBars(targetBars, candleList);
            }

            RecomputeWithCandleListLocked(candleList);
        }
    }

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        lock (_gate.SyncRoot)
        {
            if (_gate.IsDisposed || !IsVisible || canvas == null || transform == null || Points.Count < 2)
            {
                return;
            }

            var clip = canvas.LocalClipBounds;
            var p1 = transform.ChartToScreen(Points[0]);
            var p2 = transform.ChartToScreen(Points[1]);

            float x1 = (float)p1.X;
            float x2 = (float)p2.X;
            float left = MathF.Min(x1, x2);
            float right = MathF.Max(x1, x2);
            float width = right - left;

            // 1. Range background band + boundary vertical lines
            if (width > 0f)
            {
                byte fillAlpha = (byte)Math.Clamp((int)(255 * (FillOpacity / 100.0)), 0, 255);
                _backgroundPaint.Color = new SKColor(FillColor.R, FillColor.G, FillColor.B, fillAlpha);
                canvas.DrawRect(new SKRect(left, clip.Top, right, clip.Bottom), _backgroundPaint);
            }

            byte lineAlpha = (byte)(IsSelected ? Color.A : 180);
            _linePaint.Color = new SKColor(Color.R, Color.G, Color.B, lineAlpha);
            _linePaint.StrokeWidth = (float)(Thickness + (IsSelected ? 1 : 0));
            canvas.DrawLine(x1, clip.Top, x1, clip.Bottom, _linePaint);
            canvas.DrawLine(x2, clip.Top, x2, clip.Bottom, _linePaint);

            _labelBgPaint.Color = DrawingThemeContext.ChartBackground;
            _labelBorderPaint.Color = _linePaint.Color;
            _labelTextPaint.Color = DrawingThemeContext.MainTextSkColor;
            _labelTextPaint.TextSize = DrawingThemeContext.DetailFontSize > 0f
                ? DrawingThemeContext.DetailFontSize
                : DrawingThemeContext.DrawingFontSize;
            _labelTextPaint.Typeface = DrawingThemeContext.DefaultTextTypeface;

            float centerX = (left + right) * 0.5f;

            // D-3 = A: HorizontalPriceProfile requires DataMode == Price (no unit-mismatched price-axis plot)
            if (DisplayStyle == DistributionDisplayStyle.HorizontalPriceProfile && DataMode != DistributionDataMode.Price)
            {
                DrawingLabelBoxRenderer.DrawCenteredBox(
                    canvas,
                    centerX,
                    clip.Top + PlotTopLabelMarginPx,
                    placeAbove: false,
                    IncompatibleModeLines,
                    _labelBgPaint,
                    _labelBorderPaint,
                    _labelTextPaint);

                if (IsSelected)
                {
                    DrawHandles(canvas, p1, p2);
                }
                return;
            }

            float labelBottomY = clip.Top + PlotTopLabelMarginPx;
            if (ShowStatsLabel && _cachedLabelLines.Length > 0)
            {
                SKRect labelRect = DrawingLabelBoxRenderer.DrawCenteredBox(
                    canvas,
                    centerX,
                    clip.Top + PlotTopLabelMarginPx,
                    placeAbove: false,
                    _cachedLabelLines,
                    _labelBgPaint,
                    _labelBorderPaint,
                    _labelTextPaint);
                if (!labelRect.IsEmpty)
                {
                    labelBottomY = labelRect.Bottom;
                }
            }

            DistributionRangeResult res = _cachedResult;
            if (width >= 2f && res.Bins.Count > 0 && res.MinValue.HasValue && res.MaxValue.HasValue && res.MaxValue.Value > res.MinValue.Value)
            {
                byte barAlpha = (byte)Math.Clamp((int)(255.0 * Math.Clamp(BarOpacity, 0.0, 1.0)), 0, 255);
                _barFillPaint.Color = new SKColor(BarColor.R, BarColor.G, BarColor.B, barAlpha);
                _barStrokePaint.Color = new SKColor(BarColor.R, BarColor.G, BarColor.B, (byte)Math.Min(255, barAlpha + 45));
                _kdeCurvePaint.Color = ToSkColor(KdeColor);
                _refCurvePaint.Color = ToSkColor(ReferenceCurveColor);

                if (DisplayStyle == DistributionDisplayStyle.HorizontalPriceProfile)
                {
                    float profileClipTop = (ShowStatsLabel && _cachedLabelLines.Length > 0)
                        ? MathF.Max(clip.Top, labelBottomY + PlotTopLabelMarginPx)
                        : clip.Top;
                    RenderHorizontalPriceProfile(canvas, transform, res, clip, profileClipTop, left, right, width);
                }
                else
                {
                    // D-5 = A: Narrow-viewport guard preventing histogram/curve overlap with label box
                    float plotBottom = clip.Bottom - PlotBottomPaddingPx;
                    float plotMinTop = labelBottomY + PlotTopLabelMarginPx;
                    float desiredHeight = clip.Height * (float)Math.Clamp(PlotHeightRatio, MinPlotHeightRatio, MaxPlotHeightRatio);
                    float plotTop = MathF.Max(plotMinTop, plotBottom - desiredHeight);
                    float availHeight = plotBottom - plotTop;

                    if (availHeight >= MinPlotHeightPx)
                    {
                        RenderVerticalDistribution(canvas, res, left, right, width, plotTop, plotBottom, availHeight);
                    }
                }
            }

            if (IsSelected)
            {
                DrawHandles(canvas, p1, p2);
            }
        }
    }

    private void RenderVerticalDistribution(
        SKCanvas canvas,
        DistributionRangeResult res,
        float left,
        float right,
        float width,
        float plotTop,
        float plotBottom,
        float availHeight)
    {
        bool drawCurves = DisplayStyle == DistributionDisplayStyle.HistogramWithCurve;
        double maxDensity = FindMaxDensity(res, drawCurves && IncludeKde, drawCurves);
        if (maxDensity <= 0.0 || !double.IsFinite(maxDensity))
        {
            return;
        }

        double minVal = (double)res.MinValue!.Value;
        double spanVal = (double)res.MaxValue!.Value - minVal;
        if (spanVal <= 0.0) return;

        canvas.Save();
        canvas.ClipRect(new SKRect(left, plotTop, right, plotBottom));

        for (int i = 0; i < res.Bins.Count; i++)
        {
            DistributionBin bin = res.Bins[i];
            float bx1 = left + (float)(((double)bin.LowerBound - minVal) / spanVal) * width;
            float bx2 = left + (float)(((double)bin.UpperBound - minVal) / spanVal) * width;
            float barH = (float)((double)bin.Density / maxDensity) * availHeight;
            float top = MathF.Max(plotTop, plotBottom - barH);
            if (bx2 > bx1 && barH > 0f)
            {
                var rect = new SKRect(bx1, top, bx2, plotBottom);
                canvas.DrawRect(rect, _barFillPaint);
                canvas.DrawRect(rect, _barStrokePaint);
            }
        }

        if (drawCurves)
        {
            if (IncludeKde && res.KdeCurve.Count > 1)
            {
                BuildVerticalCurvePath(_kdePath, res.KdeCurve, minVal, spanVal, maxDensity, left, width, plotTop, plotBottom, availHeight);
                canvas.DrawPath(_kdePath, _kdeCurvePaint);
            }

            if (res.ReferenceStatus == ReferenceFitStatus.Fitted && res.ReferenceCurve.Count > 1)
            {
                BuildVerticalCurvePath(_refPath, res.ReferenceCurve, minVal, spanVal, maxDensity, left, width, plotTop, plotBottom, availHeight);
                canvas.DrawPath(_refPath, _refCurvePaint);
            }
        }

        canvas.Restore();
    }

    private void RenderHorizontalPriceProfile(
        SKCanvas canvas,
        ICoordinateTransform transform,
        DistributionRangeResult res,
        SKRect clip,
        float profileClipTop,
        float left,
        float right,
        float width)
    {
        if (clip.Bottom <= profileClipTop)
        {
            return;
        }

        bool drawRef = res.ReferenceStatus == ReferenceFitStatus.Fitted && res.ReferenceCurve.Count > 1;
        double maxDensity = FindMaxDensity(res, includeKde: false, includeReference: drawRef);
        if (maxDensity <= 0.0 || !double.IsFinite(maxDensity))
        {
            return;
        }

        canvas.Save();
        canvas.ClipRect(new SKRect(left, profileClipTop, right, clip.Bottom));

        for (int i = 0; i < res.Bins.Count; i++)
        {
            DistributionBin bin = res.Bins[i];
            float y1 = (float)transform.GetYFromPrice(bin.UpperBound);
            float y2 = (float)transform.GetYFromPrice(bin.LowerBound);
            float barW = MathF.Min(width, (float)((double)bin.Density / maxDensity) * width);
            if (barW <= 0f) continue;

            float top = MathF.Min(y1, y2);
            float bottom = MathF.Max(y1, y2);
            SKRect rect = RightToLeft
                ? new SKRect(right - barW, top, right, bottom)
                : new SKRect(left, top, left + barW, bottom);

            canvas.DrawRect(rect, _barFillPaint);
            canvas.DrawRect(rect, _barStrokePaint);
        }

        if (drawRef)
        {
            _refPath.Reset();
            for (int i = 0; i < res.ReferenceCurve.Count; i++)
            {
                DistributionCurvePoint pt = res.ReferenceCurve[i];
                float pxLen = MathF.Min(width, (float)((double)pt.Density / maxDensity) * width);
                float px = RightToLeft ? (right - pxLen) : (left + pxLen);
                float py = (float)transform.GetYFromPrice(pt.X);
                if (i == 0) _refPath.MoveTo(px, py);
                else _refPath.LineTo(px, py);
            }
            canvas.DrawPath(_refPath, _refCurvePaint);
        }

        canvas.Restore();
    }

    private static double FindMaxDensity(DistributionRangeResult res, bool includeKde, bool includeReference)
    {
        double maxDensity = 0.0;
        for (int i = 0; i < res.Bins.Count; i++)
        {
            double d = (double)res.Bins[i].Density;
            if (d > maxDensity) maxDensity = d;
        }

        if (includeKde)
        {
            for (int i = 0; i < res.KdeCurve.Count; i++)
            {
                double d = (double)res.KdeCurve[i].Density;
                if (d > maxDensity) maxDensity = d;
            }
        }

        if (includeReference && res.ReferenceStatus == ReferenceFitStatus.Fitted)
        {
            for (int i = 0; i < res.ReferenceCurve.Count; i++)
            {
                double d = (double)res.ReferenceCurve[i].Density;
                if (d > maxDensity) maxDensity = d;
            }
        }

        return maxDensity;
    }

    private static void BuildVerticalCurvePath(
        SKPath path,
        IReadOnlyList<DistributionCurvePoint> points,
        double minVal,
        double spanVal,
        double maxDensity,
        float left,
        float width,
        float plotTop,
        float plotBottom,
        float availHeight)
    {
        path.Reset();
        for (int i = 0; i < points.Count; i++)
        {
            DistributionCurvePoint pt = points[i];
            float px = left + (float)(((double)pt.X - minVal) / spanVal) * width;
            float py = MathF.Max(plotTop, plotBottom - (float)((double)pt.Density / maxDensity) * availHeight);
            if (i == 0) path.MoveTo(px, py);
            else path.LineTo(px, py);
        }
    }

    private void DrawHandles(SKCanvas canvas, global::Avalonia.Point p1, global::Avalonia.Point p2)
    {
        SelectionHandleRenderer.Draw(
            canvas,
            p1,
            AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null,
            radius: ChartConstants.SelectedHandleRadius);
        SelectionHandleRenderer.Draw(
            canvas,
            p2,
            AnchorPointIndex == 1 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null,
            radius: ChartConstants.SelectedHandleRadius);
    }

    public bool HitTest(
        global::Avalonia.Point screenPoint,
        ICoordinateTransform transform,
        double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (_gate.IsDisposed || !IsVisible || transform == null || Points.Count < 2)
        {
            return false;
        }

        double x1 = transform.ChartToScreen(Points[0]).X;
        double x2 = transform.ChartToScreen(Points[1]).X;
        double minX = Math.Min(x1, x2);
        double maxX = Math.Max(x1, x2);

        if (screenPoint.X < minX - tolerance || screenPoint.X > maxX + tolerance)
        {
            return false;
        }

        if (transform.ScreenRect.Height > 0)
        {
            return screenPoint.Y >= transform.ScreenRect.Top - tolerance
                && screenPoint.Y <= transform.ScreenRect.Bottom + tolerance;
        }

        return true;
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price);
        }
    }

    public void TranslateBars(int deltaBars, IReadOnlyList<CoreCandleData> candles)
    {
        if (Points.Count < 2 || candles == null || candles.Count == 0 || deltaBars == 0) return;

        int idx0 = FindNearestBarIndex(Points[0].Time, candles);
        int idx1 = FindNearestBarIndex(Points[1].Time, candles);
        if (idx0 < 0 || idx1 < 0) return;

        int minIdx = Math.Min(idx0, idx1);
        int maxIdx = Math.Max(idx0, idx1);

        int clampedDelta = Math.Clamp(deltaBars, -minIdx, candles.Count - 1 - maxIdx);
        if (clampedDelta == 0) return;

        Points[0] = new ChartPoint(candles[idx0 + clampedDelta].Timestamp, Points[0].Price);
        Points[1] = new ChartPoint(candles[idx1 + clampedDelta].Timestamp, Points[1].Price);
    }

    public static int FindNearestBarIndex(DateTime time, IReadOnlyList<CoreCandleData> candles)
    {
        if (candles == null || candles.Count == 0) return -1;
        int low = 0;
        int high = candles.Count - 1;
        while (low <= high)
        {
            int mid = low + ((high - low) >> 1);
            int cmp = candles[mid].Timestamp.CompareTo(time);
            if (cmp == 0) return mid;
            if (cmp < 0) low = mid + 1;
            else high = mid - 1;
        }
        if (low >= candles.Count) return candles.Count - 1;
        if (high < 0) return 0;
        long diffLow = Math.Abs((candles[low].Timestamp - time).Ticks);
        long diffHigh = Math.Abs((candles[high].Timestamp - time).Ticks);
        return diffHigh <= diffLow ? high : low;
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        lock (_gate.SyncRoot)
        {
            if (_gate.IsDisposed || Points.Count < 2 || _lastCandles == null)
            {
                return Array.Empty<DrawingCalculatedValue>();
            }

            DrawingAnchor p1 = Points[0].ToAnchor();
            DrawingAnchor p2 = Points[1].ToAnchor();
            var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);
            DistributionRangeParameters parameters = BuildParameters();

            if (!_hasCalculated
                || p1 != _cachedP1
                || p2 != _cachedP2
                || color != _cachedColor
                || !Equals(parameters, _cachedParameters))
            {
                RecomputeWithCandleListLocked(_lastCandles);
            }

            return _cachedValues;
        }
    }

    private void RecomputeWithCandleListLocked(IReadOnlyList<CoreCandleData> candleList)
    {
        DistributionRangeParameters parameters = BuildParameters();
        var indColor = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);
        DrawingAnchor p1 = Points[0].ToAnchor();
        DrawingAnchor p2 = Points[1].ToAnchor();

        _rangeBars = CountBarsInRange(candleList, p1.Time, p2.Time);
        DistributionRangeResult result = DistributionRangeCalculator.Calculate(candleList, p1.Time, p2.Time, parameters);

        _cachedResult = result;
        _cachedParameters = parameters;
        _cachedP1 = p1;
        _cachedP2 = p2;
        _cachedColor = indColor;
        _hasCalculated = true;
        _cachedValues = DistributionRangeValues.FromResult(result, parameters, indColor);
        _cachedLabelLines = BuildLabelLines(result, parameters);
    }

    private DistributionRangeParameters BuildParameters() => new DistributionRangeParameters
    {
        DataMode = DataMode,
        PriceSource = PriceSource,
        BinningRule = BinningRule,
        FixedBinCount = FixedBinCount,
        ReferenceDistribution = ReferenceDistribution,
        OverrideStudentTDegreesOfFreedom = OverrideStudentTDegreesOfFreedom,
        StudentTDegreesOfFreedom = StudentTDegreesOfFreedom,
        GevShapeParameter = GevShapeParameter,
        BandwidthMultiplier = BandwidthMultiplier,
        IncludeKde = IncludeKde
    }.Normalize();

    private static int CountBarsInRange(IReadOnlyList<CoreCandleData> candles, DateTime t1, DateTime t2)
    {
        if (candles.Count == 0) return 0;
        DateTime start = t1 <= t2 ? t1 : t2;
        DateTime end = t1 <= t2 ? t2 : t1;

        int lo = 0;
        int hi = candles.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (candles[mid].Timestamp < start) lo = mid + 1;
            else hi = mid;
        }
        int startIdx = lo;

        lo = 0;
        hi = candles.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (candles[mid].Timestamp <= end) lo = mid + 1;
            else hi = mid;
        }
        int endIdx = lo - 1;

        return endIdx >= startIdx ? (endIdx - startIdx + 1) : 0;
    }

    private static string[] BuildLabelLines(DistributionRangeResult result, DistributionRangeParameters parameters)
    {
        var culture = DrawingValueFormat.Culture;
        string modeText = parameters.DataMode switch
        {
            DistributionDataMode.SimpleReturn => "Simple Return",
            DistributionDataMode.Price => $"Price ({parameters.PriceSource})",
            _ => "Log Return"
        };

        string header = result.IsLowSampleSize
            ? $"{modeText} | N = {result.SampleCount.ToString(culture)} (Low N)"
            : $"{modeText} | N = {result.SampleCount.ToString(culture)}";

        string scaleFmt = parameters.DataMode == DistributionDataMode.Price ? "F3" : "F6";
        string meanText = result.Mean.HasValue ? result.Mean.Value.ToString(scaleFmt, culture) : "-";
        string sdText = result.StdDev.HasValue ? result.StdDev.Value.ToString(scaleFmt, culture) : "-";
        string stats1 = $"Mean: {meanText}  SD: {sdText}";

        string skewKurt = (result.SampleCount >= 4 && result.Skewness.HasValue && result.ExcessKurtosis.HasValue)
            ? $"Skew: {result.Skewness.Value.ToString("F3", culture)}  Kurt: {result.ExcessKurtosis.Value.ToString("F3", culture)}"
            : "Skew/Kurt: N/A";

        string refText = result.ReferenceStatus == ReferenceFitStatus.None || parameters.ReferenceDistribution == ReferenceDistributionType.None
            ? "Ref: None"
            : $"Ref: {result.ReferenceParameterSummary}";

        return new[] { header, stats1, skewKurt, refText };
    }
}
