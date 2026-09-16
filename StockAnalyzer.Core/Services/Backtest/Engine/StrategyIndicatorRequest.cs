using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// One indicator a strategy needs precomputed by the engine's Gate G2 batch-prep pass (see
/// Y:\Temp\sa_ai_context_BacktestEngine_P1.md section 1, gate G2), before the bar loop starts.
/// <paramref name="Key"/> is the caller-chosen name the strategy later uses to look the series up
/// via <see cref="IndicatorSeriesSet.ValueAt"/> (e.g. "Fast", "Slow") — it is not the indicator's
/// display name and has no meaning to the engine beyond dictionary lookup.
/// </summary>
public readonly record struct StrategyIndicatorRequest(string Key, IndicatorType Type, CoreIndicatorParameterBase? Parameters);
