using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Screener;

namespace StockAnalyzer.Core.Services.Screener;

/// <summary>
/// The time frames a Screener condition reads. A side that reads bars (an indicator, a price, a Criteria pattern or a Column that names a bar value) reads
/// the bars of its own <see cref="ScreenerIndicatorSideConfig.TimeFrame"/>; every other side (a fundamental Column) has no time frame.
/// The one definition behind the bar loading, the scan routing and the template routing.
/// </summary>
public static class ScreenerSideFrames
{
    /// <summary>True when <paramref name="side"/> reads bars (and therefore has a time frame): Indicator and Criteria always, a Column only when it names a bar value.</summary>
    public static bool ReadsBars(ScreenerIndicatorSideConfig? side)
        => side != null && side.CategoryType switch
        {
            ScreenerItemCategoryType.Indicator => true,
            ScreenerItemCategoryType.Criteria => true,
            ScreenerItemCategoryType.Column => ScreenerValueExtractor.IsCandleValueColumn(side),
            _ => false
        };

    /// <summary>The time frame whose bars <paramref name="side"/> reads: its own when it reads bars, D1 otherwise.</summary>
    public static TimeFrame FrameOf(ScreenerIndicatorSideConfig side)
    {
        ArgumentNullException.ThrowIfNull(side);
        return ReadsBars(side) ? side.TimeFrame : TimeFrame.D1;
    }

    /// <summary>
    /// The distinct time frames <paramref name="condition"/> reads, always including D1, in the order of <see cref="DataServiceTimeFrames.Supported"/>;
    /// a frame outside it follows, in enum order, so the caller can refuse it. A condition entry given directly counts whether or not it is enabled (it is
    /// evaluated as given); in a tree only the enabled leaves count, as in the evaluation. Any other condition type reads D1 only.
    /// </summary>
    public static IReadOnlyList<TimeFrame> Collect(IScreeningCondition? condition)
    {
        var frames = new HashSet<TimeFrame> { TimeFrame.D1 };
        switch (condition)
        {
            case ScreenerIndicatorEntry entry:
                AddFrames(entry, frames);
                break;
            case ScreenerConditionTreeCondition tree:
                AddFrames(tree.Root, frames);
                break;
        }

        return Ordered(frames);
    }

    /// <summary>The frames of the ENABLED entries of a template (the entries a template evaluation runs), D1 included, ordered as <see cref="Collect(IScreeningCondition)"/>.</summary>
    public static IReadOnlyList<TimeFrame> Collect(IEnumerable<ScreenerIndicatorEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var frames = new HashSet<TimeFrame> { TimeFrame.D1 };
        foreach (ScreenerIndicatorEntry entry in entries.Where(e => e.IsEnabled))
        {
            AddFrames(entry, frames);
        }

        return Ordered(frames);
    }

    /// <summary>True when <paramref name="condition"/> reads any time frame other than D1.</summary>
    public static bool UsesNonDailyBars(IScreeningCondition? condition) => Collect(condition).Count > 1;

    /// <summary>True when any ENABLED entry of <paramref name="entries"/> reads a time frame other than D1.</summary>
    public static bool UsesNonDailyBars(IEnumerable<ScreenerIndicatorEntry> entries) => Collect(entries).Count > 1;

    /// <summary>True when <paramref name="entry"/> alone reads a time frame other than D1 (enabled or not).</summary>
    public static bool UsesNonDailyBars(ScreenerIndicatorEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var frames = new HashSet<TimeFrame>();
        AddFrames(entry, frames);
        return frames.Any(frame => frame != TimeFrame.D1);
    }

    private static void AddFrames(IScreenerConditionNode node, HashSet<TimeFrame> frames)
    {
        switch (node)
        {
            case ScreenerConditionLeaf leaf:
                if (leaf.Entry.IsEnabled)
                {
                    AddFrames(leaf.Entry, frames);
                }
                break;
            case ScreenerConditionGroup group:
                for (int i = 0; i < group.Children.Length; i++)
                {
                    AddFrames(group.Children[i], frames);
                }
                break;
        }
    }

    // The right side is evaluated only when the entry compares against an indicator; the other target modes ignore it.
    private static void AddFrames(ScreenerIndicatorEntry entry, HashSet<TimeFrame> frames)
    {
        if (ReadsBars(entry.LeftHand))
        {
            frames.Add(entry.LeftHand.TimeFrame);
        }

        if (entry.TargetMode == RightHandTargetMode.Indicator && ReadsBars(entry.RightHand))
        {
            frames.Add(entry.RightHand.TimeFrame);
        }
    }

    private static IReadOnlyList<TimeFrame> Ordered(HashSet<TimeFrame> frames)
    {
        var ordered = new List<TimeFrame>(frames.Count);
        foreach (TimeFrame supported in DataServiceTimeFrames.Supported)
        {
            if (frames.Contains(supported))
            {
                ordered.Add(supported);
            }
        }

        ordered.AddRange(frames.Where(frame => !DataServiceTimeFrames.Supported.Contains(frame)).OrderBy(frame => (int)frame));
        return ordered;
    }
}
