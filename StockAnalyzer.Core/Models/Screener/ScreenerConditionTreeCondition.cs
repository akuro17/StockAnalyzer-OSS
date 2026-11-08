using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using StockAnalyzer.Core.Services.Screener;

namespace StockAnalyzer.Core.Models.Screener;

/// <summary>
/// Adapts a Filters condition tree (<see cref="IScreenerConditionNode"/>) to <see cref="IScreeningCondition"/>, so the
/// existing <c>IScreenerService.ScreenAsync(symbols, condition, timeFrame, progress, ct)</c> - with its parallel
/// per-symbol candle loading, metadata fetch and cancellation - can screen the WHOLE tree in one call, replacing the
/// previous per-entry loop that folded results with <c>HashSet.UnionWith</c>/<c>IntersectWith</c>
/// (docs/sa_implementation_plan_ScreenerFilterConditionTree Task 3, decision A). No screening logic lives here:
/// evaluation is delegated to <see cref="ScreenerConditionTreeEvaluator.Evaluate"/>.
/// </summary>
public sealed class ScreenerConditionTreeCondition : IScreeningCondition
{
    private readonly Action<ScreenerIndicatorEntry> _recordLeafMet;

    public ScreenerConditionTreeCondition(IScreenerConditionNode root)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));
        _recordLeafMet = RecordLeafMet;
    }

    public IScreenerConditionNode Root { get; }

    /// <summary>
    /// Thread-safe accumulator recording the number of times each <see cref="ScreenerIndicatorEntry"/> was met during screening.
    /// </summary>
    public ConcurrentDictionary<ScreenerIndicatorEntry, int> EntryMatchCounts { get; } = new();

    private void RecordLeafMet(ScreenerIndicatorEntry entry)
    {
        EntryMatchCounts.AddOrUpdate(entry, 1, static (_, count) => count + 1);
    }

    public bool IsMet(IReadOnlyList<CandleData> candles) => IsMet(candles, default);

    public bool IsMet(IReadOnlyList<CandleData> candles, Portfolio.TickerMetadata metadata)
        => ScreenerConditionTreeEvaluator.Evaluate(Root, candles, metadata, null, _recordLeafMet);

    public ValueTask<bool> IsMetAsync(IReadOnlyList<CandleData> candles, Portfolio.TickerMetadata metadata)
        => ValueTask.FromResult(IsMet(candles, metadata));

    public ValueTask<bool> IsMetAsync(IReadOnlyList<CandleData> candles, Portfolio.TickerMetadata metadata, IScreenerSecondaryCandles? secondary)
        => ValueTask.FromResult(ScreenerConditionTreeEvaluator.Evaluate(Root, candles, metadata, secondary, _recordLeafMet));

    public ValueTask<bool> IsMetOnBarsAsync(IScreenerBarSet bars, Portfolio.TickerMetadata metadata, IScreenerSecondaryCandles? secondary = null)
        => ValueTask.FromResult(ScreenerConditionTreeEvaluator.EvaluateOnBars(Root, bars, metadata, secondary, _recordLeafMet));
}
