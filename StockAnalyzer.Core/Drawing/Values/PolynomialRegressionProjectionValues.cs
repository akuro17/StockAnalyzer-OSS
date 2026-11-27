using StockAnalyzer.Core.Calculator;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>The polynomial least-squares fit of a drawing range: the curve to draw and the numbers of the Data tab.</summary>
public sealed class PolynomialRegressionFit
{
    public static readonly PolynomialRegressionFit Empty = new(RangeFitStatus.NoRange, 0, 0, 0, 0, 1m, RangeCurve.Empty, 0m, 0m, double.NaN);

    public PolynomialRegressionFit(
        RangeFitStatus status,
        int rangeBars,
        int effectiveBars,
        int degree,
        int forecastBars,
        decimal stdDevMultiplier,
        RangeCurve curve,
        decimal fittedLast,
        decimal residualSigma,
        double rSquared)
    {
        Status = status;
        RangeBars = rangeBars;
        EffectiveBars = effectiveBars;
        Degree = degree;
        ForecastBars = forecastBars;
        StdDevMultiplier = stdDevMultiplier;
        Curve = curve;
        FittedLast = fittedLast;
        ResidualSigma = residualSigma;
        RSquared = rSquared;
    }

    public RangeFitStatus Status { get; }

    /// <summary>The number of bars between the two anchors.</summary>
    public int RangeBars { get; }

    /// <summary>The number of bars fitted: the range, cut to the newest <see cref="PolynomialRegressionProjectionValues.MaxBars"/> bars when it is longer; 0 when no fit was tried.</summary>
    public int EffectiveBars { get; }

    public int Degree { get; }

    public int ForecastBars { get; }

    public decimal StdDevMultiplier { get; }

    /// <summary>The curve over the fitted bars and the projected bars.</summary>
    public RangeCurve Curve { get; }

    /// <summary>The curve at the last bar of the range (the fitted value, not extrapolated).</summary>
    public decimal FittedLast { get; }

    /// <summary>The standard deviation of the residuals, divisor N (the convention of the regression trend tool; DX-13 = A), in price units.</summary>
    public decimal ResidualSigma { get; }

    /// <summary><c>1 - SSE / SST</c> on the price scale; NaN when the closes have no variance.</summary>
    public double RSquared { get; }

    public bool IsOk => Status == RangeFitStatus.Ok;

    public bool IsTruncated => EffectiveBars > 0 && EffectiveBars < RangeBars;
}

/// <summary>
/// The polynomial regression of a drawing range (the single definition; the GUI object calls it). The closes of the range (the newest <see cref="MaxBars"/> when it is longer) are fitted with a
/// degree-<c>d</c> polynomial on <c>u_i = 2 i / (n - 1) - 1</c> (<see cref="DslRegressionMath.OlsFit"/> on the columns <c>u^1 .. u^d</c> with the intercept <c>beta[0]</c>, ONE fit per calculation); the curve at bar
/// offset <c>i</c> (also beyond <c>n - 1</c> for the projection) is evaluated with Horner's rule, O(d) per point. The band is <c>k</c> times the residual standard deviation (divisor N).
/// </summary>
public static class PolynomialRegressionProjectionValues
{
    /// <summary>The smallest degree (settings range).</summary>
    public const int MinDegree = IndicatorDefaultConstants.PolynomialRegressionMinDegree;

    /// <summary>The largest degree (settings range).</summary>
    public const int MaxDegree = IndicatorDefaultConstants.PolynomialRegressionMaxDegree;

    /// <summary>The degree a new tool starts with.</summary>
    public const int DefaultDegree = IndicatorDefaultConstants.PolynomialRegressionDefaultDegree;

    /// <summary>The smallest projection (settings range); 0 draws no projection.</summary>
    public const int MinForecastBars = IndicatorDefaultConstants.PolynomialRegressionMinForecastHorizon;

    /// <summary>The largest projection (settings range).</summary>
    public const int MaxForecastBars = IndicatorDefaultConstants.PolynomialRegressionMaxForecastHorizon;

    /// <summary>The projection a new tool starts with.</summary>
    public const int DefaultForecastBars = IndicatorDefaultConstants.PolynomialRegressionDefaultForecastHorizon;

    /// <summary>The most bars one fit takes (the kernel limit); a longer range is cut to its newest bars.</summary>
    public const int MaxBars = DslRegressionMath.MaxObservations;

    private const string FittedKey = "PolyFitted";
    private const string ProjectedKey = "PolyProjected";
    private const string UpperKey = "PolyUpper";
    private const string LowerKey = "PolyLower";
    private const string SigmaKey = "PolySigma";
    private const string RSquaredKey = "PolyRSquared";
    private const string DegreeKey = "PolyDegree";
    private const string RangeBarsKey = "RangeBars";
    private const string EffectiveBarsKey = "EffectiveBars";
    private const string StateKey = "State";
    private const string PriceFormat = "F3";
    private const string StatisticFormat = "F4";

    /// <summary>The smallest range a degree can be fitted on: the kernel needs <c>n &gt;= degree + 2</c>.</summary>
    public static int MinBars(int degree) => degree + 2;

    /// <summary>The state text of a fit without a curve.</summary>
    public static string StatusText(PolynomialRegressionFit fit) => RangeFitText.StatusText(fit.Status, MinBars(fit.Degree));

    /// <summary>The fit of the bars <paramref name="firstIndex"/> .. <paramref name="lastIndex"/> of <paramref name="candles"/> (oldest first; the closes are used).</summary>
    public static PolynomialRegressionFit Calculate(
        IReadOnlyList<CoreCandleData> candles,
        int firstIndex,
        int lastIndex,
        int degree,
        int forecastBars,
        decimal stdDevMultiplier)
    {
        if (candles == null || firstIndex < 0 || lastIndex >= candles.Count || firstIndex > lastIndex)
        {
            return PolynomialRegressionFit.Empty;
        }

        int d = Math.Clamp(degree, MinDegree, MaxDegree);
        int forecast = Math.Clamp(forecastBars, MinForecastBars, MaxForecastBars);
        decimal multiplier = Math.Clamp(stdDevMultiplier, (decimal)RegressionTrendValues.MinStdDevMultiplierRange, (decimal)RegressionTrendValues.MaxStdDevMultiplierRange);
        int rangeBars = lastIndex - firstIndex + 1;
        int n = Math.Min(rangeBars, MaxBars);
        if (n < MinBars(d))
        {
            return Failed(RangeFitStatus.TooFewBars, rangeBars, 0, d, forecast, multiplier);
        }

        int firstFit = lastIndex - n + 1;
        var y = new double[n];
        for (int i = 0; i < n; i++)
        {
            y[i] = (double)candles[firstFit + i].Close;
        }

        double invSpan = 2.0 / (n - 1);
        var design = new double[n * d];
        for (int i = 0; i < n; i++)
        {
            double u = (i * invSpan) - 1.0;
            double power = u;
            for (int column = 0; column < d; column++)
            {
                design[(i * d) + column] = power;
                power *= u;
            }
        }

        var beta = new double[d + 1];
        var budget = DslExecutionBudget.Create(CalculatorLimits.Default.MaxExecutionOps);
        if (DslRegressionMath.OlsFit(design, n, d, y, beta, ref budget) != MathErrorCode.None)
        {
            return Failed(RangeFitStatus.FitFailed, rangeBars, n, d, forecast, multiplier);
        }

        int total = n + forecast;
        var prices = new decimal[total];
        double mean = 0.0;
        for (int i = 0; i < n; i++)
        {
            mean += y[i];
        }

        mean /= n;
        double sse = 0.0;
        double sst = 0.0;
        int valid = 0;
        for (int i = 0; i < total; i++)
        {
            double fitted = Evaluate(beta, (i * invSpan) - 1.0);
            decimal? price = IndicatorCalculationHelper.ToDecimalOrNull(fitted);
            if (!price.HasValue)
            {
                if (i < n)
                {
                    return Failed(RangeFitStatus.FitFailed, rangeBars, n, d, forecast, multiplier);
                }

                break;   // the projection ends where the value is not finite
            }

            prices[i] = price.Value;
            valid = i + 1;
            if (i < n)
            {
                double residual = y[i] - fitted;
                sse += residual * residual;
                sst += (y[i] - mean) * (y[i] - mean);
            }
        }

        decimal? sigma = IndicatorCalculationHelper.ToDecimalOrNull(Math.Sqrt(sse / n));
        if (!sigma.HasValue)
        {
            return Failed(RangeFitStatus.FitFailed, rangeBars, n, d, forecast, multiplier);
        }

        double rSquared = sst > 0.0 ? 1.0 - (sse / sst) : double.NaN;
        DateTime[] times = RangeCurve.BuildTimes(candles, lastIndex, n, forecast);
        RangeCurve curve = RangeCurve.Create(times, prices, valid, n, sigma.Value * multiplier);
        return new PolynomialRegressionFit(RangeFitStatus.Ok, rangeBars, n, d, forecast, multiplier, curve, prices[n - 1], sigma.Value, rSquared);
    }

    /// <summary>The Data-tab values of a fit (the last bar of the range and the end of the projection; independent of the evaluation time).</summary>
    public static IReadOnlyList<DrawingCalculatedValue> Calculate(PolynomialRegressionFit fit, IndicatorColor color)
    {
        if (fit.Status == RangeFitStatus.NoRange)
        {
            return Array.Empty<DrawingCalculatedValue>();
        }

        var culture = DrawingValueFormat.Culture;
        var rows = new List<DrawingCalculatedValue>(10);
        if (!fit.IsOk)
        {
            rows.Add(new DrawingCalculatedValue(StateKey, "State", null, StatusText(fit), IndicatorColor.Gray));
            AddCounts(rows, fit, culture);
            return rows;
        }

        decimal half = fit.Curve.BandHalfWidth;
        rows.Add(new DrawingCalculatedValue(FittedKey, "Fitted (last bar)", fit.FittedLast, fit.FittedLast.ToString(PriceFormat, culture), color));
        if (fit.ForecastBars > 0 && fit.Curve.Count > fit.EffectiveBars)
        {
            decimal projected = fit.Curve.Prices[fit.Curve.Count - 1];
            string label = string.Create(culture, $"Projected (+{fit.Curve.Count - fit.EffectiveBars} bars)");
            rows.Add(new DrawingCalculatedValue(ProjectedKey, label, projected, projected.ToString(PriceFormat, culture), color));
        }

        rows.Add(PriceRow(UpperKey, RegressionTrendValues.BandName('+', fit.StdDevMultiplier), fit.FittedLast + half, color, culture));
        rows.Add(PriceRow(LowerKey, RegressionTrendValues.BandName('-', fit.StdDevMultiplier), fit.FittedLast - half, color, culture));
        rows.Add(PriceRow(SigmaKey, "Residual StdDev", fit.ResidualSigma, IndicatorColor.Gray, culture));
        decimal? rSquared = IndicatorCalculationHelper.ToDecimalOrNull(fit.RSquared);
        rows.Add(new DrawingCalculatedValue(RSquaredKey, "R²", rSquared, rSquared.HasValue ? rSquared.Value.ToString(StatisticFormat, culture) : "-", IndicatorColor.Gray));
        rows.Add(new DrawingCalculatedValue(DegreeKey, "Degree", fit.Degree, fit.Degree.ToString(culture), IndicatorColor.Gray));
        AddCounts(rows, fit, culture);
        return rows;
    }

    /// <summary>The polynomial <c>beta[0] + beta[1] u + ... + beta[d] u^d</c> by Horner's rule.</summary>
    private static double Evaluate(double[] beta, double u)
    {
        double value = beta[beta.Length - 1];
        for (int k = beta.Length - 2; k >= 0; k--)
        {
            value = (value * u) + beta[k];
        }

        return value;
    }

    private static PolynomialRegressionFit Failed(RangeFitStatus status, int rangeBars, int effectiveBars, int degree, int forecast, decimal multiplier)
        => new(status, rangeBars, effectiveBars, degree, forecast, multiplier, RangeCurve.Empty, 0m, 0m, double.NaN);

    private static void AddCounts(List<DrawingCalculatedValue> rows, PolynomialRegressionFit fit, System.Globalization.CultureInfo culture)
    {
        rows.Add(new DrawingCalculatedValue(RangeBarsKey, "Range Bars", fit.RangeBars, fit.RangeBars.ToString(culture), IndicatorColor.Gray));
        rows.Add(new DrawingCalculatedValue(EffectiveBarsKey, "Effective Bars", fit.EffectiveBars, fit.EffectiveBars.ToString(culture), IndicatorColor.Gray));
    }

    private static DrawingCalculatedValue PriceRow(string key, string label, decimal value, IndicatorColor color, System.Globalization.CultureInfo culture)
        => new(key, label, value, value.ToString(PriceFormat, culture), color);
}
