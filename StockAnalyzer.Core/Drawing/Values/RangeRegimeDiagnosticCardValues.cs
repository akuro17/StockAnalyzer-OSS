using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>
/// The regime diagnostics of one drawing range: eight statistics of the closes between the two anchors and the numbers they were computed from. A statistic that cannot be calculated
/// (window too short, flat prices, a close that is not above zero for a return) is null and shown as "-".
/// </summary>
public sealed class RangeRegimeDiagnosticCard
{
    /// <summary>A card of a range that has no bar.</summary>
    public static readonly RangeRegimeDiagnosticCard Empty = new(0, IndicatorDefaultConstants.PeriodsPerYear, null, null, null, null, null, null, null, null);

    public RangeRegimeDiagnosticCard(
        int rangeBars,
        int periodsPerYear,
        decimal? sharpe,
        decimal? sortino,
        decimal? cvar,
        decimal? cagr,
        decimal? hurstRescaledRange,
        decimal? hurstDfa,
        decimal? adfTau,
        decimal? kpss)
    {
        RangeBars = rangeBars;
        PeriodsPerYear = periodsPerYear;
        Sharpe = sharpe;
        Sortino = sortino;
        Cvar = cvar;
        Cagr = cagr;
        HurstRescaledRange = hurstRescaledRange;
        HurstDfa = hurstDfa;
        AdfTau = adfTau;
        Kpss = kpss;
    }

    /// <summary>The number of bars between the two anchors (both ends included); 0 when the range has no bar.</summary>
    public int RangeBars { get; }

    /// <summary>The bars per year the annualised values (Sharpe, Sortino, CAGR) use.</summary>
    public int PeriodsPerYear { get; }

    /// <summary>Annualised Sharpe ratio of the simple returns (risk-free rate 0), dimensionless.</summary>
    public decimal? Sharpe { get; }

    /// <summary>Annualised Sortino ratio of the simple returns (target 0), dimensionless.</summary>
    public decimal? Sortino { get; }

    /// <summary>Empirical CVaR at <see cref="RangeRegimeDiagnosticCardValues.CvarConfidence"/> as a positive loss ratio (0.03 = 3 % of the value).</summary>
    public decimal? Cvar { get; }

    /// <summary>Compound annual growth rate as a ratio (0.12 = 12 % per year); null for a range shorter than <see cref="RangeRegimeDiagnosticCardValues.MinCagrYears"/> years.</summary>
    public decimal? Cagr { get; }

    /// <summary>Hurst exponent by rescaled-range analysis of the simple returns, in [0, 1].</summary>
    public decimal? HurstRescaledRange { get; }

    /// <summary>Hurst exponent by detrended fluctuation analysis of the simple returns, in [0, 2].</summary>
    public decimal? HurstDfa { get; }

    /// <summary>The augmented Dickey-Fuller statistic tau of the log closes (the lower, the stronger the evidence of mean reversion).</summary>
    public decimal? AdfTau { get; }

    /// <summary>The KPSS level-stationarity statistic of the closes (not negative; the higher, the stronger the evidence against stationarity).</summary>
    public decimal? Kpss { get; }

    /// <summary>True when the range has at least one bar.</summary>
    public bool HasRange => RangeBars > 0;
}

/// <summary>
/// The range regime diagnostic card (the single definition; the GUI object and the MCP drawing-values catalogue call it). Definitions (decision DX-9 = A):
/// the returns are the SIMPLE returns of the closes inside the range (<c>N - 1</c> of them, the bar before the range is never read); the annualisation is the card's
/// <c>PeriodsPerYear</c> (default 252, never derived from the chart time frame); the risk-free rate and the Sortino target are 0; CVaR is the empirical 95 % value of
/// <see cref="DslQuantAndInfoMath.EmpiricalCvar"/>; CAGR uses <c>years = (N - 1) / PeriodsPerYear</c> and is shown only from <see cref="MinCagrYears"/> years (DX-9b = C: a shorter
/// range would only amplify noise); Hurst (R/S) is <see cref="HurstMath.Exponent"/> of the returns; Hurst (DFA) is <see cref="HurstMath.DetrendedFluctuationExponent"/> of the returns (16 or more, not flat: the kernel answers 0.5 for a constant window);
/// ADF is the tau of <see cref="StationarityTestMath.TryAugmentedDickeyFuller"/> on the log closes with the indicator's default lag order; KPSS is
/// <see cref="ArimaMath.ComputeKpssStatistic"/> of the closes (8 or more, not flat: the kernel answers 0.0 instead of failing). The tool is independent of
/// <see cref="DatePriceRangeValues"/>, whose ten-value contract stays as it is.
/// </summary>
public static class RangeRegimeDiagnosticCardValues
{
    /// <summary>The confidence level of the CVaR row.</summary>
    public const decimal CvarConfidence = 0.95m;

    /// <summary>The shortest range (in years) whose CAGR is shown (DX-9b = C): a shorter range only amplifies noise when it is annualised.</summary>
    public const decimal MinCagrYears = 0.25m;

    /// <summary>The risk-free rate per bar of the Sharpe row.</summary>
    public const double RiskFreeRatePerBar = 0.0;

    /// <summary>The target return per bar of the Sortino row.</summary>
    public const decimal SortinoTarget = 0m;

    public const int MinPeriodsPerYear = IndicatorDefaultConstants.MinPeriodsPerYear;

    public const int MaxPeriodsPerYear = IndicatorDefaultConstants.MaxPeriodsPerYear;

    public const int DefaultPeriodsPerYear = IndicatorDefaultConstants.PeriodsPerYear;

    /// <summary>The fewest returns of the Hurst (DFA) row (the kernel's own limit).</summary>
    public const int MinDfaReturns = 16;

    /// <summary>The fewest closes of the ADF row (the smallest window of the ADF indicator).</summary>
    public const int MinAdfBars = IndicatorDefaultConstants.AdfStationarityMinPeriod;

    /// <summary>The fewest closes of the KPSS row (the smallest window of the KPSS indicator).</summary>
    public const int MinKpssBars = IndicatorDefaultConstants.KpssStationarityMinPeriod;

    private const string AnnualizationKey = "RegimeAnnualization";
    private const string SharpeKey = "RegimeSharpe";
    private const string SortinoKey = "RegimeSortino";
    private const string CvarKey = "RegimeCvar95";
    private const string CagrKey = "RegimeCagr";
    private const string HurstRsKey = "RegimeHurstRs";
    private const string HurstDfaKey = "RegimeHurstDfa";
    private const string AdfKey = "RegimeAdf";
    private const string KpssKey = "RegimeKpss";
    private const string RangeBarsKey = "RangeBars";
    private const string RatioFormat = "F3";
    private const string StatisticFormat = "F3";
    private const string PercentFormat = "P2";
    private const string Missing = "-";

    /// <summary>The header of the card ("Ann: 252/yr"): always shown, so a reader knows which bars-per-year the annualised rows use.</summary>
    public static string AnnualizationText(int periodsPerYear)
        => string.Create(DrawingValueFormat.Culture, $"Ann: {periodsPerYear}/yr");

    /// <summary>Calculates the card of the bars <paramref name="firstIndex"/> .. <paramref name="lastIndex"/> of <paramref name="candles"/> (oldest first; the closes are used).</summary>
    public static RangeRegimeDiagnosticCard Calculate(IReadOnlyList<CoreCandleData> candles, int firstIndex, int lastIndex, int periodsPerYear)
    {
        if (candles == null || firstIndex < 0 || lastIndex >= candles.Count || firstIndex > lastIndex)
        {
            return RangeRegimeDiagnosticCard.Empty;
        }

        int perYear = Math.Clamp(periodsPerYear, MinPeriodsPerYear, MaxPeriodsPerYear);
        int bars = lastIndex - firstIndex + 1;
        var closes = new decimal[bars];
        for (int i = 0; i < bars; i++)
        {
            closes[i] = candles[firstIndex + i].Close;
        }

        // The simple returns; null when a close that is not above zero would be a denominator.
        decimal[]? returns = SimpleReturns(closes);
        double[]? returnValues = returns is null ? null : ToDouble(returns);

        decimal? sharpe = null;
        decimal? sortino = null;
        decimal? cvar = null;
        decimal? hurstRs = null;
        decimal? hurstDfa = null;
        if (returns is not null && returnValues is not null)
        {
            if (DslFinanceMath.SharpeRatio(returnValues, RiskFreeRatePerBar, perYear) is { IsOk: true } sharpeResult)
            {
                sharpe = IndicatorCalculationHelper.ToDecimalOrNull(sharpeResult.Value);
            }

            if (DslFinanceMath.SortinoRatio(returns, SortinoTarget, perYear) is { IsOk: true } sortinoResult)
            {
                sortino = sortinoResult.Value;
            }

            if (DslQuantAndInfoMath.EmpiricalCvar(returns, CvarConfidence) is { IsOk: true } cvarResult)
            {
                cvar = cvarResult.Value;
            }

            hurstRs = IndicatorCalculationHelper.ToDecimalOrNull(HurstMath.Exponent(returnValues));
            // The kernel answers 0.5 (no scaling information) for a constant window; a card shows a dash for it, as for the rescaled-range row.
            if (returnValues.Length >= MinDfaReturns
                && WindowMoments.TryCompute(returnValues, FlatWindowRule.Returns, out WindowMoments returnMoments)
                && !returnMoments.IsFlat)
            {
                hurstDfa = IndicatorCalculationHelper.ToDecimalOrNull(HurstMath.DetrendedFluctuationExponent(returnValues));
            }
        }

        decimal years = (decimal)(bars - 1) / perYear;
        decimal? cagr = null;
        if (years >= MinCagrYears && DslFinanceMath.Cagr(closes[0], closes[bars - 1], years) is { IsOk: true } cagrResult)
        {
            cagr = cagrResult.Value;
        }

        return new RangeRegimeDiagnosticCard(bars, perYear, sharpe, sortino, cvar, cagr, hurstRs, hurstDfa, AdfTau(closes), KpssStatistic(closes));
    }

    /// <summary>The same card for a range given by its two anchors (the catalogue entry); an empty card when there are no bars or no bar inside the range.</summary>
    public static IReadOnlyList<DrawingCalculatedValue> Calculate(DrawingAnchor point1, DrawingAnchor point2, IReadOnlyList<CoreCandleData>? candles, int periodsPerYear, IndicatorColor color)
    {
        if (candles == null || !DrawingRangeBars.TryFind(candles, point1.Time, point2.Time, out int first, out int last))
        {
            return Array.Empty<DrawingCalculatedValue>();
        }

        return Calculate(Calculate(candles, first, last, periodsPerYear), color);
    }

    /// <summary>The Data-tab values of a card (independent of the evaluation time): the annualisation header, the eight statistics and the number of bars.</summary>
    public static IReadOnlyList<DrawingCalculatedValue> Calculate(RangeRegimeDiagnosticCard card, IndicatorColor color)
    {
        if (!card.HasRange)
        {
            return Array.Empty<DrawingCalculatedValue>();
        }

        var culture = DrawingValueFormat.Culture;
        return new[]
        {
            new DrawingCalculatedValue(AnnualizationKey, "Annualization", card.PeriodsPerYear, AnnualizationText(card.PeriodsPerYear), IndicatorColor.Gray),
            Row(SharpeKey, "Sharpe (ann.)", card.Sharpe, RatioFormat, color, culture),
            Row(SortinoKey, "Sortino (ann.)", card.Sortino, RatioFormat, color, culture),
            PercentRow(CvarKey, "CVaR 95% (loss)", card.Cvar, color, culture),
            PercentRow(CagrKey, "CAGR", card.Cagr, color, culture),
            Row(HurstRsKey, "Hurst (R/S)", card.HurstRescaledRange, StatisticFormat, color, culture),
            Row(HurstDfaKey, "Hurst (DFA)", card.HurstDfa, StatisticFormat, color, culture),
            Row(AdfKey, "ADF tau (log price)", card.AdfTau, StatisticFormat, color, culture),
            Row(KpssKey, "KPSS (level)", card.Kpss, StatisticFormat, color, culture),
            new DrawingCalculatedValue(RangeBarsKey, "Range Bars", card.RangeBars, card.RangeBars.ToString(culture), IndicatorColor.Gray),
        };
    }

    /// <summary>The lines of the card as drawn on the chart: "Label  value" per row, in the order of the values.</summary>
    public static string[] Lines(RangeRegimeDiagnosticCard card)
    {
        IReadOnlyList<DrawingCalculatedValue> rows = Calculate(card, IndicatorColor.Gray);
        var lines = new string[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            lines[i] = string.Create(DrawingValueFormat.Culture, $"{rows[i].Label}: {rows[i].FormattedText}");
        }

        return lines;
    }

    private static decimal[]? SimpleReturns(decimal[] closes)
    {
        if (closes.Length < 2)
        {
            return Array.Empty<decimal>();
        }

        var returns = new decimal[closes.Length - 1];
        for (int i = 0; i < returns.Length; i++)
        {
            decimal previous = closes[i];
            if (previous <= 0m)
            {
                return null;
            }

            returns[i] = (closes[i + 1] - previous) / previous;
        }

        return returns;
    }

    private static double[] ToDouble(decimal[] values)
    {
        var result = new double[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            result[i] = (double)values[i];
        }

        return result;
    }

    private static decimal? AdfTau(decimal[] closes)
    {
        const int lagOrder = IndicatorDefaultConstants.AdfStationarityDefaultLagOrder;
        if (closes.Length < Math.Max(MinAdfBars, StationarityTestMath.MinWindowLength(lagOrder)))
        {
            return null;
        }

        var levels = new double[closes.Length];
        for (int i = 0; i < closes.Length; i++)
        {
            if (closes[i] <= 0m)
            {
                return null;
            }

            levels[i] = Math.Log((double)closes[i]);
        }

        var scratch = new double[StationarityTestMath.ScratchLength(levels.Length, lagOrder)];
        return StationarityTestMath.TryAugmentedDickeyFuller(levels, lagOrder, IndicatorDefaultConstants.HalfLifeLogEpsilon, scratch, out AugmentedDickeyFullerResult result)
            ? IndicatorCalculationHelper.ToDecimalOrNull(result.Tau)
            : null;
    }

    private static decimal? KpssStatistic(decimal[] closes)
    {
        if (closes.Length < MinKpssBars)
        {
            return null;
        }

        double[] levels = ToDouble(closes);
        // The kernel answers 0.0 for a window it cannot test: guard it as the KPSS indicator does (not flat, then the statistic).
        if (!WindowMoments.TryCompute(levels, FlatWindowRule.Series, out WindowMoments moments) || moments.IsFlat)
        {
            return null;
        }

        return IndicatorCalculationHelper.ToDecimalOrNull(ArimaMath.ComputeKpssStatistic(levels));
    }

    private static DrawingCalculatedValue Row(string key, string label, decimal? value, string format, IndicatorColor color, System.Globalization.CultureInfo culture)
        => new(key, label, value, value.HasValue ? value.Value.ToString(format, culture) : Missing, color);

    private static DrawingCalculatedValue PercentRow(string key, string label, decimal? value, IndicatorColor color, System.Globalization.CultureInfo culture)
        => new(key, label, value, value.HasValue ? value.Value.ToString(PercentFormat, culture) : Missing, color);
}
