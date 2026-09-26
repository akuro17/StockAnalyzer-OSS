using System.Collections.Generic;
using System.Collections.Immutable;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// Read-only view over the indicator series precomputed once (Gate G2) before the bar loop, keyed by
/// the <see cref="StrategyIndicatorRequest.Key"/> each strategy chose. Exposes only index-based lookup.
/// The precompute covers the whole bar array, so the set handed to a strategy through
/// <see cref="StrategyContext"/> is bounded to that context's own bar (see <see cref="BoundedTo"/>):
/// the strategy can read the current bar and any earlier one, never a later one.
/// </summary>
public sealed class IndicatorSeriesSet
{
    /// <summary>Bound of an instance created directly (tests and helper callers): every in-range index stays readable.</summary>
    private const int Unbounded = int.MaxValue;

    private readonly IReadOnlyDictionary<string, ImmutableArray<decimal?>> _series;
    private readonly int _maxBarIndex;

    internal IndicatorSeriesSet(IReadOnlyDictionary<string, ImmutableArray<decimal?>> series)
        : this(series, Unbounded)
    {
    }

    private IndicatorSeriesSet(IReadOnlyDictionary<string, ImmutableArray<decimal?>> series, int maxBarIndex)
    {
        _series = series;
        _maxBarIndex = maxBarIndex;
    }

    /// <summary>True when no series was declared, so there is nothing a bound could protect.</summary>
    internal bool IsEmpty => _series.Count == 0;

    /// <summary>A view over the same immutable series that answers null for any index greater than <paramref name="maxBarIndex"/>.</summary>
    internal IndicatorSeriesSet BoundedTo(int maxBarIndex) => new(_series, maxBarIndex);

    /// <summary>One immutable view per bar (bar <c>i</c> bound to <c>i</c>), so a context a strategy retains never gains access to later bars.</summary>
    internal IndicatorSeriesSet[] CreateBarBoundedViews(int barCount)
    {
        var views = new IndicatorSeriesSet[barCount];
        for (int i = 0; i < views.Length; i++) views[i] = BoundedTo(i);
        return views;
    }

    /// <summary>Returns null if <paramref name="barIndex"/> is out of range (e.g. barIndex-1 at bar 0) or lies after this view's bound.</summary>
    public decimal? ValueAt(string key, int barIndex)
    {
        if (!_series.TryGetValue(key, out ImmutableArray<decimal?> values))
        {
            throw new KeyNotFoundException($"Indicator key '{key}' was not declared via IBacktestStrategy.GetRequiredIndicators().");
        }
        if (barIndex < 0 || barIndex > _maxBarIndex || barIndex >= values.Length) return null;
        return values[barIndex];
    }
}
