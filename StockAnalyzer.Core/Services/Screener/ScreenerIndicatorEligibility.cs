using System.Collections.Concurrent;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Services.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Screener;

/// <summary>
/// The one entry point of the Screener for "may this indicator output be evaluated". Its rule is the one of <see cref="BacktestIndicatorEligibility"/> with ONE difference:
/// an indicator that compares two symbols (<see cref="ICrossTickerIndicator"/>) is not refused for being "synchronously unsupported". That refusal existed only because the
/// L2 causality test calculates every indicator with default parameters, which carry no comparison symbol, so a two-symbol indicator could not be evaluated there and had to be
/// listed; the two-symbol L2 test (<c>L2_TwoTickerIndicatorCausalityTests</c>) now proves every such indicator causal in both symbols. A look-ahead type or series stays refused,
/// and so do the Python and placeholder types (they are not two-symbol indicators). The Backtest, the L2 test, the training export and the prediction validation keep using
/// <see cref="BacktestIndicatorEligibility"/> and are untouched. No type names are listed here: "compares two symbols" is read from the indicator itself.
/// </summary>
public static class ScreenerIndicatorEligibility
{
    // Asked only when the Backtest rule says "synchronously unsupported", so an allowed type never reaches it and the hot path allocates nothing.
    private static readonly ConcurrentDictionary<IndicatorType, bool> TwoTickerTypes = new();

    /// <inheritdoc cref="BacktestIndicatorEligibility.Check(IndicatorType, string?)"/>
    public static BacktestIndicatorViolationReason Check(IndicatorType type, string? outputName)
    {
        BacktestIndicatorViolationReason reason = BacktestIndicatorEligibility.Check(type, outputName);
        if (reason == BacktestIndicatorViolationReason.SynchronousCalculationUnsupported && IsTwoTickerType(type))
        {
            // The type-level refusal is lifted; the series-level (look-ahead) rule still applies.
            return BacktestIndicatorEligibility.IsNonCausalSeries(type, outputName)
                ? BacktestIndicatorViolationReason.NonCausal
                : BacktestIndicatorViolationReason.None;
        }

        return reason;
    }

    /// <inheritdoc cref="BacktestIndicatorEligibility.IsAllowed(IndicatorType, string?)"/>
    public static bool IsAllowed(IndicatorType type, string? outputName = null)
        => Check(type, outputName) == BacktestIndicatorViolationReason.None;

    /// <summary>True when the whole indicator is refused in the Screener (series-level blocks only hide that series).</summary>
    public static bool IsTypeBlocked(IndicatorType type)
        => BacktestIndicatorEligibility.IsTypeBlocked(type)
           && !(BacktestIndicatorEligibility.Check(type, null) == BacktestIndicatorViolationReason.SynchronousCalculationUnsupported && IsTwoTickerType(type));


    /// <summary>
    /// True when the Screener evaluates <paramref name="type"/> although the Backtest refuses it (the two-symbol indicators that need a comparison symbol). A host that cannot hand
    /// an evaluation the comparison symbol's bars offers exactly the catalog without these types (<c>IScreenerCatalogProvider.GetCatalogItems</c>).
    /// </summary>
    public static bool IsScreenerOnlyType(IndicatorType type)
        => BacktestIndicatorEligibility.IsTypeBlocked(type) && !IsTypeBlocked(type);
    /// <summary>
    /// True when <paramref name="type"/> is an indicator that compares two symbols (<see cref="ICrossTickerIndicator"/>). The one definition of "two-symbol type" of the Screener, the Scatter
    /// rules and the registration windows; worked out once per type.
    /// </summary>
    public static bool IsTwoTickerType(IndicatorType type)
        => TwoTickerTypes.GetOrAdd(type, static t => IndicatorFactory.CreateStatic(t) is ICrossTickerIndicator);
}
