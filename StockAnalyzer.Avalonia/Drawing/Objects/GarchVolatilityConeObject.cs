using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using SkiaSharp;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Objects;

/// <summary>
/// GARCH volatility cone: fits GARCH(1,1) to the closes between the two anchors and draws the 1 and 2 sigma price bands of the next <see cref="ForecastBars"/> bars around the last close of
/// the range (<see cref="GarchVolatilityConeValues"/> holds the calculation and the Data-tab rows). A range longer than the model limit is fitted on its newest bars and says so in a badge.
/// </summary>
public sealed class GarchVolatilityConeObject : RangeAnalysisObjectBase
{
    private const int InnerFillOpacityFactor = 2;
    private const float CenterDashOn = 2f;
    private const float EdgeDashOn = 4f;
    private const float EdgeDashOff = 3f;
    private const float CenterLineWidth = 1f;

    private int _forecastBars = GarchVolatilityConeValues.DefaultHorizon;
    private int _fillOpacity = DefaultFillOpacity;
    private global::Avalonia.Media.Color _fillColor = DrawingThemeContext.DefaultColor;

    private GarchVolatilityCone _cone = GarchVolatilityCone.Empty;
    private DateTime[] _times = Array.Empty<DateTime>();
    private string _overlayText = string.Empty;
    private IReadOnlyList<DrawingCalculatedValue> _cachedValues = Array.Empty<DrawingCalculatedValue>();
    private IndicatorColor _cachedValuesColor;
    private bool _hasCachedValues;

    private readonly SKPath _outerFillPath = new();
    private readonly SKPath _innerFillPath = new();
    private readonly SKPath _linePath = new();
    private readonly SKPaint _outerFillPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _innerFillPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPathEffect _edgeDash = SKPathEffect.CreateDash(new[] { EdgeDashOn, EdgeDashOff }, 0f);
    private readonly SKPathEffect _centerDash = SKPathEffect.CreateDash(new[] { CenterDashOn, EdgeDashOff }, 0f);
    private readonly SKPaint _edgePaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _centerPaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = CenterLineWidth };

    public const int DefaultFillOpacity = DrawingFillOpacity.DefaultPercent;

    public GarchVolatilityConeObject()
    {
        _edgePaint.PathEffect = _edgeDash;
        _centerPaint.PathEffect = _centerDash;
    }

    public GarchVolatilityConeObject(ChartPoint start, ChartPoint end)
        : this()
    {
        Points.Add(start);
        Points.Add(end);
    }

    public override ChartObjectType Type => ChartObjectType.GarchVolatilityCone;

    /// <summary>The price the log returns are taken from (the close by default); the cone is centred on this price of the last bar.</summary>
    [Category("Parameters")]
    [DisplayName("Price Source")]
    [Display(Order = 1)]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public PriceType PriceSource { get; set; } = PriceType.Close;

    /// <summary>Number of future bars of the cone.</summary>
    [Category("Parameters")]
    [DisplayName("Forecast Bars")]
    [Range(GarchVolatilityConeValues.MinHorizon, GarchVolatilityConeValues.MaxHorizon)]
    [Display(Order = 2)]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public int ForecastBars
    {
        get => _forecastBars;
        set => _forecastBars = Math.Clamp(value, GarchVolatilityConeValues.MinHorizon, GarchVolatilityConeValues.MaxHorizon);
    }

    /// <summary>Draws the 1 sigma band.</summary>
    [Category("Parameters")]
    [DisplayName("Show 1σ Band")]
    [Display(Order = 3)]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public bool ShowInnerBand { get; set; } = true;

    /// <summary>Draws the 2 sigma band.</summary>
    [Category("Parameters")]
    [DisplayName("Show 2σ Band")]
    [Display(Order = 4)]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public bool ShowOuterBand { get; set; } = true;

    /// <summary>Fill colour of the two bands (the default is the default drawing colour, the same as the line colour of a new drawing).</summary>
    [Category("Fill Style")]
    [DisplayName("Fill Color")]
    [Display(Order = 1)]
    [ParameterTag(DrawingParameterTags.Fill)]
    public global::Avalonia.Media.Color FillColor
    {
        get => _fillColor;
        set => _fillColor = DrawingFillOpacity.NormalizeFillColor(value);
    }

    /// <summary>Opacity (0-100 %) of the 2 sigma fill; the 1 sigma fill is twice as opaque (at most 100 %).</summary>
    [Category("Fill Style")]
    [DisplayName("Fill Opacity")]
    [Range(0, DrawingFillOpacity.FullPercent)]
    [Display(Order = 2)]
    [ParameterTag(DrawingParameterTags.Fill)]
    public int FillOpacity
    {
        get => _fillOpacity;
        set => _fillOpacity = Math.Clamp(value, 0, DrawingFillOpacity.FullPercent);
    }

    /// <summary>The cached cone (tests and the interaction code read it; it is not persisted).</summary>
    internal GarchVolatilityCone Cone
    {
        get
        {
            lock (SyncRoot)
            {
                return _cone;
            }
        }
    }

    protected override string? OverlayText => _overlayText;

    protected override void ComputeRange(IReadOnlyList<CoreCandleData> candles, int firstIndex, int lastIndex)
    {
        _cone = GarchVolatilityConeValues.Calculate(candles, firstIndex, lastIndex, _forecastBars, PriceSource);
        _hasCachedValues = false;
        if (_cone.IsOk)
        {
            TimeSpan span = DrawingRangeBars.MeanBarSpan(candles);
            var times = new DateTime[_cone.Horizon + 1];
            times[0] = candles[lastIndex].Timestamp;
            for (int h = 1; h <= _cone.Horizon; h++)
            {
                times[h] = DrawingRangeBars.FutureBarTime(candles, lastIndex, h, span);
            }

            _times = times;
            _overlayText = GarchVolatilityConeValues.TruncationBadge(_cone);
        }
        else
        {
            _times = Array.Empty<DateTime>();
            _overlayText = GarchVolatilityConeValues.StatusText(_cone.Status);
        }
    }

    protected override void ClearResult()
    {
        _cone = GarchVolatilityCone.Empty;
        _times = Array.Empty<DateTime>();
        _overlayText = string.Empty;
        _hasCachedValues = false;
    }

    protected override void RenderResult(SKCanvas canvas, ICoordinateTransform transform, float leftX, float rightX, SKColor color)
    {
        if (!_cone.IsOk || _times.Length != _cone.Horizon + 1)
        {
            return;
        }

        global::Avalonia.Point start = transform.ChartToScreen(new ChartPoint(_times[0], _cone.CenterPrice));
        if (!double.IsFinite(start.X) || !double.IsFinite(start.Y))
        {
            return;
        }

        if (ShowOuterBand)
        {
            _outerFillPaint.Color = DrawingFillOpacity.Resolve(FillColor, _fillOpacity);
            BuildBandPath(_outerFillPath, transform, start, _cone.Upper2, _cone.Lower2);
            canvas.DrawPath(_outerFillPath, _outerFillPaint);
        }

        if (ShowInnerBand)
        {
            _innerFillPaint.Color = DrawingFillOpacity.Resolve(FillColor, Math.Min(DrawingFillOpacity.FullPercent, _fillOpacity * InnerFillOpacityFactor));
            BuildBandPath(_innerFillPath, transform, start, _cone.Upper1, _cone.Lower1);
            canvas.DrawPath(_innerFillPath, _innerFillPaint);
        }

        _edgePaint.Color = color;
        _edgePaint.StrokeWidth = (float)Math.Max(1.0, Thickness * 0.75);
        if (ShowOuterBand)
        {
            DrawEdge(canvas, transform, start, _cone.Upper2);
            DrawEdge(canvas, transform, start, _cone.Lower2);
        }

        if (ShowInnerBand)
        {
            DrawEdge(canvas, transform, start, _cone.Upper1);
            DrawEdge(canvas, transform, start, _cone.Lower1);
        }

        // The centre: the last close, dotted to the end of the cone.
        global::Avalonia.Point end = transform.ChartToScreen(new ChartPoint(_times[_times.Length - 1], _cone.CenterPrice));
        if (double.IsFinite(end.X) && double.IsFinite(end.Y))
        {
            _centerPaint.Color = color;
            canvas.DrawLine((float)start.X, (float)start.Y, (float)end.X, (float)end.Y, _centerPaint);
        }
    }

    protected override IReadOnlyList<DrawingCalculatedValue> BuildValues(IndicatorColor color)
    {
        if (!_hasCachedValues || _cachedValuesColor != color)
        {
            _cachedValues = GarchVolatilityConeValues.Calculate(_cone, color);
            _cachedValuesColor = color;
            _hasCachedValues = true;
        }

        return _cachedValues;
    }

    protected override bool HitTestResult(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance)
    {
        if (!_cone.IsOk || _times.Length != _cone.Horizon + 1)
        {
            return false;
        }

        decimal?[]? upper = ShowOuterBand ? _cone.Upper2 : ShowInnerBand ? _cone.Upper1 : null;
        decimal?[]? lower = ShowOuterBand ? _cone.Lower2 : ShowInnerBand ? _cone.Lower1 : null;
        if (upper == null || lower == null)
        {
            return false;
        }

        // Walk the segments of the cone: the point is inside when it lies between the interpolated upper and lower edge of the segment it is above.
        global::Avalonia.Point previousTop = transform.ChartToScreen(new ChartPoint(_times[0], _cone.CenterPrice));
        global::Avalonia.Point previousBottom = previousTop;
        for (int h = 1; h <= _cone.Horizon; h++)
        {
            decimal? top = upper[h - 1];
            decimal? bottom = lower[h - 1];
            if (!top.HasValue || !bottom.HasValue)
            {
                return false;
            }

            global::Avalonia.Point nextTop = transform.ChartToScreen(new ChartPoint(_times[h], top.Value));
            global::Avalonia.Point nextBottom = transform.ChartToScreen(new ChartPoint(_times[h], bottom.Value));
            double xa = previousTop.X;
            double xb = nextTop.X;
            if (double.IsFinite(xa) && double.IsFinite(xb) && xb > xa && screenPoint.X >= xa && screenPoint.X <= xb)
            {
                double t = (screenPoint.X - xa) / (xb - xa);
                double yTop = previousTop.Y + t * (nextTop.Y - previousTop.Y);
                double yBottom = previousBottom.Y + t * (nextBottom.Y - previousBottom.Y);
                double low = Math.Min(yTop, yBottom) - tolerance;
                double high = Math.Max(yTop, yBottom) + tolerance;
                return screenPoint.Y >= low && screenPoint.Y <= high;
            }

            previousTop = nextTop;
            previousBottom = nextBottom;
        }

        return false;
    }

    protected override void DisposeNativeResources()
    {
        _outerFillPath.Dispose();
        _innerFillPath.Dispose();
        _linePath.Dispose();
        _outerFillPaint.Dispose();
        _innerFillPaint.Dispose();
        _edgePaint.Dispose();
        _centerPaint.Dispose();
        _edgeDash.Dispose();
        _centerDash.Dispose();
    }

    /// <summary>The number of leading steps whose upper and lower band are both usable.</summary>
    private static int LeadingValidSteps(decimal?[] upper, decimal?[] lower)
    {
        int count = Math.Min(upper.Length, lower.Length);
        int valid = 0;
        while (valid < count && upper[valid].HasValue && lower[valid].HasValue)
        {
            valid++;
        }

        return valid;
    }

    /// <summary>Builds the closed fan polygon of a band pair into the reused <paramref name="path"/>: out along the upper edge, back along the lower edge.</summary>
    private void BuildBandPath(SKPath path, ICoordinateTransform transform, global::Avalonia.Point start, decimal?[] upper, decimal?[] lower)
    {
        path.Rewind();
        int valid = LeadingValidSteps(upper, lower);
        if (valid == 0)
        {
            return;
        }

        path.MoveTo((float)start.X, (float)start.Y);
        for (int h = 1; h <= valid; h++)
        {
            global::Avalonia.Point point = transform.ChartToScreen(new ChartPoint(_times[h], upper[h - 1]!.Value));
            path.LineTo((float)point.X, (float)point.Y);
        }

        for (int h = valid; h >= 1; h--)
        {
            global::Avalonia.Point point = transform.ChartToScreen(new ChartPoint(_times[h], lower[h - 1]!.Value));
            path.LineTo((float)point.X, (float)point.Y);
        }

        path.Close();
    }

    /// <summary>Draws one edge (start point, then the band value of every step while usable) with the reused line path.</summary>
    private void DrawEdge(SKCanvas canvas, ICoordinateTransform transform, global::Avalonia.Point start, decimal?[] edge)
    {
        _linePath.Rewind();
        _linePath.MoveTo((float)start.X, (float)start.Y);
        int drawn = 0;
        for (int h = 1; h <= edge.Length; h++)
        {
            decimal? value = edge[h - 1];
            if (!value.HasValue)
            {
                break;
            }

            global::Avalonia.Point point = transform.ChartToScreen(new ChartPoint(_times[h], value.Value));
            _linePath.LineTo((float)point.X, (float)point.Y);
            drawn++;
        }

        if (drawn > 0)
        {
            canvas.DrawPath(_linePath, _edgePaint);
        }
    }
}
