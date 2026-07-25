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
