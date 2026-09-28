using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using StockAnalyzer.Core.Services.Screener;

namespace StockAnalyzer.Core.Models.Screener;

/// <summary>
/// Adapts a Filters condition tree (<see cref="IScreenerConditionNode"/>) to <see cref="IScreeningCondition"/>, so the
/// existing <c>IScreenerService.ScreenAsync(symbols, condition, timeFrame, progress, ct)</c> - with its parallel
/// per-symbol candle loading, metadata fetch and cancellation - can screen the WHOLE tree in one call, replacing the
/// previous per-entry loop that folded results with <c>HashSet.UnionWith</c>/<c>IntersectWith</c>
/// (Y:\Temp\sa_implementation_plan_ScreenerFilterConditionTree.md Task 3, decision A). No screening logic lives here:
/// evaluation is delegated to <see cref="ScreenerConditionTreeEvaluator.Evaluate"/>.
/// </summary>
public sealed class ScreenerConditionTreeCondition : IScreeningCondition
{
    public ScreenerConditionTreeCondition(IScreenerConditionNode root)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));
    }

    public IScreenerConditionNode Root { get; }

    public bool IsMet(IReadOnlyList<CandleData> candles) => IsMet(candles, default);

    public bool IsMet(IReadOnlyList<CandleData> candles, Portfolio.TickerMetadata metadata)
        => ScreenerConditionTreeEvaluator.Evaluate(Root, candles, metadata);

    public ValueTask<bool> IsMetAsync(IReadOnlyList<CandleData> candles, Portfolio.TickerMetadata metadata)
        => ValueTask.FromResult(IsMet(candles, metadata));
}
