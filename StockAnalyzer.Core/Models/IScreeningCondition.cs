using System.Collections.Generic;

namespace StockAnalyzer.Core.Models;

/// <summary>
/// Defines a condition for screening stocks based on their candle data.
/// </summary>
public interface IScreeningCondition
{
    /// <summary>
    /// Checks if the condition is met for the given candle data.
    /// </summary>
    /// <param name="candles">A read-only list of candle data.</param>
    /// <returns>True if the condition is met, otherwise false.</returns>
    bool IsMet(IReadOnlyList<CandleData> candles);

    /// <summary>
    /// Checks if the condition is met for the given candle data and ticker metadata.
    /// </summary>
    bool IsMet(IReadOnlyList<CandleData> candles, Portfolio.TickerMetadata metadata)
    {
        return IsMet(candles);
    }

    /// <summary>
    /// Checks if the condition is met asynchronously with ticker metadata.
    /// </summary>
    System.Threading.Tasks.ValueTask<bool> IsMetAsync(IReadOnlyList<CandleData> candles, Portfolio.TickerMetadata metadata)
    {
        return IsMetAsync(candles);
    }

    /// <summary>
    /// Checks if the condition is met asynchronously with ticker metadata and the bars of the comparison symbols, for the indicators that compare two
    /// tickers. The default ignores <paramref name="secondary"/> and runs the metadata form, so an implementation that does not read secondary data
    /// needs no change.
    /// </summary>
    System.Threading.Tasks.ValueTask<bool> IsMetAsync(IReadOnlyList<CandleData> candles, Portfolio.TickerMetadata metadata, Screener.IScreenerSecondaryCandles? secondary)
    {
        return IsMetAsync(candles, metadata);
    }

    /// <summary>
    /// Checks if the condition is met when each side reads the bars of its own time frame from <paramref name="bars"/>. A condition that does not read the bars of
    /// more than one time frame cannot answer this, so the default refuses instead of falling back to the daily bars (which would give a wrong answer silently).
    /// </summary>
    System.Threading.Tasks.ValueTask<bool> IsMetOnBarsAsync(Services.Screener.IScreenerBarSet bars, Portfolio.TickerMetadata metadata, Screener.IScreenerSecondaryCandles? secondary = null)
    {
        throw new System.NotSupportedException($"{GetType().Name} does not evaluate sides on their own time frames.");
    }

    /// <summary>
    /// Checks if the condition is met asynchronously.
    /// The default implementation runs synchronously, but can be overridden
    /// for ML inference or other async operations to prevent ThreadPool starvation.
    /// </summary>
    /// <param name="candles">A read-only list of candle data.</param>
    /// <returns>A task representing the async operation containing true if the condition is met.</returns>
    System.Threading.Tasks.ValueTask<bool> IsMetAsync(IReadOnlyList<CandleData> candles)
    {
        return new System.Threading.Tasks.ValueTask<bool>(IsMet(candles));
    }
}
