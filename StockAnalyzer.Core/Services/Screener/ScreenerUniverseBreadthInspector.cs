using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Services.Analysis;

namespace StockAnalyzer.Core.Services.Screener;

/// <summary>
/// Single source of truth for inspecting Screener conditions/sides for <see cref="IUniverseBreadthIndicator"/>
/// usage and pre-fetching each distinct <see cref="UniverseBreadthKey"/> once per scan before evaluating symbols.
/// </summary>
public static class ScreenerUniverseBreadthInspector
{
    /// <summary>
    /// Returns the distinct <see cref="UniverseBreadthKey"/> pairs required by enabled evaluable sides of <paramref name="condition"/>.
    /// </summary>
    public static IReadOnlyList<UniverseBreadthKey> Inspect(
        IScreeningCondition? condition,
        TimeFrame defaultTimeFrame,
        bool sideFramesMode,
        DateOnly? endDate = null)
    {
        var sides = new List<ScreenerIndicatorSideConfig?>();
        switch (condition)
        {
            case ScreenerIndicatorEntry entry:
                GatherEntry(entry, sides);
                break;
            case ScreenerConditionTreeCondition tree:
                GatherNode(tree.Root, sides);
                break;
        }

        return InspectSides(sides, defaultTimeFrame, sideFramesMode, endDate);
    }

    /// <summary>
    /// Returns the distinct <see cref="UniverseBreadthKey"/> pairs required by enabled <paramref name="entries"/>.
    /// </summary>
    public static IReadOnlyList<UniverseBreadthKey> Inspect(
        IEnumerable<ScreenerIndicatorEntry>? entries,
        TimeFrame defaultTimeFrame,
        bool sideFramesMode,
        DateOnly? endDate = null)
    {
        if (entries == null)
        {
            return Array.Empty<UniverseBreadthKey>();
        }

        var sides = new List<ScreenerIndicatorSideConfig?>();
        foreach (ScreenerIndicatorEntry entry in entries)
        {
            GatherEntry(entry, sides);
        }

        return InspectSides(sides, defaultTimeFrame, sideFramesMode, endDate);
    }

    /// <summary>
    /// Returns the distinct <see cref="UniverseBreadthKey"/> pairs required by <paramref name="sides"/>.
    /// </summary>
    public static IReadOnlyList<UniverseBreadthKey> InspectSides(
        IEnumerable<ScreenerIndicatorSideConfig?>? sides,
        TimeFrame defaultTimeFrame,
        bool sideFramesMode,
        DateOnly? endDate = null)
    {
        if (sides == null)
        {
            return Array.Empty<UniverseBreadthKey>();
        }

        var keys = new List<UniverseBreadthKey>();
        var seen = new HashSet<UniverseBreadthKey>();

        foreach (ScreenerIndicatorSideConfig? side in sides)
        {
            if (TryGetKey(side, defaultTimeFrame, sideFramesMode, endDate, out UniverseBreadthKey key) && seen.Add(key))
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    /// <summary>
    /// Pre-fetches each uncached <see cref="UniverseBreadthKey"/> from <paramref name="provider"/> once
    /// and merges the results into an <see cref="IScreenerEvaluationContext"/> preserving existing lookups.
    /// </summary>
    public static async Task<IScreenerEvaluationContext?> ComputeContextAsync(
        IMarketBreadthSeriesProvider? provider,
        IReadOnlyCollection<UniverseBreadthKey> keys,
        IScreenerEvaluationContext? baseContext,
        TimeFrame? scanTimeFrame = null,
        CancellationToken cancellationToken = default)
    {
        DateOnly? keyEndDate = baseContext?.AsOfDate;
        if (keyEndDate == null && keys != null)
        {
            foreach (UniverseBreadthKey k in keys)
            {
                if (k.EndDate.HasValue)
                {
                    keyEndDate = k.EndDate;
                    break;
                }
            }
        }

        if (keys == null || keys.Count == 0 || provider == null)
        {
            return (scanTimeFrame.HasValue || keyEndDate.HasValue) && baseContext != null
                ? ScreenerEvaluationContext.Merge(baseContext, null, scanTimeFrame, keyEndDate)
                : baseContext;
        }

        Dictionary<UniverseBreadthKey, MarketBreadthResult?>? fetched = null;

        foreach (UniverseBreadthKey key in keys)
        {
            if (baseContext != null && baseContext.TryGetUniverseBreadth(key, out _))
            {
                continue;
            }

            if (fetched != null && fetched.ContainsKey(key))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            MarketBreadthResult? result = await provider
                .GetAsync(key.GroupId, key.TimeFrame, key.ToParameters(), cancellationToken)
                .ConfigureAwait(false);
            (fetched ??= new Dictionary<UniverseBreadthKey, MarketBreadthResult?>())[key] = result;
        }

        if (fetched == null
            && (!scanTimeFrame.HasValue || baseContext == null || baseContext.ScanTimeFrame == scanTimeFrame)
            && (!keyEndDate.HasValue || baseContext == null || baseContext.AsOfDate == keyEndDate))
        {
            return baseContext;
        }

        return ScreenerEvaluationContext.Merge(baseContext, fetched, scanTimeFrame, keyEndDate);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="side"/> represents a universe-breadth indicator with a valid non-empty
    /// <see cref="IUniverseBreadthIndicator.UniverseGroupId"/>.
    /// </summary>
    public static bool HasValidUniverseGroupId(ScreenerIndicatorSideConfig? side)
        => TryResolveBreadthIndicator(side, out _, out _);

    private static bool TryGetKey(
        ScreenerIndicatorSideConfig? side,
        TimeFrame defaultTimeFrame,
        bool sideFramesMode,
        DateOnly? endDate,
        out UniverseBreadthKey key)
    {
        key = default;
        if (!TryResolveBreadthIndicator(side, out IUniverseBreadthIndicator? breadthIndicator, out Guid groupId)
            || breadthIndicator == null
            || side == null)
        {
            return false;
        }

        TimeFrame targetFrame = sideFramesMode ? ScreenerSideFrames.FrameOf(side) : defaultTimeFrame;
        key = UniverseBreadthKey.From(groupId, targetFrame, breadthIndicator.BreadthParameters, endDate);
        return true;
    }

    private static bool TryResolveBreadthIndicator(
        ScreenerIndicatorSideConfig? side,
        out IUniverseBreadthIndicator? breadthIndicator,
        out Guid groupId)
    {
        breadthIndicator = null;
        groupId = Guid.Empty;
        if (side == null
            || side.CategoryType != ScreenerItemCategoryType.Indicator
            || !ScreenerIndicatorEligibility.IsUniverseBreadthType(side.IndicatorType)
            || !ScreenerIndicatorEligibility.IsAllowed(side.IndicatorType, side.OutputName))
        {
            return false;
        }

        ICoreIndicator? indicator = IndicatorFactory.CreateStatic(side.IndicatorType);
        if (indicator == null)
        {
            return false;
        }

        if (side.Parameters != null && side.Parameters.Count > 0)
        {
            CoreIndicatorParameterBase? parameterObject = ScreenerValueExtractor.Default.CreateConfiguredParameterObject(side.IndicatorType, side.Parameters);
            if (parameterObject != null)
            {
                try
                {
                    indicator.Configure(parameterObject);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    return false;
                }
            }
        }

        if (indicator is not IUniverseBreadthIndicator resolved
            || string.IsNullOrWhiteSpace(resolved.UniverseGroupId)
            || !Guid.TryParse(resolved.UniverseGroupId.Trim(), out groupId)
            || groupId == Guid.Empty)
        {
            return false;
        }

        breadthIndicator = resolved;
        return true;
    }

    private static void GatherNode(IScreenerConditionNode? node, List<ScreenerIndicatorSideConfig?> into)
    {
        switch (node)
        {
            case ScreenerConditionLeaf leaf:
                if (leaf.Entry.IsEnabled)
                {
                    GatherEntry(leaf.Entry, into);
                }
                break;
            case ScreenerConditionGroup group:
                for (int i = 0; i < group.Children.Length; i++)
                {
                    GatherNode(group.Children[i], into);
                }
                break;
        }
    }

    private static void GatherEntry(ScreenerIndicatorEntry entry, List<ScreenerIndicatorSideConfig?> into)
    {
        if (!entry.IsEnabled)
        {
            return;
        }

        into.Add(entry.LeftHand);
        if (entry.TargetMode == RightHandTargetMode.Indicator)
        {
            into.Add(entry.RightHand);
        }
    }
}
