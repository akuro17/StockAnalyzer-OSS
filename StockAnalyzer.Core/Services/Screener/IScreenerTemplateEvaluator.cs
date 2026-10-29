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

    /// <summary>
    /// The evaluation in which every side of every enabled entry reads the bars of its OWN time frame (<see cref="IScreenerService.ScreenWithSideFramesAsync"/>), built from the
    /// daily history. The entries are combined exactly as in the overloads above. An evaluator that cannot do this refuses instead of evaluating daily bars.
    /// </summary>
    Task<HashSet<string>> EvaluateWithSideFramesAsync(
        IReadOnlyList<string> symbols,
        ScreenerIndicatorTemplate template,
        ScreenerBarSelection selection,
        IProgress<int>? progress,
        CancellationToken ct)
        => throw new NotSupportedException($"{GetType().Name} does not evaluate sides on their own time frames.");

    /// <summary>
    /// <see cref="EvaluateWithSideFramesAsync"/> that also says what the scans did: the symbols that meet the template (in no particular order), the symbols with no daily bar,
    /// and one report per weekly or monthly frame an enabled entry reads (the reports of the entries merged: the symbol lists and partial periods united, the skipped count
    /// the largest of the entries, since every entry scans the same symbols). An evaluator that cannot do this refuses instead of evaluating daily bars.
    /// </summary>
    Task<ScreenerSideFramesResult> EvaluateWithSideFramesReportAsync(
        IReadOnlyList<string> symbols,
        ScreenerIndicatorTemplate template,
        ScreenerBarSelection selection,
        IProgress<int>? progress,
        CancellationToken ct)
        => throw new NotSupportedException($"{GetType().Name} does not evaluate sides on their own time frames.");
}
