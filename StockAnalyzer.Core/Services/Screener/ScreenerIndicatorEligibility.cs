using System.Collections.Concurrent;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Services.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Screener;

/// <summary>
/// The one entry point of the Screener for "may this indicator output be evaluated". Its rule is the one of <see cref="BacktestIndicatorEligibility"/> with ONE difference:
/// an indicator that compares two symbols (<see cref="ICrossTickerIndicator"/>) or reads precomputed universe breadth (<see cref="IUniverseBreadthIndicator"/>)
/// is not refused for being "synchronously unsupported". That refusal existed only because the L2 causality test calculates every indicator with default parameters,
/// which carry no comparison symbol or universe group, so those indicators could not be evaluated there and had to be listed; look-ahead types/series and any remaining
/// Python/placeholder types stay refused. The Backtest, the training export and the prediction validation keep using <see cref="BacktestIndicatorEligibility"/> and are untouched.
/// No type names are listed here: the capability is read from the indicator itself.
/// </summary>
public static class ScreenerIndicatorEligibility
{
    // Asked only when the Backtest rule says "synchronously unsupported", so an allowed type never reaches it and the hot path allocates nothing.
    private static readonly ConcurrentDictionary<IndicatorType, bool> TwoTickerTypes = new();
    private static readonly ConcurrentDictionary<IndicatorType, bool> UniverseBreadthTypes = new();

    /// <inheritdoc cref="BacktestIndicatorEligibility.Check(IndicatorType, string?)"/>
    public static BacktestIndicatorViolationReason Check(IndicatorType type, string? outputName)
    {
        BacktestIndicatorViolationReason reason = BacktestIndicatorEligibility.Check(type, outputName);
        if (reason == BacktestIndicatorViolationReason.SynchronousCalculationUnsupported && IsScreenerPrecomputedExternalType(type))
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
           && !(BacktestIndicatorEligibility.Check(type, null) == BacktestIndicatorViolationReason.SynchronousCalculationUnsupported && IsScreenerPrecomputedExternalType(type));


    /// <summary>
    /// True when the Screener evaluates <paramref name="type"/> although the Backtest refuses it (two-symbol indicators and universe-breadth indicators). A host that cannot hand
    /// an evaluation the comparison symbol's bars or precomputed universe breadth offers the catalog without those types (<c>IScreenerCatalogProvider.GetCatalogItems</c>).
    /// </summary>
    public static bool IsScreenerOnlyType(IndicatorType type)
        => BacktestIndicatorEligibility.IsTypeBlocked(type) && !IsTypeBlocked(type);

    /// <summary>
    /// True when <paramref name="type"/> is an indicator that compares two symbols (<see cref="ICrossTickerIndicator"/>). The one definition of "two-symbol type" of the Screener, the Scatter
    /// rules and the registration windows; worked out once per type.
    /// </summary>
    public static bool IsTwoTickerType(IndicatorType type)
        => TwoTickerTypes.GetOrAdd(type, static t => IndicatorFactory.CreateStatic(t) is ICrossTickerIndicator);

    /// <summary>
    /// True when <paramref name="type"/> is an indicator that consumes precomputed universe breadth (<see cref="IUniverseBreadthIndicator"/>); evaluated once per type.
    /// </summary>
    public static bool IsUniverseBreadthType(IndicatorType type)
        => UniverseBreadthTypes.GetOrAdd(type, static t => IndicatorFactory.CreateStatic(t) is IUniverseBreadthIndicator);

    private static bool IsScreenerPrecomputedExternalType(IndicatorType type)
        => IsTwoTickerType(type) || IsUniverseBreadthType(type);
}
