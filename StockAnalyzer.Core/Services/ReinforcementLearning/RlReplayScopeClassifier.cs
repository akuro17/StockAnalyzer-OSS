using System;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>Names how an external replay window relates to the agent's training source. Pure; no I/O.</summary>
public static class RlReplayScopeClassifier
{
    /// <param name="generationSymbol">The symbol of the agent's source manifest.</param>
    /// <param name="sourcePeriodStartInclusive">The agent's source period start (manifest <c>PeriodStartInclusive</c>).</param>
    /// <param name="sourcePeriodEndExclusive">The agent's source period end (manifest <c>PeriodEndExclusive</c>).</param>
    /// <param name="symbol">The replayed ticker; compared after trimming, ignoring case.</param>
    /// <param name="episodeFirstBar">Date of the first of the 192 bars.</param>
    /// <param name="episodeLastBar">Date of the last of the 192 bars.</param>
    public static RlReplayScope Classify(string generationSymbol, DateOnly sourcePeriodStartInclusive, DateOnly sourcePeriodEndExclusive,
        string symbol, DateOnly episodeFirstBar, DateOnly episodeLastBar)
    {
        ArgumentNullException.ThrowIfNull(generationSymbol);
        ArgumentNullException.ThrowIfNull(symbol);
        if (!string.Equals(symbol.Trim(), generationSymbol, StringComparison.OrdinalIgnoreCase)) return RlReplayScope.OtherInstrument;
        if (episodeLastBar < sourcePeriodStartInclusive) return RlReplayScope.SameInstrumentBeforeTraining;
        if (episodeFirstBar >= sourcePeriodEndExclusive) return RlReplayScope.SameInstrumentAfterTraining;
        return RlReplayScope.SameInstrumentOverlapsTraining;
    }

    /// <summary>
    /// Which parts of the agent's source period the episode overlaps: Train is [source start, <paramref name="validationStart"/>), Validation is
    /// [<paramref name="validationStart"/>, <paramref name="finalEvaluationStart"/>) and FinalEvaluation is [<paramref name="finalEvaluationStart"/>,
    /// source end). <see cref="RlSourceParts.None"/> for another ticker or a window outside the source period. A part is overlapped when any of the
    /// episode's dates lies inside it, so a window of any length across a boundary names both parts.
    /// </summary>
    /// <param name="validationStart">Date of the first bar of the validation part (bar index <c>floor(3N/5)</c> of the source).</param>
    /// <param name="finalEvaluationStart">Date of the first bar of the final-evaluation part (bar index <c>floor(4N/5)</c> of the source).</param>
    public static RlSourceParts OverlappingParts(string generationSymbol, DateOnly sourcePeriodStartInclusive, DateOnly validationStart,
        DateOnly finalEvaluationStart, DateOnly sourcePeriodEndExclusive, string symbol, DateOnly episodeFirstBar, DateOnly episodeLastBar)
    {
        if (Classify(generationSymbol, sourcePeriodStartInclusive, sourcePeriodEndExclusive, symbol, episodeFirstBar, episodeLastBar)
            != RlReplayScope.SameInstrumentOverlapsTraining) return RlSourceParts.None;
        bool Overlaps(DateOnly start, DateOnly endExclusive) => episodeFirstBar < endExclusive && episodeLastBar >= start;
        var parts = RlSourceParts.None;
        if (Overlaps(sourcePeriodStartInclusive, validationStart)) parts |= RlSourceParts.Train;
        if (Overlaps(validationStart, finalEvaluationStart)) parts |= RlSourceParts.Validation;
        if (Overlaps(finalEvaluationStart, sourcePeriodEndExclusive)) parts |= RlSourceParts.FinalEvaluation;
        return parts;
    }
}
