namespace StockAnalyzer.Core.Services.Backtest.Optimization;

/// <summary>How a walk-forward window ended; the names are the status texts the MCP tool writes.</summary>
public enum WalkForwardWindowStatus
{
    /// <summary>A best combination exists and the out-of-sample part has stored bars.</summary>
    Evaluated,

    /// <summary>The in-sample part holds no stored bar.</summary>
    NoInSampleBars,

    /// <summary>No combination made the required number of in-sample trades.</summary>
    NoInSampleTrades,

    /// <summary>Some combination traded enough, but none has a Valid objective.</summary>
    NoValidInSampleObjective,

    /// <summary>The best combination exists but the out-of-sample part holds no stored bar.</summary>
    NoOutOfSampleBars,
}

/// <summary>
/// What a window needs to know about one combination run on its in-sample part. <paramref name="TotalTrades"/> is null when the run produced no report;
/// <paramref name="ValidObjective"/> is the objective when its status is Valid, otherwise null (before the trade requirement is applied).
/// </summary>
public readonly record struct InSampleOutcome(bool NoEvaluatedBars, int? TotalTrades, decimal? ValidObjective);

/// <summary>The best combination of a window (index into the outcomes, -1 when there is none), its objective and the in-sample status.</summary>
public readonly record struct WalkForwardWindowSelection(WalkForwardWindowStatus Status, int BestIndex, decimal? BestObjective);

/// <summary>Chooses the best in-sample combination of a walk-forward window (pure function, no I/O).</summary>
public static class WalkForwardWindowSelector
{
    /// <summary>
    /// Only a combination with at least <paramref name="minInSampleTrades"/> trades can be the best one: with no trade its objective (for example a
    /// total return of 0) says nothing about the strategy. Among the eligible combinations the one with the best Valid objective wins, equal values by
    /// lower index. The status is <see cref="WalkForwardWindowStatus.Evaluated"/> with a best combination, otherwise NoInSampleBars (no run evaluated a
    /// bar), NoInSampleTrades (none was eligible) or NoValidInSampleObjective (some were eligible, none has a Valid objective).
    /// </summary>
    public static WalkForwardWindowSelection Select(IReadOnlyList<InSampleOutcome> outcomes, bool minimize, int minInSampleTrades)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        decimal?[] eligibleValues = outcomes.Select(outcome => IsEligible(outcome, minInSampleTrades) ? outcome.ValidObjective : null).ToArray();
        bool noBars = outcomes.All(outcome => outcome.NoEvaluatedBars);
        int best = noBars ? -1 : OptimizationRanking.Rank(eligibleValues, minimize).FirstOrDefault(-1);
        if (best >= 0)
        {
            return new WalkForwardWindowSelection(WalkForwardWindowStatus.Evaluated, best, eligibleValues[best]);
        }

        WalkForwardWindowStatus status = noBars
            ? WalkForwardWindowStatus.NoInSampleBars
            : outcomes.Any(outcome => IsEligible(outcome, minInSampleTrades))
                ? WalkForwardWindowStatus.NoValidInSampleObjective
                : WalkForwardWindowStatus.NoInSampleTrades;
        return new WalkForwardWindowSelection(status, -1, null);
    }

    private static bool IsEligible(InSampleOutcome outcome, int minInSampleTrades) => outcome.TotalTrades is { } trades && trades >= minInSampleTrades;
}
