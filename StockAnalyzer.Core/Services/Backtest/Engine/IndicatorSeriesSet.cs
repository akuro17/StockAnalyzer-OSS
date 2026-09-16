using System.Collections.Generic;
using System.Collections.Immutable;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// Read-only view over the indicator series precomputed once (Gate G2) before the bar loop, keyed by
/// the <see cref="StrategyIndicatorRequest.Key"/> each strategy chose. Exposes only index-based lookup
/// so a strategy can read the current (<c>barIndex</c>) and previous (<c>barIndex-1</c>) value without
/// being able to reach past the caller-supplied index (no lookahead is possible through this API since
/// the caller — the engine — only ever passes the current bar's index).
/// </summary>
public sealed class IndicatorSeriesSet
{
    private readonly IReadOnlyDictionary<string, ImmutableArray<decimal?>> _series;

    internal IndicatorSeriesSet(IReadOnlyDictionary<string, ImmutableArray<decimal?>> series)
    {
        _series = series;
    }

    /// <summary>Returns null if <paramref name="barIndex"/> is out of range (e.g. barIndex-1 at bar 0).</summary>
    public decimal? ValueAt(string key, int barIndex)
    {
        if (!_series.TryGetValue(key, out ImmutableArray<decimal?> values))
        {
            throw new KeyNotFoundException($"Indicator key '{key}' was not declared via IBacktestStrategy.GetRequiredIndicators().");
        }
        if (barIndex < 0 || barIndex >= values.Length) return null;
        return values[barIndex];
    }
}
