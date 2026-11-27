using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>The nonlinear least-squares fit of a drawing range: the curve to draw and the numbers of the Data tab.</summary>
public sealed class NonlinearRegressionFit
{
    public static readonly NonlinearRegressionFit Empty = new(RangeFitStatus.NoRange, 0, 0, NonlinearRegressionModel.Exponential, 0, RangeCurve.Empty, 0m, null, double.NaN);

    public NonlinearRegressionFit(
        RangeFitStatus status,
        int rangeBars,
        int effectiveBars,
        NonlinearRegressionModel model,
        int extendBars,
        RangeCurve curve,
        decimal fittedLast,
        decimal? shapeParameter,
        double rSquared)
    {
        Status = status;
        RangeBars = rangeBars;
        EffectiveBars = effectiveBars;
        Model = model;
        ExtendBars = extendBars;
        Curve = curve;
        FittedLast = fittedLast;
        ShapeParameter = shapeParameter;
        RSquared = rSquared;
    }

    public RangeFitStatus Status { get; }

    public int RangeBars { get; }

    /// <summary>The number of bars fitted: the range, cut to the newest <see cref="NonlinearRegressionCurveValues.MaxBars"/> bars when it is longer; 0 when no fit was tried.</summary>
    public int EffectiveBars { get; }

    public NonlinearRegressionModel Model { get; }

    public int ExtendBars { get; }

    public RangeCurve Curve { get; }

    /// <summary>The curve at the last bar of the range (x = n, the fitted value, not extrapolated).</summary>
    public decimal FittedLast { get; }

    /// <summary>The shape parameter b of the curve (Exponential: growth rate per bar, Logarithmic: the coefficient of ln x, Power: the exponent, Logistic: the slope of the logit per bar).</summary>
    public decimal? ShapeParameter { get; }

    /// <summary><c>1 - SSE / SST</c> on the price scale for every model, not clipped (a poor fit can be negative); NaN when the closes have no variance.</summary>
    public double RSquared { get; }

    public bool IsOk => Status == RangeFitStatus.Ok;

    public bool IsTruncated => EffectiveBars > 0 && EffectiveBars < RangeBars;
}

/// <summary>
/// The nonlinear regression curve of a drawing range (the single definition; the GUI object calls it). The same models, the same window rule (x = 1 .. n) and the same logistic mapping as the Nonlinear
/// Regression indicator (<see cref="NonlinearCurveFitMath"/> is the one fit); the curve is also evaluated beyond x = n for <c>ExtendBars</c> bars. A range longer than <see cref="MaxBars"/> is cut to its newest bars.
/// </summary>
public static class NonlinearRegressionCurveValues
{
    /// <summary>The smallest range that is fitted (the minimum period of the indicator).</summary>
    public const int MinBars = IndicatorDefaultConstants.NonlinearRegressionMinPeriod;

    /// <summary>The most bars one fit takes (the kernel limit).</summary>
    public const int MaxBars = DslRegressionMath.MaxObservations;

    /// <summary>The smallest extension (settings range); 0 draws no extension.</summary>
    public const int MinExtendBars = 0;

    /// <summary>The largest extension (settings range).</summary>
    public const int MaxExtendBars = 50;

    /// <summary>The extension a new tool starts with.</summary>
    public const int DefaultExtendBars = 0;

    private const string FittedKey = "NlFitted";
    private const string ProjectedKey = "NlProjected";
    private const string ShapeKey = "NlParameterB";
    private const string RSquaredKey = "NlRSquared";
    private const string ModelKey = "NlModel";
    private const string RangeBarsKey = "RangeBars";
    private const string EffectiveBarsKey = "EffectiveBars";
    private const string StateKey = "State";
    private const string PriceFormat = "F3";
    private const string StatisticFormat = "F4";

    /// <summary>The state text of a fit without a curve.</summary>
    public static string StatusText(NonlinearRegressionFit fit) => RangeFitText.StatusText(fit.Status, MinBars);

    /// <summary>The fit of the bars <paramref name="firstIndex"/> .. <paramref name="lastIndex"/> of <paramref name="candles"/> (oldest first; the closes are used).</summary>
    public static NonlinearRegressionFit Calculate(IReadOnlyList<CoreCandleData> candles, int firstIndex, int lastIndex, NonlinearRegressionModel model, int extendBars)
    {
        if (candles == null || firstIndex < 0 || lastIndex >= candles.Count || firstIndex > lastIndex)
        {
            return NonlinearRegressionFit.Empty;
        }

        int extend = Math.Clamp(extendBars, MinExtendBars, MaxExtendBars);
        int rangeBars = lastIndex - firstIndex + 1;
        int n = Math.Min(rangeBars, MaxBars);
        if (n < MinBars)
        {
            return Failed(RangeFitStatus.TooFewBars, rangeBars, 0, model, extend);
        }

        int firstFit = lastIndex - n + 1;
        var y = new double[n];
        var x = new double[n];
        for (int j = 0; j < n; j++)
        {
            y[j] = (double)candles[firstFit + j].Close;
            x[j] = j + 1;
        }

        var fitted = new double[n];
        var mapped = new double[n];
        if (!NonlinearCurveFitMath.TryFit(model, x, y, fitted, mapped, out NonlinearCurveFit fit, out _, out double rSquared))
        {
            return Failed(RangeFitStatus.FitFailed, rangeBars, n, model, extend);
        }

        int total = n + extend;
        var prices = new decimal[total];
        int valid = 0;
        for (int i = 0; i < total; i++)
        {
            decimal? price = IndicatorCalculationHelper.ToDecimalOrNull(i < n ? fitted[i] : fit.ValueAt(i + 1));
            if (!price.HasValue)
            {
                if (i < n)
                {
                    return Failed(RangeFitStatus.FitFailed, rangeBars, n, model, extend);
                }

                break;   // the extension ends where the curve is not finite
            }

            prices[i] = price.Value;
            valid = i + 1;
        }

        DateTime[] times = RangeCurve.BuildTimes(candles, lastIndex, n, extend);
        RangeCurve curve = RangeCurve.Create(times, prices, valid, n, 0m);
        return new NonlinearRegressionFit(RangeFitStatus.Ok, rangeBars, n, model, extend, curve, prices[n - 1], IndicatorCalculationHelper.ToDecimalOrNull(fit.B), rSquared);
    }

    /// <summary>The Data-tab values of a fit (the last bar of the range and the end of the extension; independent of the evaluation time).</summary>
    public static IReadOnlyList<DrawingCalculatedValue> Calculate(NonlinearRegressionFit fit, IndicatorColor color)
    {
        if (fit.Status == RangeFitStatus.NoRange)
        {
            return Array.Empty<DrawingCalculatedValue>();
        }

        var culture = DrawingValueFormat.Culture;
        var rows = new List<DrawingCalculatedValue>(8);
        if (!fit.IsOk)
        {
            rows.Add(new DrawingCalculatedValue(StateKey, "State", null, StatusText(fit), IndicatorColor.Gray));
            AddCounts(rows, fit, culture);
            return rows;
        }

        rows.Add(new DrawingCalculatedValue(ModelKey, "Model", null, fit.Model.ToString(), IndicatorColor.Gray));
        rows.Add(new DrawingCalculatedValue(FittedKey, "Fitted (last bar)", fit.FittedLast, fit.FittedLast.ToString(PriceFormat, culture), color));
        if (fit.ExtendBars > 0 && fit.Curve.Count > fit.EffectiveBars)
        {
            decimal projected = fit.Curve.Prices[fit.Curve.Count - 1];
            string label = string.Create(culture, $"Extended (+{fit.Curve.Count - fit.EffectiveBars} bars)");
            rows.Add(new DrawingCalculatedValue(ProjectedKey, label, projected, projected.ToString(PriceFormat, culture), color));
        }

        rows.Add(new DrawingCalculatedValue(ShapeKey, "Parameter b", fit.ShapeParameter, fit.ShapeParameter.HasValue ? fit.ShapeParameter.Value.ToString(StatisticFormat, culture) : "-", IndicatorColor.Gray));
        decimal? rSquared = IndicatorCalculationHelper.ToDecimalOrNull(fit.RSquared);
        rows.Add(new DrawingCalculatedValue(RSquaredKey, "R²", rSquared, rSquared.HasValue ? rSquared.Value.ToString(StatisticFormat, culture) : "-", IndicatorColor.Gray));
        AddCounts(rows, fit, culture);
        return rows;
    }

    private static NonlinearRegressionFit Failed(RangeFitStatus status, int rangeBars, int effectiveBars, NonlinearRegressionModel model, int extend)
        => new(status, rangeBars, effectiveBars, model, extend, RangeCurve.Empty, 0m, null, double.NaN);

    private static void AddCounts(List<DrawingCalculatedValue> rows, NonlinearRegressionFit fit, System.Globalization.CultureInfo culture)
    {
        rows.Add(new DrawingCalculatedValue(RangeBarsKey, "Range Bars", fit.RangeBars, fit.RangeBars.ToString(culture), IndicatorColor.Gray));
        rows.Add(new DrawingCalculatedValue(EffectiveBarsKey, "Effective Bars", fit.EffectiveBars, fit.EffectiveBars.ToString(culture), IndicatorColor.Gray));
    }
}
