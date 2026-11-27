using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>Why a GARCH volatility cone has no bands (or that it has).</summary>
public enum GarchConeStatus
{
    /// <summary>The model was fitted and the bands are available.</summary>
    Ok,

    /// <summary>No bar lies between the two anchor times.</summary>
    NoRange,

    /// <summary>The range has fewer than <see cref="GarchVolatilityConeValues.MinBars"/> bars.</summary>
    TooFewBars,

    /// <summary>The fit failed: a flat window or no usable optimum.</summary>
    FitFailed,

    /// <summary>A selected price of the fitted window is not above zero (a log return needs positive prices; for example a negative oil price).</summary>
    NonPositivePrice,
}

/// <summary>
/// The result of one GARCH(1,1) volatility cone: the fit, the cumulative variance per horizon step and the 1 and 2 sigma price bands around the last close of the range.
/// The band arrays have <see cref="Horizon"/> entries (index <c>h - 1</c> is the step <c>h</c>); an entry is null when the band is not finite (it is not drawn).
/// </summary>
public sealed class GarchVolatilityCone
{
    /// <summary>The cone of a range that has no bar.</summary>
    public static readonly GarchVolatilityCone Empty = new(GarchConeStatus.NoRange, 0, 0, 0, default, 0m, 0.0, Array.Empty<decimal?>(), Array.Empty<decimal?>(), Array.Empty<decimal?>(), Array.Empty<decimal?>());

    public GarchVolatilityCone(
        GarchConeStatus status,
        int rangeBars,
        int effectiveBars,
        int horizon,
        Garch11Fit fit,
        decimal centerPrice,
        double longRunVariance,
        decimal?[] upper1,
        decimal?[] lower1,
        decimal?[] upper2,
        decimal?[] lower2)
    {
        Status = status;
        RangeBars = rangeBars;
        EffectiveBars = effectiveBars;
        Horizon = horizon;
        Fit = fit;
        CenterPrice = centerPrice;
        LongRunVariance = longRunVariance;
        Upper1 = upper1;
        Lower1 = lower1;
        Upper2 = upper2;
        Lower2 = lower2;
    }

    public GarchConeStatus Status { get; }

    /// <summary>The number of bars between the two anchors (both ends included).</summary>
    public int RangeBars { get; }

    /// <summary>The number of bars the model was estimated on: the range, cut to the newest <see cref="GarchVolatilityConeValues.MaxBars"/> bars when it is longer (DX-10 = A); 0 when no fit was tried.</summary>
    public int EffectiveBars { get; }

    /// <summary>The number of future bars of the cone.</summary>
    public int Horizon { get; }

    /// <summary>The fitted model (percent log returns; the variances are in percent squared). Default when <see cref="Status"/> is not <see cref="GarchConeStatus.Ok"/>.</summary>
    public Garch11Fit Fit { get; }

    /// <summary>The selected price (the close by default) of the last bar of the range: the centre of the cone.</summary>
    public decimal CenterPrice { get; }

    /// <summary>The unconditional variance <c>omega / (1 - alpha - beta)</c> in percent squared.</summary>
    public double LongRunVariance { get; }

    public decimal?[] Upper1 { get; }

    public decimal?[] Lower1 { get; }

    public decimal?[] Upper2 { get; }

    public decimal?[] Lower2 { get; }

    /// <summary>True when the model was fitted and the bands are available.</summary>
    public bool IsOk => Status == GarchConeStatus.Ok;

    /// <summary>The persistence <c>alpha + beta</c>.</summary>
    public double Persistence => Fit.Alpha + Fit.Beta;

    /// <summary>True when the range was longer than <see cref="GarchVolatilityConeValues.MaxBars"/> and only its newest bars were used.</summary>
    public bool IsTruncated => EffectiveBars > 0 && EffectiveBars < RangeBars;
}

/// <summary>
/// The GARCH volatility cone of a drawing range (the single definition; the GUI object calls it and a later MCP catalogue entry will call it).
/// The model is the existing GARCH(1,1) of <see cref="Garch11Math"/> on the percent log returns of the closes of the range (<see cref="EgarchMath.PercentLogReturn"/>):
/// <c>s2(T+k) = sbar2 + (alpha + beta)^(k-1) (NextVariance - sbar2)</c>, <c>V(h) = sum of s2(T+1 .. T+h)</c>, band <c>P_T exp(+- z 0.01 sqrt(V(h)))</c> with <c>z</c> in {1, 2} and zero drift
/// (the fitted mean is NOT added, the cone is centred on the last close <c>P_T</c> of the range). The returns are those of the bars inside the range (the bar before the range is never read).
/// The price the returns are taken from is a setting (<see cref="PriceType"/>, the close by default): the series is <see cref="DrawingRangePriceSeries"/> of the fitted window and the cone is centred on
/// its last value. A price that is not above zero gives <see cref="GarchConeStatus.NonPositivePrice"/>.
/// </summary>
public static class GarchVolatilityConeValues
{
    /// <summary>The smallest number of returns a fit needs.</summary>
    public const int MinReturns = 30;

    /// <summary>The smallest range (in bars) that has <see cref="MinReturns"/> returns.</summary>
    public const int MinBars = MinReturns + 1;

    /// <summary>The largest number of returns the model is estimated on; a longer range is cut to its newest returns (DX-10 = A). The same limit as the indicator's <c>Period</c>.</summary>
    public const int MaxReturns = IndicatorDefaultConstants.Garch11MaxPeriod;

    /// <summary>The largest number of bars the model is estimated on (<see cref="MaxReturns"/> returns need one bar more).</summary>
    public const int MaxBars = MaxReturns + 1;

    /// <summary>The smallest forecast horizon (settings range).</summary>
    public const int MinHorizon = 5;

    /// <summary>The largest forecast horizon (settings range).</summary>
    public const int MaxHorizon = 100;

    /// <summary>The horizon a new cone starts with.</summary>
    public const int DefaultHorizon = 20;

    /// <summary>The multiplier of the inner band.</summary>
    public const double InnerSigma = 1.0;

    /// <summary>The multiplier of the outer band.</summary>
    public const double OuterSigma = 2.0;

    /// <summary>The factor from a percent log return to a fractional log return.</summary>
    public const double PercentToFraction = 0.01;

    /// <summary>The optimizer iterations of one fit (the indicator's default).</summary>
    public const int FitMaxIterations = IndicatorDefaultConstants.Garch11DefaultMaxIterations;

    private const string CenterKey = "GarchCenter";
    private const string Upper1Key = "GarchUpper1";
    private const string Lower1Key = "GarchLower1";
    private const string Upper2Key = "GarchUpper2";
    private const string Lower2Key = "GarchLower2";
    private const string AlphaKey = "GarchAlpha";
    private const string BetaKey = "GarchBeta";
    private const string PersistenceKey = "GarchPersistence";
    private const string LongRunVolatilityKey = "GarchLongRunVolatility";
    private const string RangeBarsKey = "RangeBars";
    private const string EffectiveBarsKey = "EffectiveBars";
    private const string StateKey = "State";
    private const string PriceFormat = "F3";
    private const string CoefficientFormat = "F4";
    private const string PercentFormat = "F3";

    /// <summary>The text of a cone that has no bands (shown in the chart and in the Data tab).</summary>
    public static string StatusText(GarchConeStatus status) => status switch
    {
        GarchConeStatus.Ok => string.Empty,
        GarchConeStatus.NoRange => "No bars in the range",
        GarchConeStatus.TooFewBars => string.Create(DrawingValueFormat.Culture, $"Insufficient bars (< {MinBars})"),
        GarchConeStatus.NonPositivePrice => "Prices must be above zero",
        _ => "GARCH fit failed",
    };

    /// <summary>The badge of a cone that was fitted on the newest bars only (R-18); empty when the whole range was used.</summary>
    public static string TruncationBadge(GarchVolatilityCone cone)
        => cone.IsTruncated ? string.Create(DrawingValueFormat.Culture, $"Last {cone.EffectiveBars} bars") : string.Empty;

    /// <summary>
    /// Fits the model on the bars <paramref name="firstIndex"/> .. <paramref name="lastIndex"/> of <paramref name="candles"/> (oldest first; the closes are used) and builds the cone
    /// of <paramref name="horizon"/> future bars. A range with fewer than <see cref="MinBars"/> bars, a failed fit or a horizon outside the settings range gives a cone without bands.
    /// <paramref name="priceSource"/> is the price the log returns are taken from (the series starts at the first bar of the fitted window, see <see cref="DrawingRangePriceSeries"/>).
    /// </summary>
    public static GarchVolatilityCone Calculate(IReadOnlyList<CoreCandleData> candles, int firstIndex, int lastIndex, int horizon, PriceType priceSource = PriceType.Close)
    {
        if (candles == null || firstIndex < 0 || lastIndex >= candles.Count || firstIndex > lastIndex)
        {
            return GarchVolatilityCone.Empty;
        }

        int rangeBars = lastIndex - firstIndex + 1;
        int steps = Math.Clamp(horizon, MinHorizon, MaxHorizon);
        if (rangeBars < MinBars)
        {
            return Failed(GarchConeStatus.TooFewBars, rangeBars, 0, steps, candles[lastIndex].Close);
        }

        int returnCount = Math.Min(rangeBars - 1, MaxReturns);
        int effectiveBars = returnCount + 1;
        int firstWindowBar = lastIndex - returnCount;   // the bar whose price starts the first return; always inside the range

        decimal[] prices = DrawingRangePriceSeries.Extract(candles, firstWindowBar, lastIndex, priceSource);
        decimal centerPrice = prices[prices.Length - 1];
        for (int k = 0; k < prices.Length; k++)
        {
            if (prices[k] <= 0m)
            {
                return Failed(GarchConeStatus.NonPositivePrice, rangeBars, effectiveBars, steps, centerPrice);
            }
        }

        double[] returns = new double[returnCount];
        for (int k = 0; k < returnCount; k++)
        {
            returns[k] = EgarchMath.PercentLogReturn((double)prices[k], (double)prices[k + 1]);
        }

        if (!Garch11Math.TryFit(returns, 0, returnCount, FitMaxIterations, out Garch11Fit fit))
        {
            return Failed(GarchConeStatus.FitFailed, rangeBars, effectiveBars, steps, centerPrice);
        }

        double persistence = fit.Alpha + fit.Beta;
        double longRunVariance = fit.Omega / (1.0 - persistence);
        if (!double.IsFinite(longRunVariance) || !(longRunVariance > 0.0))
        {
            return Failed(GarchConeStatus.FitFailed, rangeBars, effectiveBars, steps, centerPrice);
        }

        double center = (double)centerPrice;
        var upper1 = new decimal?[steps];
        var lower1 = new decimal?[steps];
        var upper2 = new decimal?[steps];
        var lower2 = new decimal?[steps];

        double cumulative = 0.0;
        double decay = 1.0;   // persistence^(k-1)
        for (int h = 1; h <= steps; h++)
        {
            double stepVariance = longRunVariance + decay * (fit.NextVariance - longRunVariance);
            cumulative += stepVariance;
            decay *= persistence;

            double deviation = PercentToFraction * Math.Sqrt(cumulative);
            bool usable = cumulative > 0.0 && double.IsFinite(cumulative) && double.IsFinite(stepVariance) && stepVariance > 0.0;
            int index = h - 1;
            upper1[index] = usable ? Band(center, InnerSigma * deviation) : null;
            lower1[index] = usable ? Band(center, -InnerSigma * deviation) : null;
            upper2[index] = usable ? Band(center, OuterSigma * deviation) : null;
            lower2[index] = usable ? Band(center, -OuterSigma * deviation) : null;
        }

        return new GarchVolatilityCone(GarchConeStatus.Ok, rangeBars, effectiveBars, steps, fit, centerPrice, longRunVariance, upper1, lower1, upper2, lower2);
    }

    /// <summary>The Data-tab values of a cone (the horizon end of the cone and the fit; independent of the evaluation time).</summary>
    public static IReadOnlyList<DrawingCalculatedValue> Calculate(GarchVolatilityCone cone, IndicatorColor color)
    {
        if (cone.Status == GarchConeStatus.NoRange)
        {
            return Array.Empty<DrawingCalculatedValue>();
        }

        var culture = DrawingValueFormat.Culture;
        var rows = new List<DrawingCalculatedValue>(12);
        if (!cone.IsOk)
        {
            rows.Add(new DrawingCalculatedValue(StateKey, "State", null, StatusText(cone.Status), IndicatorColor.Gray));
            AddCounts(rows, cone, culture);
            return rows;
        }

        int last = cone.Horizon - 1;
        decimal? upper1 = cone.Upper1[last];
        decimal? lower1 = cone.Lower1[last];
        decimal? upper2 = cone.Upper2[last];
        decimal? lower2 = cone.Lower2[last];
        string horizonText = string.Create(culture, $"+{cone.Horizon} bars");

        rows.Add(new DrawingCalculatedValue(CenterKey, "Center (last close)", cone.CenterPrice, cone.CenterPrice.ToString(PriceFormat, culture), color));
        rows.Add(PriceRow(Upper1Key, string.Create(culture, $"+{InnerSigma:0.##}σ {horizonText}"), upper1, color, culture));
        rows.Add(PriceRow(Lower1Key, string.Create(culture, $"-{InnerSigma:0.##}σ {horizonText}"), lower1, color, culture));
        rows.Add(PriceRow(Upper2Key, string.Create(culture, $"+{OuterSigma:0.##}σ {horizonText}"), upper2, color, culture));
        rows.Add(PriceRow(Lower2Key, string.Create(culture, $"-{OuterSigma:0.##}σ {horizonText}"), lower2, color, culture));
        rows.Add(NumberRow(AlphaKey, "GARCH Alpha", cone.Fit.Alpha, CoefficientFormat, IndicatorColor.Gray, culture));
        rows.Add(NumberRow(BetaKey, "GARCH Beta", cone.Fit.Beta, CoefficientFormat, IndicatorColor.Gray, culture));
        rows.Add(NumberRow(PersistenceKey, "Persistence (alpha + beta)", cone.Persistence, CoefficientFormat, IndicatorColor.Gray, culture));
        rows.Add(NumberRow(LongRunVolatilityKey, "Long-run Volatility (%/bar)", Math.Sqrt(cone.LongRunVariance), PercentFormat, IndicatorColor.Gray, culture));
        AddCounts(rows, cone, culture);
        return rows;
    }

    /// <summary>The Data-tab values of the cone of a range given by its two anchors (the catalogue entry): the range is the bars between the two anchor times; no bar gives no values.</summary>
    public static IReadOnlyList<DrawingCalculatedValue> Calculate(DrawingAnchor point1, DrawingAnchor point2, IReadOnlyList<CoreCandleData>? candles, int horizon, IndicatorColor color, PriceType priceSource = PriceType.Close)
    {
        if (candles == null || !DrawingRangeBars.TryFind(candles, point1.Time, point2.Time, out int first, out int last))
        {
            return Array.Empty<DrawingCalculatedValue>();
        }

        return Calculate(Calculate(candles, first, last, horizon, priceSource), color);
    }

    private static GarchVolatilityCone Failed(GarchConeStatus status, int rangeBars, int effectiveBars, int steps, decimal centerPrice)
        => new(status, rangeBars, effectiveBars, steps, default, centerPrice, 0.0, Array.Empty<decimal?>(), Array.Empty<decimal?>(), Array.Empty<decimal?>(), Array.Empty<decimal?>());

    private static decimal? Band(double center, double logDeviation)
        => IndicatorCalculationHelper.ToDecimalOrNull(center * Math.Exp(logDeviation));

    private static void AddCounts(List<DrawingCalculatedValue> rows, GarchVolatilityCone cone, System.Globalization.CultureInfo culture)
    {
        rows.Add(new DrawingCalculatedValue(RangeBarsKey, "Range Bars", cone.RangeBars, cone.RangeBars.ToString(culture), IndicatorColor.Gray));
        rows.Add(new DrawingCalculatedValue(EffectiveBarsKey, "Effective Bars", cone.EffectiveBars, cone.EffectiveBars.ToString(culture), IndicatorColor.Gray));
    }

    private static DrawingCalculatedValue PriceRow(string key, string label, decimal? value, IndicatorColor color, System.Globalization.CultureInfo culture)
        => new(key, label, value, value.HasValue ? value.Value.ToString(PriceFormat, culture) : "-", color);

    private static DrawingCalculatedValue NumberRow(string key, string label, double value, string format, IndicatorColor color, System.Globalization.CultureInfo culture)
    {
        decimal? number = IndicatorCalculationHelper.ToDecimalOrNull(value);
        return new DrawingCalculatedValue(key, label, number, number.HasValue ? number.Value.ToString(format, culture) : "-", color);
    }
}
