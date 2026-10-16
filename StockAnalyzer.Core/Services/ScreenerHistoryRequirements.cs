using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.ScreeningConditions;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Evaluates and determines the optimal candle history length required for screening conditions.
/// Differentiates full-history indicators (Anchored VWAP, OBV, ATH/ATL) from standard indicators (SMA, RSI, MACD).
/// </summary>
public static class ScreenerHistoryRequirements
{
    public const int DefaultStandardCandleCount = 500;

    /// <summary>
    /// Bars a Criteria pattern (candle pattern, harmonic, Elliott, market structure, ...) is evaluated on: the window ending at the target bar.
    /// An algorithm constant of the pattern detectors, not a user tunable. Granville's law uses <c>GranvilleLawCondition.RequiredCandleCount()</c> instead.
    /// </summary>
    public const int CriteriaWindowBars = 100;

    /// <summary>
    /// The window (bars ending at the evaluated bar) the Criteria named <paramref name="criteriaName"/> is evaluated on: Granville's law needs its own
    /// warm-up window (<see cref="GranvilleLawCondition.RequiredCandleCount"/>) to have a signal on the last bar, every other pattern uses
    /// <see cref="CriteriaWindowBars"/>. The one definition behind the evaluation (<c>ScreenerValueExtractor</c>) and the history sizing.
    /// </summary>
    public static int GetCriteriaWindowBars(string? criteriaName)
        => criteriaName != null && criteriaName.Contains("Granville", StringComparison.OrdinalIgnoreCase)
            ? GranvilleLawCondition.RequiredCandleCount()
            : CriteriaWindowBars;

    /// <summary>
    /// Bars loaded on top of the indicator warm-up and the Offset; it equals the window of an ordinary Criteria pattern. It does not cover a longer window
    /// (Granville's law): <see cref="GetRequiredCandleCount"/> adds the Offset plus <see cref="GetCriteriaWindowBars"/> of every Criteria side separately.
    /// </summary>
    public const int HistoryMarginBars = CriteriaWindowBars;

    /// <summary>Period (in bars) assumed for a side that names no period parameter, and the floor of the largest-period search.</summary>
    public const int DefaultIndicatorPeriod = 20;

    /// <summary>Period (in bars) assumed for a condition type that does not declare its periods; deliberately generous so the history is never short.</summary>
    public const int UnknownConditionPeriod = 200;

    /// <summary>A numeric parameter at or above this value is not read as a period (it is a price level, a volume, an id ...).</summary>
    public const int PeriodParameterExclusiveUpperBound = 10000;

    /// <summary>
    /// Gets the minimum candle count required for screening evaluation.
    /// Returns 0 if full available history is required (e.g. AnchoredVWAP, OBV, AllTimeHigh).
    /// </summary>
    public static int GetRequiredCandleCount(IScreeningCondition? condition)
    {
        if (condition == null) return DefaultStandardCandleCount;

        if (RequiresFullHistory(condition))
        {
            return 0; // 0 indicates load all available historical candles
        }

        int maxPeriod = GetMaxPeriod(condition);
        int maxOffset = GetMaxOffset(condition);

        // Warm-up bars of a recursive indicator: the same multiplier the indicators use for their own overlap.
        int required = (maxPeriod * IndicatorDefaultConstants.EmaConvergenceMultiplier) + maxOffset + HistoryMarginBars;

        // A Criteria needs its whole window behind the bar it evaluates, whatever the margin above is (Granville's window is longer than the margin).
        return Math.Max(DefaultStandardCandleCount, Math.Max(required, GetMaxCriteriaReach(condition)));
    }

    /// <summary>
    /// Checks whether the condition includes indicators requiring full available historical data.
    /// </summary>
    public static bool RequiresFullHistory(IScreeningCondition condition)
    {
        if (condition is ScreenerIndicatorEntry entry)
        {
            return CheckSideRequiresFullHistory(entry.LeftHand) ||
                   (entry.TargetMode == RightHandTargetMode.Indicator && CheckSideRequiresFullHistory(entry.RightHand));
        }

        if (condition is BundledSignalCondition bundle && bundle.Conditions != null)
        {
            return bundle.Conditions.Any(c => RequiresFullHistory(c));
        }

        if (condition is ScreenerConditionTreeCondition treeCondition)
        {
            return EnumerateEntries(treeCondition.Root).Any(RequiresFullHistory);
        }

        return false;
    }

    private static bool CheckSideRequiresFullHistory(ScreenerIndicatorSideConfig? side)
    {
        if (side == null) return false;

        var type = side.IndicatorType;
        if (type == IndicatorType.AnchoredVWAP ||
            type == IndicatorType.OBV ||
            type == IndicatorType.ADL ||
            type == IndicatorType.ZigZag)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(side.CustomDisplayName))
        {
            string name = side.CustomDisplayName;
            if (name.Contains("AllTime", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("ATH", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("ATL", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int GetMaxPeriod(IScreeningCondition condition)
    {
        if (condition is ScreenerIndicatorEntry entry)
        {
            int leftPeriod = ExtractPeriodFromSide(entry.LeftHand);
            int rightPeriod = entry.TargetMode == RightHandTargetMode.Indicator ? ExtractPeriodFromSide(entry.RightHand) : 0;
            return Math.Max(leftPeriod, rightPeriod);
        }

        if (condition is BundledSignalCondition bundle && bundle.Conditions != null)
        {
            return bundle.Conditions.Select(GetMaxPeriod).DefaultIfEmpty(0).Max();
        }

        if (condition is ScreenerConditionTreeCondition treeCondition)
        {
            return EnumerateEntries(treeCondition.Root).Select(GetMaxPeriod).DefaultIfEmpty(0).Max();
        }

        return UnknownConditionPeriod;
    }

    /// <summary>
    /// The largest Offset any side of <paramref name="condition"/> carries (0 for none): the same figure the history sizing uses, so the Offset contract
    /// (<c>Screener:MaxConditionOffset</c>) and the number of bars loaded are judged from one value.
    /// </summary>
    public static int GetMaxConditionOffset(IScreeningCondition? condition)
        => condition == null ? 0 : GetMaxOffset(condition);

    /// <summary>
    /// The most bars a Criteria side of <paramref name="condition"/> reaches back from the newest bar: its Offset plus its window (0 when there is none).
    /// A right side counts only for an indicator comparison, the only mode in which it is evaluated.
    /// </summary>
    private static int GetMaxCriteriaReach(IScreeningCondition condition)
    {
        if (condition is ScreenerIndicatorEntry entry)
        {
            int left = GetCriteriaReach(entry.LeftHand);
            int right = entry.TargetMode == RightHandTargetMode.Indicator ? GetCriteriaReach(entry.RightHand) : 0;
            return Math.Max(left, right);
        }

        if (condition is BundledSignalCondition bundle && bundle.Conditions != null)
        {
            return bundle.Conditions.Select(GetMaxCriteriaReach).DefaultIfEmpty(0).Max();
        }

        if (condition is ScreenerConditionTreeCondition treeCondition)
        {
            return EnumerateEntries(treeCondition.Root).Select(GetMaxCriteriaReach).DefaultIfEmpty(0).Max();
        }

        return 0;
    }

    private static int GetCriteriaReach(ScreenerIndicatorSideConfig? side)
        => side != null && side.CategoryType == ScreenerItemCategoryType.Criteria
            ? side.Offset + GetCriteriaWindowBars(side.ResolveLookupName() ?? string.Empty)
            : 0;

    private static int GetMaxOffset(IScreeningCondition condition)
    {
        if (condition is ScreenerIndicatorEntry entry)
        {
            int leftOffset = entry.LeftHand?.Offset ?? 0;
            int rightOffset = entry.RightHand?.Offset ?? 0;
            return Math.Max(leftOffset, rightOffset);
        }

        if (condition is BundledSignalCondition bundle && bundle.Conditions != null)
        {
            return bundle.Conditions.Select(GetMaxOffset).DefaultIfEmpty(0).Max();
        }

        if (condition is ScreenerConditionTreeCondition treeCondition)
        {
            return EnumerateEntries(treeCondition.Root).Select(GetMaxOffset).DefaultIfEmpty(0).Max();
        }

        return 0;
    }

    /// <summary>Every <see cref="ScreenerIndicatorEntry"/> leaf reachable from <paramref name="node"/>, including disabled ones -
    /// the required-candle-count estimate is intentionally conservative (sized for whatever is currently in the tree, not only
    /// the enabled subset), so toggling a Filter back on never needs a re-fetch with a longer history mid-session.</summary>
    private static IEnumerable<ScreenerIndicatorEntry> EnumerateEntries(IScreenerConditionNode node)
    {
        switch (node)
        {
            case ScreenerConditionLeaf leaf:
                yield return leaf.Entry;
                break;
            case ScreenerConditionGroup group:
                foreach (IScreenerConditionNode child in group.Children)
                {
                    foreach (ScreenerIndicatorEntry entry in EnumerateEntries(child))
                    {
                        yield return entry;
                    }
                }
                break;
        }
    }

    private static int ExtractPeriodFromSide(ScreenerIndicatorSideConfig? side)
    {
        if (side?.Parameters == null) return DefaultIndicatorPeriod;

        int maxP = DefaultIndicatorPeriod;
        foreach (var val in side.Parameters.Values)
        {
            if (val is int iVal && iVal > maxP && iVal < PeriodParameterExclusiveUpperBound)
            {
                maxP = iVal;
            }
            else if (val is long lVal && lVal > maxP && lVal < PeriodParameterExclusiveUpperBound)
            {
                maxP = (int)lVal;
            }
            else if (val is JsonElement { ValueKind: JsonValueKind.Number } jsonVal
                     && jsonVal.TryGetInt64(out long jVal) && jVal > maxP && jVal < PeriodParameterExclusiveUpperBound)
            {
                // Parameters of a template loaded from disk arrive as JsonElement; same bounds as the int/long branches.
                maxP = (int)jVal;
            }
        }
        return maxP;
    }
}
