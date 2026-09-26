using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.Models.Backtest.Engine;

/// <summary>
/// One side (Left or Right) of a <see cref="BacktestConditionEntry"/> comparison — indicator-only
/// (no Column/Criteria concerns, unlike Screener's <c>ScreenerIndicatorSideConfig</c>), matching
/// <c>BacktestIndicatorSelectionViewModel</c>'s own Indicator-only restriction.
/// See Y:\Temp\sa_implementation_plan_BacktestComparisonSignals.md section 4.1.
/// </summary>
public sealed class BacktestConditionSide
{
    public IndicatorType IndicatorType { get; init; }

    public CoreIndicatorParameterBase? Parameters { get; init; }

    public string OutputName { get; init; } = IndicatorResult.MainSeriesName;

    /// <summary>Bars before the bar being evaluated (0 = the current bar itself).</summary>
    public int Offset { get; init; }

    /// <summary>Null means "same as the run's Frame" (matches <c>BacktestSelectedIndicatorDto.Frame</c>'s
    /// existing convention verbatim). Non-null triggers cross-timeframe alignment (plan section 3.2.4).</summary>
    public TimeFrame? Frame { get; init; }

    /// <summary>
    /// Safe extension (SAで改善, Y:\Temp\sa_improvement_plan_BacktestPriceSourceConditionFix.md): which raw
    /// OHLC-derived series this side reads. Null by default — meaningful only when <see cref="IndicatorType"/>
    /// is <see cref="IndicatorType.Price"/> (the catalog's "Price" group has one row per <see cref="Indicators.PriceType"/>
    /// value, e.g. "High"/"Low", all sharing <see cref="IndicatorType.Price"/> but distinguished only by this
    /// field). Mirrors the existing <c>CoreIndicatorSettings.PriceSource</c>/<c>AnalysisPipelineService.
    /// SyncPriceSource</c> mechanism the rest of the app already uses to apply a non-Close price source onto
    /// a created indicator instance — Backtest's condition builder previously had no field to carry this
    /// through at all, so every Price-type condition silently evaluated against Close regardless of which
    /// PriceType row was selected.
    /// </summary>
    public PriceType? PriceSource { get; init; }
}
