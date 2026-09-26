using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// One indicator a strategy needs precomputed by the engine's Gate G2 batch-prep pass (see
/// Y:\Temp\sa_ai_context_BacktestEngine_P1.md section 1, gate G2), before the bar loop starts.
/// <paramref name="Key"/> is the caller-chosen name the strategy later uses to look the series up
/// via <see cref="IndicatorSeriesSet.ValueAt"/> (e.g. "Fast", "Slow") — it is not the indicator's
/// display name and has no meaning to the engine beyond dictionary lookup.
/// <paramref name="Frame"/> is a trailing, optional safe-extension (added 2026-09-18, see
/// Y:\Temp\sa_implementation_plan_BacktestComparisonSignals.md section 3.2.4): null means "compute
/// against the run's own <see cref="BacktestInput.Frame"/>", the exact existing behavior. Every call
/// site that omits it keeps compiling and behaving identically. A non-null value requests the
/// indicator be computed against a different Frame's own bars (see
/// <see cref="BacktestInput.AdditionalTimeframeBars"/>) and aligned via <c>TimeframeAlignment</c>.
/// <paramref name="OutputName"/> is a second trailing, optional safe-extension (added 2026-09-18, see
/// Y:\Temp\sa_implementation_plan_BacktestOutputNameSupport.md, Task 6a): which named series of a
/// multi-series indicator's <see cref="IIndicatorResult"/> to extract (e.g. MACD's "Signal" line).
/// Defaults to <see cref="IndicatorResult.MainSeriesName"/>, so every existing call site that omits it
/// keeps compiling and behaving identically to before this field existed.
/// <paramref name="PriceSource"/> is a third trailing, optional safe-extension (SAで改善, see
/// Y:\Temp\sa_improvement_plan_BacktestPriceSourceConditionFix.md): null means "leave the created
/// indicator instance's own default PriceSource untouched", the exact existing behavior. A non-null
/// value is applied onto the created <c>CoreIndicatorBase</c> instance before <c>Calculate</c> runs,
/// mirroring <c>AnalysisPipelineService.SyncPriceSource</c>'s existing mechanism — without this, every
/// Backtest condition built on a "Price" catalog row (Open/High/Low/...) silently evaluated against
/// Close regardless of which row was actually selected.
/// </summary>
public readonly record struct StrategyIndicatorRequest(
    string Key,
    IndicatorType Type,
    CoreIndicatorParameterBase? Parameters,
    TimeFrame? Frame = null,
    string OutputName = IndicatorResult.MainSeriesName,
    PriceType? PriceSource = null,
    bool StrictOutputName = false);
