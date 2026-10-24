using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Templates;

namespace StockAnalyzer.Core.Services.Screener;

/// <summary>
/// Evaluates a saved <see cref="ScreenerIndicatorTemplate"/> against a set of symbols, combining its
/// entries with the same And/Or convention as the Screener tab itself.
/// </summary>
public interface IScreenerTemplateEvaluator
{
    Task<HashSet<string>> EvaluateAsync(
        IReadOnlyList<string> symbols,
        ScreenerIndicatorTemplate template,
        TimeFrame timeFrame,
        IProgress<int>? progress,
        CancellationToken ct);

    /// <summary>
    /// The evaluation of the overload above with the bars of the comparison symbols, which the indicators that compare two tickers read. An evaluator
    /// that does not read them refuses them instead of ignoring them silently; null is the overload above.
    /// </summary>
    Task<HashSet<string>> EvaluateAsync(
        IReadOnlyList<string> symbols,
        ScreenerIndicatorTemplate template,
        TimeFrame timeFrame,
        Models.Screener.IScreenerSecondaryCandles? secondary,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        if (secondary != null)
        {
            throw new NotSupportedException($"{GetType().Name} does not evaluate indicators that compare two tickers.");
        }

        return EvaluateAsync(symbols, template, timeFrame, progress, ct);
    }
}
