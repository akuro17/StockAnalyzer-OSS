using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;

namespace StockAnalyzer.Core.Services;

public interface IScreenerService
{
    Task<List<string>> ScreenAsync(
        ScreeningCriteria criteria,
        IProgress<int> progress,
        CancellationToken ct);

    Task<List<string>> ScreenAsync(
        List<string> symbols,
        IScreeningCondition condition,
        TimeFrame timeFrame,
        IProgress<int> progress,
        CancellationToken ct);

    /// <summary>
    /// The scan of the overload above with the bars of the comparison symbols, which the indicators that compare two tickers read. They are loaded once by
    /// the caller and shared by the evaluation of every symbol. A service that does not read them refuses them instead of ignoring them silently;
    /// null is the overload above.
    /// </summary>
    Task<List<string>> ScreenAsync(
        List<string> symbols,
        IScreeningCondition condition,
        TimeFrame timeFrame,
        Models.Screener.IScreenerSecondaryCandles? secondary,
        IProgress<int> progress,
        CancellationToken ct)
    {
        if (secondary != null)
        {
            throw new NotSupportedException($"{GetType().Name} does not evaluate indicators that compare two tickers.");
        }

        return ScreenAsync(symbols, condition, timeFrame, progress, ct);
    }
}
