using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Services.Screener;

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

    /// <summary>
    /// The scan of the overload above over the bars of a <see cref="ScreenerBarSelection"/>: the daily history is aggregated to the weekly or monthly frame the way the chart does
    /// (not read from stored weekly/monthly files), and a last period that has not ended is left out when <see cref="ScreenerBarSelection.CompleteBarsOnly"/>. The answer also names
    /// the symbols without a bar and the partial periods. A service that cannot do this refuses instead of scanning other bars.
    /// </summary>
    Task<ScreenerBarsResult> ScreenOnBarsAsync(
        List<string> symbols,
        IScreeningCondition condition,
        TimeFrame timeFrame,
        ScreenerBarSelection selection,
        IProgress<int> progress,
        CancellationToken ct)
        => throw new NotSupportedException($"{GetType().Name} does not scan the bars of a selection.");

    /// <summary>
    /// A scan in which each side of <paramref name="condition"/> reads the bars of its OWN time frame (<c>ScreenerIndicatorSideConfig.TimeFrame</c>), built from the daily history
    /// the way <see cref="ScreenOnBarsAsync"/> builds them. A condition whose sides all read D1 (and no as-of date) is the plain daily scan. The answer names, per weekly or
    /// monthly frame the condition reads, the symbols without a bar and the partial periods. A service that cannot do this refuses instead of scanning other bars.
    /// </summary>
    Task<ScreenerSideFramesResult> ScreenWithSideFramesAsync(
        List<string> symbols,
        IScreeningCondition condition,
        ScreenerBarSelection selection,
        IProgress<int> progress,
        CancellationToken ct)
        => throw new NotSupportedException($"{GetType().Name} does not scan sides on their own time frames.");

    /// <summary>
    /// The latest <paramref name="count"/> bars (0 = all) of one symbol that <see cref="ScreenOnBarsAsync"/> evaluates for the same <paramref name="selection"/>: the same daily-aggregated
    /// frame, date cut and partial-period rule. The Screener tab reads the values of its result columns from them. An empty list means the symbol has no bar in the selection.
    /// </summary>
    Task<IReadOnlyList<CandleData>> LoadScreenerBarsAsync(
        string symbol,
        TimeFrame timeFrame,
        ScreenerBarSelection selection,
        int count,
        CancellationToken ct)
        => throw new NotSupportedException($"{GetType().Name} does not read the bars of a selection.");
}
