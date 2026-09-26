using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Trend;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

public enum BacktestIndicatorViolationReason
{
    None,

    /// <summary>The series value at bar t depends on bars after t (future data, whole-range statistics, or repainting).</summary>
    NonCausal,

    /// <summary>The indicator only works through <c>CalculateAsync</c>; the synchronous <c>Calculate</c> the engine uses returns an unsuccessful result.</summary>
    SynchronousCalculationUnsupported,
}

/// <summary>
/// Single source of truth for which indicator outputs the backtest must refuse.
///
/// Rule: an output series may be used in a backtest only if its value at bar t depends solely on bars &lt;= t. The PLOT position is irrelevant -
/// what matters is when the information could be known. Consequences: Ichimoku's Senkou spans are stored at their plot position
/// (<c>SenkouSpanA[t]</c> == the cloud currently drawn at t) and are allowed, while <c>ChikouSpan[t]</c> carries a FUTURE close and is refused
/// (use the Price indicator with an Offset instead, e.g. Close &gt; Close[Offset N]).
///
/// Consumed by the engine (<c>BacktestEngine.PrepareIndicators</c>), saved-configuration validation, the backtest indicator selection UI and the
/// L2 causality test - no other list of these indicators may exist.
/// </summary>
public static class BacktestIndicatorEligibility
{
    /// <summary>Whole indicators that are non-causal (every series): whole-range distributions and repainting pivots.</summary>
    public static IReadOnlyCollection<IndicatorType> NonCausalIndicatorTypes { get; } = new[]
    {
        IndicatorType.TimeAtPrice,
        IndicatorType.VolumeProfile,
        IndicatorType.ZigZag,
    }.ToFrozenSet();

    /// <summary>Single output series that are non-causal while the rest of the indicator is causal.</summary>
    public static IReadOnlyCollection<(IndicatorType Type, string OutputName)> NonCausalSeries { get; } = ImmutableArray.Create(
        (IndicatorType.Ichimoku, CoreIchimokuIndicator.ChikouSpanSeriesName));

    /// <summary>Indicators without a usable synchronous calculation path (Python/async-only or placeholder-only).</summary>
    public static IReadOnlyCollection<IndicatorType> SynchronousCalculationUnsupportedTypes { get; } = new[]
    {
        IndicatorType.Garch11,
        IndicatorType.ThreeLineBreakSignal,
    }.ToFrozenSet();

    /// <summary>Type-level checks first, then series-level. <paramref name="outputName"/> may be null/blank (= the indicator's main series, never blocked at series level).</summary>
    public static BacktestIndicatorViolationReason Check(IndicatorType type, string? outputName)
    {
        if (NonCausalIndicatorTypes.Contains(type)) return BacktestIndicatorViolationReason.NonCausal;
        if (SynchronousCalculationUnsupportedTypes.Contains(type)) return BacktestIndicatorViolationReason.SynchronousCalculationUnsupported;
        if (IsNonCausalSeries(type, outputName)) return BacktestIndicatorViolationReason.NonCausal;
        return BacktestIndicatorViolationReason.None;
    }

    /// <summary>
    /// True when this single output series carries future data although the rest of the indicator is causal (e.g. Ichimoku's ChikouSpan). Series-level rule only:
    /// it is also the rule other consumers of a causal series must follow (e.g. the analysis pipeline's dynamic-period driver), so it is defined once, here.
    /// A null/blank <paramref name="outputName"/> (= the main series) is never blocked at series level.
    /// </summary>
    public static bool IsNonCausalSeries(IndicatorType type, string? outputName)
        => !string.IsNullOrWhiteSpace(outputName)
           && NonCausalSeries.Any(s => s.Type == type && string.Equals(s.OutputName, outputName, StringComparison.OrdinalIgnoreCase));

    public static bool IsAllowed(IndicatorType type, string? outputName = null) => Check(type, outputName) == BacktestIndicatorViolationReason.None;

    /// <summary>True when the whole indicator is refused (used by the UI catalog; series-level blocks only hide that series).</summary>
    public static bool IsTypeBlocked(IndicatorType type)
        => NonCausalIndicatorTypes.Contains(type) || SynchronousCalculationUnsupportedTypes.Contains(type);

    /// <summary>English diagnostic used by exceptions and configuration-load errors.</summary>
    public static string Describe(IndicatorType type, string? outputName, BacktestIndicatorViolationReason reason)
    {
        string target = string.IsNullOrWhiteSpace(outputName) ? type.ToString() : $"{type}.{outputName}";
        return reason switch
        {
            BacktestIndicatorViolationReason.NonCausal =>
                $"'{target}' cannot be used in a backtest: its value at a bar depends on later bars (look-ahead).",
            BacktestIndicatorViolationReason.SynchronousCalculationUnsupported =>
                $"'{target}' cannot be used in a backtest: it has no usable synchronous calculation output.",
            _ => $"'{target}' is allowed.",
        };
    }
}
