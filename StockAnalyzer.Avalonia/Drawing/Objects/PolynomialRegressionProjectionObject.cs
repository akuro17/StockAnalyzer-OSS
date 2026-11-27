using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Objects;

/// <summary>
/// Polynomial regression of the closes between the two anchors: a degree-<see cref="Degree"/> least-squares curve with a band of <see cref="StdDevMultiplier"/> residual standard deviations,
/// optionally projected <see cref="ForecastBars"/> bars beyond the range end (dashed). <see cref="PolynomialRegressionProjectionValues"/> holds the calculation and the Data-tab rows.
/// </summary>
public sealed class PolynomialRegressionProjectionObject : RangeCurveObjectBase
{
    private int _degree = PolynomialRegressionProjectionValues.DefaultDegree;
    private int _forecastBars = PolynomialRegressionProjectionValues.DefaultForecastBars;
    private decimal _stdDevMultiplier = RegressionTrendValues.DefaultStdDevMultiplier;

    private PolynomialRegressionFit _fit = PolynomialRegressionFit.Empty;
    private IReadOnlyList<DrawingCalculatedValue> _cachedValues = Array.Empty<DrawingCalculatedValue>();
    private IndicatorColor _cachedValuesColor;
    private bool _hasCachedValues;

    public PolynomialRegressionProjectionObject()
    {
    }

    public PolynomialRegressionProjectionObject(ChartPoint start, ChartPoint end)
    {
        Points.Add(start);
        Points.Add(end);
    }

    public override ChartObjectType Type => ChartObjectType.PolynomialRegressionProjection;

    /// <summary>Degree of the polynomial (needs at least degree + 2 bars in the range).</summary>
    [Category("Parameters")]
    [DisplayName("Degree")]
    [Range(PolynomialRegressionProjectionValues.MinDegree, PolynomialRegressionProjectionValues.MaxDegree)]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public int Degree
    {
        get => _degree;
        set => _degree = Math.Clamp(value, PolynomialRegressionProjectionValues.MinDegree, PolynomialRegressionProjectionValues.MaxDegree);
    }

    /// <summary>Number of bars the curve is projected beyond the range end (0 = no projection).</summary>
    [Category("Parameters")]
    [DisplayName("Forecast Bars")]
    [Range(PolynomialRegressionProjectionValues.MinForecastBars, PolynomialRegressionProjectionValues.MaxForecastBars)]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public int ForecastBars
    {
        get => _forecastBars;
        set => _forecastBars = Math.Clamp(value, PolynomialRegressionProjectionValues.MinForecastBars, PolynomialRegressionProjectionValues.MaxForecastBars);
    }

    /// <summary>Band half width in residual standard deviations.</summary>
    [Category("Parameters")]
    [DisplayName("StdDev Multiplier")]
    [Range(RegressionTrendValues.MinStdDevMultiplierRange, RegressionTrendValues.MaxStdDevMultiplierRange)]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public decimal StdDevMultiplier
    {
        get => _stdDevMultiplier;
        set => _stdDevMultiplier = Math.Clamp(value, (decimal)RegressionTrendValues.MinStdDevMultiplierRange, (decimal)RegressionTrendValues.MaxStdDevMultiplierRange);
    }

    /// <summary>Draws the band around the curve.</summary>
    [Category("Parameters")]
    [DisplayName("Show Band")]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public bool ShowBand { get; set; } = true;

    /// <summary>The cached fit (tests read it; it is not persisted).</summary>
    internal PolynomialRegressionFit Fit
    {
        get
        {
            lock (SyncRoot)
            {
                return _fit;
            }
        }
    }

    protected override bool IsBandVisible => ShowBand;

    protected override void ComputeRange(IReadOnlyList<CoreCandleData> candles, int firstIndex, int lastIndex)
    {
        _fit = PolynomialRegressionProjectionValues.Calculate(candles, firstIndex, lastIndex, _degree, _forecastBars, _stdDevMultiplier);
        _hasCachedValues = false;
        SetCurve(
            _fit.IsOk ? _fit.Curve : RangeCurve.Empty,
            _fit.IsOk ? RangeFitText.TruncationBadge(_fit.RangeBars, _fit.EffectiveBars) : PolynomialRegressionProjectionValues.StatusText(_fit));
    }

    protected override void ClearResult()
    {
        _fit = PolynomialRegressionFit.Empty;
        _hasCachedValues = false;
        base.ClearResult();
    }

    protected override IReadOnlyList<DrawingCalculatedValue> BuildValues(IndicatorColor color)
    {
        if (!_hasCachedValues || _cachedValuesColor != color)
        {
            _cachedValues = PolynomialRegressionProjectionValues.Calculate(_fit, color);
            _cachedValuesColor = color;
            _hasCachedValues = true;
        }

        return _cachedValues;
    }
}
