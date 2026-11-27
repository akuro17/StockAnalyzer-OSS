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
/// Robust (Theil-Sen / Siegel repeated-median) regression line of the closes between the two anchors with a band of <see cref="StdDevMultiplier"/> robust sigmas. The kernel takes at most
/// <see cref="RobustRegressionTrendValues.MaxBars"/> bars: the anchors stay where the user put them, only the newest bars of a longer range are fitted and drawn, and the badge says so.
/// </summary>
public sealed class RobustRegressionTrendObject : RangeCurveObjectBase, IPeriodSnappedRange
{
    /// <summary>
    /// The fill opacity (percent) a new tool starts with (15 %, alpha 38). The sibling range curve tools keep the constant band alpha 28 of <c>RangeCurveObjectBase</c>: only this tool overrides
    /// <see cref="BandFillColor"/>.
    /// </summary>
    public const int DefaultFillOpacity = DrawingFillOpacity.DefaultPercent;

    private decimal _stdDevMultiplier = RegressionTrendValues.DefaultStdDevMultiplier;
    private int _period = RobustRegressionTrendValues.DefaultPeriod;
    private int _fillOpacity = DefaultFillOpacity;
    private global::Avalonia.Media.Color _fillColor = DrawingThemeContext.DefaultColor;

    private RangeLineFit _fit = RangeLineFit.Empty;
    private IReadOnlyList<DrawingCalculatedValue> _cachedValues = Array.Empty<DrawingCalculatedValue>();
    private IndicatorColor _cachedValuesColor;
    private bool _hasCachedValues;

    public RobustRegressionTrendObject()
    {
    }

    public RobustRegressionTrendObject(ChartPoint start, ChartPoint end)
    {
        Points.Add(start);
        Points.Add(end);
    }

    public override ChartObjectType Type => ChartObjectType.RobustRegressionTrend;

    /// <summary>The price that is fitted (the close by default).</summary>
    [Category("Parameters")]
    [DisplayName("Price Source")]
    [Display(Order = 1)]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public PriceType PriceSource { get; set; } = PriceType.Close;

    /// <summary>
    /// The number of bars fitted (the newest bars of the range when it is longer). A small period gives a very narrow band (the residual scatter of two or three bars is small).
    /// </summary>
    [Category("Parameters")]
    [DisplayName("Period")]
    [Range(RobustRegressionTrendValues.MinBars, RobustRegressionTrendValues.MaxBars)]
    [Display(Order = 2)]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public int Period
    {
        get => _period;
        set => _period = Math.Clamp(value, RobustRegressionTrendValues.MinBars, RobustRegressionTrendValues.MaxBars);
    }

    /// <summary>Band half width in robust residual sigmas.</summary>
    [Category("Parameters")]
    [DisplayName("StdDev Multiplier")]
    [Range(RegressionTrendValues.MinStdDevMultiplierRange, RegressionTrendValues.MaxStdDevMultiplierRange)]
    [Display(Order = 3)]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public decimal StdDevMultiplier
    {
        get => _stdDevMultiplier;
        set => _stdDevMultiplier = Math.Clamp(value, (decimal)RegressionTrendValues.MinStdDevMultiplierRange, (decimal)RegressionTrendValues.MaxStdDevMultiplierRange);
    }

    /// <summary>Siegel repeated-median slope instead of the Theil-Sen median of pairwise slopes.</summary>
    [Category("Parameters")]
    [DisplayName("Use Siegel Median")]
    [Display(Order = 4)]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public bool UseSiegelMedian { get; set; }

    /// <summary>Continue the line and band over the existing bars after the range end.</summary>
    [Category("Parameters")]
    [DisplayName("Extend Right")]
    [Display(Order = 5)]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public bool ExtendRight { get; set; }

    /// <summary>Fill colour of the band (the default is the default drawing colour, the same as the line colour of a new drawing).</summary>
    [Category("Fill Style")]
    [DisplayName("Fill Color")]
    [Display(Order = 1)]
    [ParameterTag(DrawingParameterTags.Fill)]
    public global::Avalonia.Media.Color FillColor
    {
        get => _fillColor;
        set => _fillColor = DrawingFillOpacity.NormalizeFillColor(value);
    }

    /// <summary>Opacity (0-100 %) of the band fill (default 15 %).</summary>
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

    /// <summary>The cached fit (tests read it; it is not persisted).</summary>
    internal RangeLineFit Fit
    {
        get
        {
            lock (SyncRoot)
            {
                return _fit;
            }
        }
    }

    protected override void ComputeRange(IReadOnlyList<CoreCandleData> candles, int firstIndex, int lastIndex)
    {
        _fit = RobustRegressionTrendValues.Calculate(candles, firstIndex, lastIndex, UseSiegelMedian, _stdDevMultiplier, ExtendRight, _period, PriceSource);
        _hasCachedValues = false;
        SetCurve(
            _fit.IsOk ? _fit.Curve : RangeCurve.Empty,
            _fit.IsOk ? RangeFitText.TruncationBadge(_fit.RangeBars, _fit.EffectiveBars) : RobustRegressionTrendValues.StatusText(_fit));
    }

    /// <summary>
    /// Puts the range on exactly <see cref="Period"/> bars after an edit the user has confirmed (see <see cref="DrawingRangeSnap"/>): the earlier anchor becomes the start bar, the later one the end bar. A later-handle
    /// release writes the width back to <see cref="Period"/>. The anchor prices are kept. Never called from a recalculation, a load or a data refresh.
    /// </summary>
    public bool SnapToPeriod(IReadOnlyList<CoreCandleData> candles, RangeSnapKind kind, int draggedPointIndex = -1)
    {
        if (Points.Count < 2 ||
            !DrawingRangeSnap.TryCompute(candles, Points[0].Time, Points[1].Time, _period, RobustRegressionTrendValues.MinBars, RobustRegressionTrendValues.MaxBars, kind, draggedPointIndex, out RangeSnapResult snapped))
        {
            return false;
        }

        int earlier = Points[0].Time <= Points[1].Time ? 0 : 1;
        int later = 1 - earlier;
        ChartPoint start = new(snapped.StartTime, Points[earlier].Price);
        ChartPoint end = new(snapped.EndTime, Points[later].Price);
        bool changed = Points[earlier].Time != start.Time || Points[later].Time != end.Time || _period != snapped.Period;
        Points[earlier] = start;
        Points[later] = end;
        _period = snapped.Period;
        return changed;
    }

    protected override SKColor BandFillColor(SKColor lineColor) => DrawingFillOpacity.Resolve(FillColor, _fillOpacity);

    protected override void ClearResult()
    {
        _fit = RangeLineFit.Empty;
        _hasCachedValues = false;
        base.ClearResult();
    }

    protected override IReadOnlyList<DrawingCalculatedValue> BuildValues(IndicatorColor color)
    {
        if (!_hasCachedValues || _cachedValuesColor != color)
        {
            _cachedValues = RobustRegressionTrendValues.Calculate(_fit, color);
            _cachedValuesColor = color;
            _hasCachedValues = true;
        }

        return _cachedValues;
    }
}

/// <summary>
/// Weighted least-squares regression line of the closes between the two anchors (<see cref="WeightingMode"/>: volume or exponential decay) with a band of <see cref="StdDevMultiplier"/> weighted
/// sigmas. A range longer than the kernel limit is fitted on its newest bars.
/// </summary>
public sealed class WeightedRegressionTrendObject : RangeCurveObjectBase
{
    private decimal _stdDevMultiplier = RegressionTrendValues.DefaultStdDevMultiplier;
    private double _decayFactor = WeightedRegressionTrendValues.DefaultDecayFactor;

    private RangeLineFit _fit = RangeLineFit.Empty;
    private IReadOnlyList<DrawingCalculatedValue> _cachedValues = Array.Empty<DrawingCalculatedValue>();
    private IndicatorColor _cachedValuesColor;
    private bool _hasCachedValues;

    public WeightedRegressionTrendObject()
    {
    }

    public WeightedRegressionTrendObject(ChartPoint start, ChartPoint end)
    {
        Points.Add(start);
        Points.Add(end);
    }

    public override ChartObjectType Type => ChartObjectType.WeightedRegressionTrend;

    /// <summary>How the bars are weighted.</summary>
    [Category("Parameters")]
    [DisplayName("Weighting Mode")]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public WeightedRegressionMode WeightingMode { get; set; } = WeightedRegressionMode.VolumeWeighted;

    /// <summary>The decay factor lambda of the exponential weighting (the weight of a bar is lambda^(bars before the newest)).</summary>
    [Category("Parameters")]
    [DisplayName("Decay Factor")]
    [Range(WeightedRegressionTrendValues.MinDecayFactor, WeightedRegressionTrendValues.MaxDecayFactor)]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public double DecayFactor
    {
        get => _decayFactor;
        set => _decayFactor = Math.Clamp(value, WeightedRegressionTrendValues.MinDecayFactor, WeightedRegressionTrendValues.MaxDecayFactor);
    }

    /// <summary>Band half width in weighted residual sigmas.</summary>
    [Category("Parameters")]
    [DisplayName("StdDev Multiplier")]
    [Range(RegressionTrendValues.MinStdDevMultiplierRange, RegressionTrendValues.MaxStdDevMultiplierRange)]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public decimal StdDevMultiplier
    {
        get => _stdDevMultiplier;
        set => _stdDevMultiplier = Math.Clamp(value, (decimal)RegressionTrendValues.MinStdDevMultiplierRange, (decimal)RegressionTrendValues.MaxStdDevMultiplierRange);
    }

    /// <summary>Continue the line and band over the existing bars after the range end.</summary>
    [Category("Parameters")]
    [DisplayName("Extend Right")]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public bool ExtendRight { get; set; }

    /// <summary>The cached fit (tests read it; it is not persisted).</summary>
    internal RangeLineFit Fit
    {
        get
        {
            lock (SyncRoot)
            {
                return _fit;
            }
        }
    }

    protected override void ComputeRange(IReadOnlyList<CoreCandleData> candles, int firstIndex, int lastIndex)
    {
        _fit = WeightedRegressionTrendValues.Calculate(candles, firstIndex, lastIndex, WeightingMode, _decayFactor, _stdDevMultiplier, ExtendRight);
        _hasCachedValues = false;
        SetCurve(
            _fit.IsOk ? _fit.Curve : RangeCurve.Empty,
            _fit.IsOk ? RangeFitText.TruncationBadge(_fit.RangeBars, _fit.EffectiveBars) : WeightedRegressionTrendValues.StatusText(_fit));
    }

    protected override void ClearResult()
    {
        _fit = RangeLineFit.Empty;
        _hasCachedValues = false;
        base.ClearResult();
    }

    protected override IReadOnlyList<DrawingCalculatedValue> BuildValues(IndicatorColor color)
    {
        if (!_hasCachedValues || _cachedValuesColor != color)
        {
            _cachedValues = WeightedRegressionTrendValues.Calculate(_fit, color);
            _cachedValuesColor = color;
            _hasCachedValues = true;
        }

        return _cachedValues;
    }
}
