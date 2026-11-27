using System.Buffers;
using StockAnalyzer.Core.Calculator;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>How the bars of a weighted regression are weighted.</summary>
public enum WeightedRegressionMode
{
    /// <summary>Weight = the bar volume (at least 1).</summary>
    VolumeWeighted = 0,

    /// <summary>Weight = lambda^(n - 1 - i): the newest bar has weight 1, older bars less.</summary>
    ExponentialDecay = 1,
}

/// <summary>A regression LINE over a drawing range (robust or weighted): the curve to draw and the numbers of the Data tab.</summary>
public sealed class RangeLineFit
{
    public static readonly RangeLineFit Empty = new(RangeFitStatus.NoRange, 0, 0, 1m, RangeCurve.Empty, 0m, 0m, 0m, 0m);

    public RangeLineFit(
        RangeFitStatus status,
        int rangeBars,
        int effectiveBars,
        decimal stdDevMultiplier,
        RangeCurve curve,
        decimal slope,
        decimal intercept,
        decimal fittedLast,
        decimal sigma)
    {
        Status = status;
        RangeBars = rangeBars;
        EffectiveBars = effectiveBars;
        StdDevMultiplier = stdDevMultiplier;
        Curve = curve;
        Slope = slope;
        Intercept = intercept;
        FittedLast = fittedLast;
        Sigma = sigma;
    }

    public RangeFitStatus Status { get; }

    public int RangeBars { get; }

    /// <summary>The number of bars fitted (the newest ones when the range is longer than the tool takes); 0 when no fit was tried.</summary>
    public int EffectiveBars { get; }

    public decimal StdDevMultiplier { get; }

    /// <summary>The line over the fitted bars and, with extend-right, the existing bars after the range end.</summary>
    public RangeCurve Curve { get; }

    /// <summary>Price change per bar.</summary>
    public decimal Slope { get; }

    /// <summary>The line at the first fitted bar.</summary>
    public decimal Intercept { get; }

    /// <summary>The line at the last bar of the range.</summary>
    public decimal FittedLast { get; }

    /// <summary>The scatter of the closes around the line (robust: MAD-based sigma; weighted: weighted root mean square), price units.</summary>
    public decimal Sigma { get; }

    public bool IsOk => Status == RangeFitStatus.Ok;

    public bool IsTruncated => EffectiveBars > 0 && EffectiveBars < RangeBars;
}

/// <summary>What the two range line tools share: the curve of a line, the rows of the Data tab.</summary>
internal static class RangeLineSupport
{
    public const string RangeBarsKey = "RangeBars";
    public const string EffectiveBarsKey = "EffectiveBars";
    public const string StateKey = "State";
    public const string PriceFormat = "F3";
    public const string StatisticFormat = "F4";

    /// <summary>The number of existing bars after <paramref name="lastIndex"/> (the extend-right bars).</summary>
    public static int TailBars(IReadOnlyList<CoreCandleData> candles, int lastIndex) => Math.Max(0, candles.Count - 1 - lastIndex);

    /// <summary>The line <c>intercept + slope i</c> over <paramref name="fitted"/> bars and <paramref name="extra"/> existing bars after the range.</summary>
    public static RangeCurve BuildLine(IReadOnlyList<CoreCandleData> candles, int lastIndex, int fitted, int extra, decimal intercept, decimal slope, decimal bandHalfWidth)
    {
        var prices = new decimal[fitted + extra];
        for (int i = 0; i < prices.Length; i++)
        {
            prices[i] = intercept + (slope * i);
        }

        return new RangeCurve(RangeCurve.BuildTimes(candles, lastIndex, fitted, extra), prices, fitted, bandHalfWidth);
    }

    public static decimal ClampMultiplier(decimal value)
        => Math.Clamp(value, (decimal)RegressionTrendValues.MinStdDevMultiplierRange, (decimal)RegressionTrendValues.MaxStdDevMultiplierRange);

    public static RangeLineFit Failed(RangeFitStatus status, int rangeBars, int effectiveBars, decimal multiplier)
        => new(status, rangeBars, effectiveBars, multiplier, RangeCurve.Empty, 0m, 0m, 0m, 0m);

    /// <summary>The Data-tab values of a line fit (the last bar of the range; independent of the evaluation time). <paramref name="prefix"/> names the tool in the keys.</summary>
    public static IReadOnlyList<DrawingCalculatedValue> Rows(RangeLineFit fit, string prefix, string statusText, IndicatorColor color)
    {
        if (fit.Status == RangeFitStatus.NoRange)
        {
            return Array.Empty<DrawingCalculatedValue>();
        }

        var culture = DrawingValueFormat.Culture;
        var rows = new List<DrawingCalculatedValue>(9);
        if (!fit.IsOk)
        {
            rows.Add(new DrawingCalculatedValue(StateKey, "State", null, statusText, IndicatorColor.Gray));
        }
        else
        {
            decimal half = fit.Curve.BandHalfWidth;
            decimal upper = fit.FittedLast + half;
            decimal lower = fit.FittedLast - half;
            rows.Add(new DrawingCalculatedValue(prefix + "Slope", "Slope", fit.Slope, fit.Slope.ToString(StatisticFormat, culture), color));
            rows.Add(new DrawingCalculatedValue(prefix + "Intercept", "Intercept (first bar)", fit.Intercept, fit.Intercept.ToString(PriceFormat, culture), color));
            rows.Add(new DrawingCalculatedValue(prefix + "FittedLast", "Fitted (last bar)", fit.FittedLast, fit.FittedLast.ToString(PriceFormat, culture), color));
            rows.Add(new DrawingCalculatedValue(prefix + "Upper", RegressionTrendValues.BandName('+', fit.StdDevMultiplier), upper, upper.ToString(PriceFormat, culture), color));
            rows.Add(new DrawingCalculatedValue(prefix + "Lower", RegressionTrendValues.BandName('-', fit.StdDevMultiplier), lower, lower.ToString(PriceFormat, culture), color));
            rows.Add(new DrawingCalculatedValue(prefix + "Sigma", "Residual Sigma", fit.Sigma, fit.Sigma.ToString(PriceFormat, culture), IndicatorColor.Gray));
        }

        rows.Add(new DrawingCalculatedValue(RangeBarsKey, "Range Bars", fit.RangeBars, fit.RangeBars.ToString(culture), IndicatorColor.Gray));
        rows.Add(new DrawingCalculatedValue(EffectiveBarsKey, "Effective Bars", fit.EffectiveBars, fit.EffectiveBars.ToString(culture), IndicatorColor.Gray));
        return rows;
    }
}

/// <summary>
/// The robust regression line of a drawing range (the single definition). The trailing bars of the range (at most the Period, which is at most <see cref="MaxBars"/>) are fitted with the Theil-Sen
/// (or Siegel repeated-median) line; the two anchors of the tool stay where the user put them and only the newest Period bars are fitted and drawn (DX-1 = A). The band is <c>k</c> times the robust
/// residual sigma. With extend-right the line continues over the existing bars after the range end. The tool fits ONE window per recalculation, so its limit is the hard cap of the kernel
/// (<see cref="RobustRegressionMath.HardMaxWindowLength"/>, measured), not the 30-bar bound of the indicator that fits every bar (<see cref="RobustRegressionMath.MaxWindowLength"/>).
/// </summary>
public static class RobustRegressionTrendValues
{
    /// <summary>The most bars one fit takes: the Period range of the tool (owner decision 2026-11-27, measured; the indicators keep their own, smaller bound).</summary>
    public const int MaxBars = RobustRegressionMath.HardMaxWindowLength;

    /// <summary>The fewest bars one fit takes.</summary>
    public const int MinBars = RobustRegressionMath.MinWindowLength;

    public static string StatusText(RangeLineFit fit) => RangeFitText.StatusText(fit.Status, MinBars);

    /// <summary>The window a new tool starts with (the old fixed window of 30 bars; owner decision DR-1: the default does not follow the larger maximum).</summary>
    public const int DefaultPeriod = RobustRegressionMath.MaxWindowLength;

    /// <summary>
    /// The robust line of the bars <paramref name="firstIndex"/> .. <paramref name="lastIndex"/>. <paramref name="period"/> is the number of bars fitted when the range is longer (the newest ones; clamped to
    /// <see cref="MinBars"/> .. <see cref="MaxBars"/>; the default is the old fixed 30); <paramref name="priceSource"/> is the price that is fitted (the close by default, see <see cref="DrawingRangePriceSeries"/>:
    /// the series starts at the first fitted bar).
    /// </summary>
    public static RangeLineFit Calculate(
        IReadOnlyList<CoreCandleData> candles,
        int firstIndex,
        int lastIndex,
        bool useSiegelMedian,
        decimal stdDevMultiplier,
        bool extendRight,
        int period = DefaultPeriod,
        PriceType priceSource = PriceType.Close)
    {
        if (candles == null || firstIndex < 0 || lastIndex >= candles.Count || firstIndex > lastIndex)
        {
            return RangeLineFit.Empty;
        }

        decimal multiplier = RangeLineSupport.ClampMultiplier(stdDevMultiplier);
        int rangeBars = lastIndex - firstIndex + 1;
        int n = Math.Min(rangeBars, Math.Clamp(period, MinBars, MaxBars));
        if (n < MinBars)
        {
            return RangeLineSupport.Failed(RangeFitStatus.TooFewBars, rangeBars, 0, multiplier);
        }

        int firstFit = lastIndex - n + 1;
        decimal[] window = DrawingRangePriceSeries.Extract(candles, firstFit, lastIndex, priceSource);

        // Up to the indicator bound the scratch is a plain array (the old path); a longer window borrows it from the shared pool (the Theil-Sen scratch of W = 500 is 1.9 MB, a large object) and returns it.
        int scratchLength = RobustRegressionMath.ScratchLength(n, useSiegelMedian);
        decimal[]? rented = n > RobustRegressionMath.MaxWindowLength ? ArrayPool<decimal>.Shared.Rent(scratchLength) : null;
        decimal[] scratch = rented ?? new decimal[scratchLength];
        try
        {
            if (!RobustRegressionMath.TryFit(window, useSiegelMedian, scratch.AsSpan(0, scratchLength), MaxBars, out RobustLine line))
            {
                return RangeLineSupport.Failed(RangeFitStatus.FitFailed, rangeBars, n, multiplier);
            }

            int extra = extendRight ? RangeLineSupport.TailBars(candles, lastIndex) : 0;
            RangeCurve curve = RangeLineSupport.BuildLine(candles, lastIndex, n, extra, line.Intercept, line.Slope, line.ResidualSigma * multiplier);
            return new RangeLineFit(RangeFitStatus.Ok, rangeBars, n, multiplier, curve, line.Slope, line.Intercept, line.FittedLast, line.ResidualSigma);
        }
        catch (OverflowException)
        {
            return RangeLineSupport.Failed(RangeFitStatus.FitFailed, rangeBars, n, multiplier);
        }
        finally
        {
            if (rented != null)
            {
                ArrayPool<decimal>.Shared.Return(rented);
            }
        }
    }

    public static IReadOnlyList<DrawingCalculatedValue> Calculate(RangeLineFit fit, IndicatorColor color)
        => RangeLineSupport.Rows(fit, "Robust", StatusText(fit), color);
}

/// <summary>
/// The weighted regression line of a drawing range (the single definition): ONE <see cref="DslRegressionMath.WlsFit"/> of the closes of the range (the newest <see cref="MaxBars"/> bars when longer)
/// against the bar offset, weighted by volume (<c>w = max(V, 1)</c>) or exponentially (<c>w = lambda^(n - 1 - i)</c>). The band is <c>k</c> times
/// <c>sigma_w = sqrt(sum w e^2 / sum w)</c> (the weighted form of the divisor-N convention of the regression trend tool).
/// </summary>
public static class WeightedRegressionTrendValues
{
    /// <summary>The most bars one fit takes (the kernel limit).</summary>
    public const int MaxBars = DslRegressionMath.MaxObservations;

    /// <summary>The fewest bars one fit takes (the kernel needs more than three effective rows for one column).</summary>
    public const int MinBars = 3;

    /// <summary>The smallest decay factor (settings range).</summary>
    public const double MinDecayFactor = 0.80;

    /// <summary>The largest decay factor (settings range).</summary>
    public const double MaxDecayFactor = 0.99;

    /// <summary>The decay factor a new tool starts with.</summary>
    public const double DefaultDecayFactor = 0.94;

    public static string StatusText(RangeLineFit fit) => RangeFitText.StatusText(fit.Status, MinBars);

    public static RangeLineFit Calculate(
        IReadOnlyList<CoreCandleData> candles,
        int firstIndex,
        int lastIndex,
        WeightedRegressionMode mode,
        double decayFactor,
        decimal stdDevMultiplier,
        bool extendRight)
    {
        if (candles == null || firstIndex < 0 || lastIndex >= candles.Count || firstIndex > lastIndex)
        {
            return RangeLineFit.Empty;
        }

        decimal multiplier = RangeLineSupport.ClampMultiplier(stdDevMultiplier);
        double lambda = Math.Clamp(decayFactor, MinDecayFactor, MaxDecayFactor);
        int rangeBars = lastIndex - firstIndex + 1;
        int n = Math.Min(rangeBars, MaxBars);
        if (n < MinBars)
        {
            return RangeLineSupport.Failed(RangeFitStatus.TooFewBars, rangeBars, 0, multiplier);
        }

        int firstFit = lastIndex - n + 1;
        var x = new double[n];
        var y = new double[n];
        var w = new double[n];
        for (int i = 0; i < n; i++)
        {
            CoreCandleData bar = candles[firstFit + i];
            x[i] = i;
            y[i] = (double)bar.Close;
            w[i] = mode == WeightedRegressionMode.VolumeWeighted ? Math.Max((double)bar.Volume, 1.0) : Math.Pow(lambda, n - 1 - i);
        }

        var beta = new double[2];
        var budget = DslExecutionBudget.Create(CalculatorLimits.Default.MaxExecutionOps);
        if (DslRegressionMath.WlsFit(x, n, 1, y, w, beta, ref budget) != MathErrorCode.None)
        {
            return RangeLineSupport.Failed(RangeFitStatus.FitFailed, rangeBars, n, multiplier);
        }

        double weightedSquares = 0.0;
        double weightSum = 0.0;
        for (int i = 0; i < n; i++)
        {
            double residual = y[i] - (beta[0] + (beta[1] * i));
            weightedSquares += w[i] * residual * residual;
            weightSum += w[i];
        }

        decimal? intercept = IndicatorCalculationHelper.ToDecimalOrNull(beta[0]);
        decimal? slope = IndicatorCalculationHelper.ToDecimalOrNull(beta[1]);
        decimal? sigma = weightSum > 0.0 ? IndicatorCalculationHelper.ToDecimalOrNull(Math.Sqrt(weightedSquares / weightSum)) : null;
        if (!intercept.HasValue || !slope.HasValue || !sigma.HasValue)
        {
            return RangeLineSupport.Failed(RangeFitStatus.FitFailed, rangeBars, n, multiplier);
        }

        try
        {
            int extra = extendRight ? RangeLineSupport.TailBars(candles, lastIndex) : 0;
            RangeCurve curve = RangeLineSupport.BuildLine(candles, lastIndex, n, extra, intercept.Value, slope.Value, sigma.Value * multiplier);
            return new RangeLineFit(RangeFitStatus.Ok, rangeBars, n, multiplier, curve, slope.Value, intercept.Value, curve.Prices[n - 1], sigma.Value);
        }
        catch (OverflowException)
        {
            return RangeLineSupport.Failed(RangeFitStatus.FitFailed, rangeBars, n, multiplier);
        }
    }

    public static IReadOnlyList<DrawingCalculatedValue> Calculate(RangeLineFit fit, IndicatorColor color)
        => RangeLineSupport.Rows(fit, "Weighted", StatusText(fit), color);
}
