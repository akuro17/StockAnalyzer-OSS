using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Services.Screener;

/// <summary>
/// Which bars a Screener scan of the read-only gateway evaluates. <see cref="AsOfDate"/>: only the days dated on or before it are read (null = every stored day).
/// <see cref="CompleteBarsOnly"/> (weekly and monthly bars only): a last period that has not ended yet is not evaluated, the previous period is.
/// A period is partial when its last calendar day (the Sunday of the ISO week, the last day of the month) is after the reference date, which is the
/// <see cref="AsOfDate"/> or, when that is later than the last stored day or absent, the last stored day (<c>TimeFrameAggregator.IsPartialPeriod</c>, the rule of <c>sa_get_candles</c>).
/// </summary>
public sealed record ScreenerBarSelection(DateOnly? AsOfDate, bool CompleteBarsOnly)
{
    /// <summary>
    /// The <see cref="CompleteBarsOnly"/> a scan uses when nobody chose: the Screener tab's check box starts on it, the MCP <c>completeBarsOnly</c> argument defaults to it, and the
    /// Tickers-tab template filter (which has no such check box) always uses it.
    /// </summary>
    public const bool DefaultCompleteBarsOnly = true;
}

/// <summary>
/// The answer of a Screener scan over a <see cref="ScreenerBarSelection"/>: the symbols that meet the condition (in the order of the scan input for a condition, in no
/// particular order for a template), the scanned symbols that had no bar to evaluate, and what happened to partial last periods.
/// </summary>
/// <param name="Matched">Symbols that meet the condition on the last bar of the selection.</param>
/// <param name="SymbolsWithoutBars">Scanned symbols (canonical form, ordinal order) with no bar in the selection (not evaluated).</param>
/// <param name="PartialLastBarPeriodStarts">First day of every partial last period that was evaluated (selection with <c>CompleteBarsOnly</c> false), distinct, ascending; empty otherwise.</param>
/// <param name="SkippedPartialBarCount">Scanned symbols whose partial last period was left out (selection with <c>CompleteBarsOnly</c> true).</param>
public sealed record ScreenerBarsResult(
    IReadOnlyList<string> Matched,
    IReadOnlyList<string> SymbolsWithoutBars,
    IReadOnlyList<DateOnly> PartialLastBarPeriodStarts,
    int SkippedPartialBarCount);
