using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services.Screener;

/// <summary>
/// The answer of a Screener scan in which each condition side reads its own time frame: the symbols that meet the condition (in the order of the scan input),
/// the scanned symbols with no daily bar at all, and one <see cref="ScreenerFrameReport"/> per weekly or monthly frame the condition reads.
/// </summary>
/// <param name="Matched">Symbols that meet the condition.</param>
/// <param name="SymbolsWithoutDailyBars">Scanned symbols with no daily bar in the selection (not evaluated), ordinal order.</param>
/// <param name="Frames">One report per frame other than D1 that the condition reads, in the order of <see cref="DataServiceTimeFrames.Supported"/>; empty when it reads D1 only.</param>
public sealed record ScreenerSideFramesResult(
    IReadOnlyList<string> Matched,
    IReadOnlyList<string> SymbolsWithoutDailyBars,
    IReadOnlyList<ScreenerFrameReport> Frames);

/// <summary>What a scan did with one weekly or monthly frame, for the scanned symbols only.</summary>
/// <param name="Frame">The frame (W1 or MN1).</param>
/// <param name="SymbolsWithoutBars">Scanned symbols with no bar in this frame (ordinal order); their sides on this frame have no value.</param>
/// <param name="PartialLastBarPeriodStarts">First day of every partial last period that was evaluated (<c>CompleteBarsOnly</c> false), distinct, ascending.</param>
/// <param name="SkippedPartialBarCount">Scanned symbols whose partial last period was left out (<c>CompleteBarsOnly</c> true).</param>
public sealed record ScreenerFrameReport(
    TimeFrame Frame,
    IReadOnlyList<string> SymbolsWithoutBars,
    IReadOnlyList<DateOnly> PartialLastBarPeriodStarts,
    int SkippedPartialBarCount);
