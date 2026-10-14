using System;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>
/// Compares the continuity of the bars an external replay has shown with the continuity of the bars the agent was trained on. The agent's own
/// extremes are the only reference (same principle as <see cref="RlScalerRangeCheck"/>): no limit is invented, and nothing is reported unless the
/// shown bars go beyond what the agent saw in training. Stored local bars carry no verified split or missing-day information, so a finding says
/// "unlike the agent's training data", never "this is a split" or "a day is missing".
/// </summary>
public static class RlDataContinuity
{
    /// <summary>The largest calendar-day gap between consecutive bars and the largest absolute close-to-close change, each with the date of the later bar.</summary>
    public readonly record struct Profile(int LargestGapDays, DateOnly GapDate, decimal LargestChange, DateOnly ChangeDate);

    /// <param name="bars">Bars in chronological order. A previous close that is not positive contributes no change.</param>
    public static Profile Measure(ReadOnlySpan<CandleData> bars)
    {
        int gap = 0;
        decimal change = 0m;
        DateOnly gapDate = default, changeDate = default;
        for (int i = 1; i < bars.Length; i++)
        {
            var previous = DateOnly.FromDateTime(bars[i - 1].Timestamp);
            var current = DateOnly.FromDateTime(bars[i].Timestamp);
            int days = current.DayNumber - previous.DayNumber;
            if (days > gap) { gap = days; gapDate = current; }
            if (bars[i - 1].Close > 0m)
            {
                decimal relative = Math.Abs(bars[i].Close / bars[i - 1].Close - 1m);
                if (relative > change) { change = relative; changeDate = current; }
            }
        }
        return new Profile(gap, gapDate, change, changeDate);
    }

    /// <summary>One finding per kind whose value in <paramref name="shown"/> is strictly larger than the agent's own largest value in <paramref name="trained"/>.</summary>
    public static ImmutableArray<RlContinuityFinding> Find(Profile trained, Profile shown)
    {
        var findings = ImmutableArray.CreateBuilder<RlContinuityFinding>();
        if (shown.LargestGapDays > trained.LargestGapDays)
            findings.Add(new RlContinuityFinding(RlContinuityKind.CalendarGap, shown.LargestGapDays, trained.LargestGapDays, shown.GapDate));
        if (shown.LargestChange > trained.LargestChange)
            findings.Add(new RlContinuityFinding(RlContinuityKind.CloseChange, shown.LargestChange, trained.LargestChange, shown.ChangeDate));
        return findings.ToImmutable();
    }
}
