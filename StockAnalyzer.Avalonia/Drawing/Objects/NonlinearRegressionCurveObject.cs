using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Avalonia.Drawing.Objects;

/// <summary>
/// Nonlinear regression of the closes between the two anchors (<see cref="ModelType"/>: Exponential, Logarithmic, Power or Logistic, the same models as the Nonlinear Regression indicator), drawn as a
/// curve through the bars of the range and, dashed, <see cref="ExtendBars"/> bars beyond its end. <see cref="NonlinearRegressionCurveValues"/> holds the calculation and the Data-tab rows.
/// </summary>
public sealed class NonlinearRegressionCurveObject : RangeCurveObjectBase
{
    private NonlinearRegressionModel _modelType = NonlinearRegressionModel.Exponential;
    private int _extendBars = NonlinearRegressionCurveValues.DefaultExtendBars;

    private NonlinearRegressionFit _fit = NonlinearRegressionFit.Empty;
    private IReadOnlyList<DrawingCalculatedValue> _cachedValues = Array.Empty<DrawingCalculatedValue>();
    private IndicatorColor _cachedValuesColor;
    private bool _hasCachedValues;

    public NonlinearRegressionCurveObject()
    {
    }

    public NonlinearRegressionCurveObject(ChartPoint start, ChartPoint end)
    {
        Points.Add(start);
        Points.Add(end);
    }

    public override ChartObjectType Type => ChartObjectType.NonlinearRegressionCurve;

    /// <summary>The curve family. Exponential and Power need every close above zero; Logistic needs a range that is not flat.</summary>
    [Category("Parameters")]
    [DisplayName("Model Type")]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public NonlinearRegressionModel ModelType
    {
        get => _modelType;
        set => _modelType = value;
    }

    /// <summary>Number of bars the curve is extended beyond the range end (0 = no extension).</summary>
    [Category("Parameters")]
    [DisplayName("Extend Bars")]
    [Range(NonlinearRegressionCurveValues.MinExtendBars, NonlinearRegressionCurveValues.MaxExtendBars)]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public int ExtendBars
    {
        get => _extendBars;
        set => _extendBars = Math.Clamp(value, NonlinearRegressionCurveValues.MinExtendBars, NonlinearRegressionCurveValues.MaxExtendBars);
    }

    /// <summary>The cached fit (tests read it; it is not persisted).</summary>
    internal NonlinearRegressionFit Fit
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
        _fit = NonlinearRegressionCurveValues.Calculate(candles, firstIndex, lastIndex, _modelType, _extendBars);
        _hasCachedValues = false;
        SetCurve(
            _fit.IsOk ? _fit.Curve : RangeCurve.Empty,
            _fit.IsOk ? RangeFitText.TruncationBadge(_fit.RangeBars, _fit.EffectiveBars) : NonlinearRegressionCurveValues.StatusText(_fit));
    }

    protected override void ClearResult()
    {
        _fit = NonlinearRegressionFit.Empty;
        _hasCachedValues = false;
        base.ClearResult();
    }

    protected override IReadOnlyList<DrawingCalculatedValue> BuildValues(IndicatorColor color)
    {
        if (!_hasCachedValues || _cachedValuesColor != color)
        {
            _cachedValues = NonlinearRegressionCurveValues.Calculate(_fit, color);
            _cachedValuesColor = color;
            _hasCachedValues = true;
        }

        return _cachedValues;
    }
}
