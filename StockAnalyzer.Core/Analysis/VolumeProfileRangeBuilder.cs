using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Analysis;

public readonly record struct VolumeProfileRange(int StartIndex, int Count, bool IsPartial);

public static class VolumeProfileRangeBuilder
{
    public static void Build(
        IReadOnlyList<CoreCandleData> candles,
        int visibleStartIndex,
        int visibleCount,
        int period,
        List<VolumeProfileRange> destination)
    {
        if (destination == null) throw new ArgumentNullException(nameof(destination));
        destination.Clear();

        if (candles == null || candles.Count == 0 || visibleCount <= 0) return;
        if (visibleStartIndex >= candles.Count) return;

        int n = candles.Count;
        int clampedStart = Math.Max(0, visibleStartIndex);
        if (clampedStart >= n) return;

        // Anchor reversal (2026-09-16, user-approved quality-improvement change): Period now counts BACKWARD from the
        // END of the visible range instead of forward from its start, so the profile always reflects
        // the most recent bars currently on screen (e.g. current price) even when zoomed out to show
        // the entire history. See Y:\Temp\sa_improvement_plan_VolumeProfilePeriodAnchor.md. This
        // supersedes the original "First Profile Range"/"FixedBars Mode" spec locked in
        // sa_analysis_report_VolumeProfile_ImplementationContext.md P03/P04.
        int visibleEnd = Math.Min(n, clampedStart + visibleCount);
        int availableBackward = visibleEnd;
        int count = period > 0 ? Math.Min(period, availableBackward) : Math.Min(visibleCount, availableBackward);
        if (count <= 0) return;

        int startIndex = visibleEnd - count;
        destination.Add(new VolumeProfileRange(startIndex, count, false));
    }

    public static void Build(
        IReadOnlyList<CoreCandleData> candles,
        int visibleStartIndex,
        int visibleCount,
        TimeframeType timeframe,
        int period,
        List<VolumeProfileRange> destination)
    {
        Build(candles, visibleStartIndex, visibleCount, period, destination);
    }
}
