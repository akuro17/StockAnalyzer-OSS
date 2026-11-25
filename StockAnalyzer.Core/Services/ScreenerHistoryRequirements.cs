using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.PatternFlags;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.ScreeningConditions;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Evaluates and determines the optimal candle history length required for screening conditions.
/// Differentiates full-history indicators (Anchored VWAP, a VWAP without a reset period, OBV, ATH/ATL) from standard indicators (SMA, RSI, MACD).
/// </summary>
public static class ScreenerHistoryRequirements
{
    public const int DefaultStandardCandleCount = 500;

    /// <summary>
    /// Bars a Criteria pattern (candle pattern, harmonic, Elliott, market structure, ...) is evaluated on: the window ending at the target bar.
    /// An algorithm constant of the pattern detectors, not a user tunable. Granville's law uses <c>GranvilleLawCondition.RequiredCandleCount()</c> instead.
    /// </summary>
    public const int CriteriaWindowBars = ChartConstants.PatternDetectionWindowBars;

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
        // Flag indicators also use GetMinimumBars directly (R-29) so high-offset or long-period conditions always cover N_min + offset without 5x inflation.
        return Math.Max(DefaultStandardCandleCount, Math.Max(required, Math.Max(GetMaxCriteriaReach(condition), GetMaxFlagReach(condition))));
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

        // A Ratiocator in NormalizedRatio mode takes its base (R_0) from the first valid bar it is given: with a history cut at a length that depends on the other
        // conditions of the scan, its value would depend on them. The whole stored history makes the value a property of the stored data alone.
        if (type == IndicatorType.Ratiocator && NamesNormalizedRatioMode(side.Parameters))
        {
            return true;
        }

        // A VWAP that never restarts (ResetPeriod Cumulative, the default) sums from the first bar it is given: the same reason, so it needs the whole history too.
        return type == IndicatorType.VWAP && !RestartsWithinTheHistory(side.Parameters);
    }

    private static readonly string RatiocatorModeParameterName = nameof(CoreRatiocatorParameter.CalculationMode);

    private static readonly string VwapResetPeriodParameterName = nameof(CoreVwapParameter.ResetPeriod);

    /// <summary>
    /// What <see cref="ReadLooseEnumParameter{TEnum}"/> found under a parameter name. The key is matched the way <see cref="ScreenerValueExtractor"/> matches it
    /// (exact, ordinal): a key that differs only in case is never applied to the indicator, so it is never trusted here.
    /// </summary>
    private enum LooseEnumRead
    {
        /// <summary>No key of that name in any case: the indicator keeps its own default.</summary>
        Absent,

        /// <summary>No key of that exact name, but a key that differs only in case: the extractor ignores it, so the side's real value is not known (answered as the safe side by the caller).</summary>
        VariantOnly,

        /// <summary>The exact key holds a value that is not a number of the enum (another type, a text that is no member name or number, a boolean).</summary>
        Unresolved,

        /// <summary>The exact key holds an enum value, a member name (any case, trimmed; a numeric text counts as the number), a JSON string or a whole number.</summary>
        Resolved,
    }

    /// <summary>
    /// Reads the enum-valued parameter <paramref name="name"/> in any form a side keeps it in: the enum value, its name (any case, trimmed; a numeric text counts as the
    /// number), a JSON string, or the number of the member (an integer, a JSON number, any other whole number). <paramref name="value"/> is the number of the member
    /// (compared as <see cref="long"/>, whether or not it is a defined member). Three situations are told apart: the exact key exists (its value alone decides, whatever
    /// case variants exist next to it), only case variants exist (<see cref="LooseEnumRead.VariantOnly"/>), or no key at all (<see cref="LooseEnumRead.Absent"/>).
    /// </summary>
    private static LooseEnumRead ReadLooseEnumParameter<TEnum>(IReadOnlyDictionary<string, object>? parameters, string name, out long value)
        where TEnum : struct, Enum
    {
        value = 0;
        if (parameters == null)
        {
            return LooseEnumRead.Absent;
        }

        bool variantSeen = false;
        foreach (KeyValuePair<string, object> parameter in parameters)
        {
            if (string.Equals(parameter.Key, name, StringComparison.Ordinal))
            {
                return ReadLooseEnumValue<TEnum>(parameter.Value, out value);
            }

            variantSeen |= string.Equals(parameter.Key, name, StringComparison.OrdinalIgnoreCase);
        }

        return variantSeen ? LooseEnumRead.VariantOnly : LooseEnumRead.Absent;
    }

    private static LooseEnumRead ReadLooseEnumValue<TEnum>(object rawValue, out long value)
        where TEnum : struct, Enum
    {
        value = 0;
        switch (rawValue)
        {
            case TEnum member:
                value = Convert.ToInt64(member, System.Globalization.CultureInfo.InvariantCulture);
                return LooseEnumRead.Resolved;
            case string text:
                return TryParseLooseEnumText<TEnum>(text, out value);
            case JsonElement { ValueKind: JsonValueKind.String } element:
                return TryParseLooseEnumText<TEnum>(element.GetString(), out value);
            case JsonElement { ValueKind: JsonValueKind.Number } element:
                return element.TryGetInt64(out value) ? LooseEnumRead.Resolved : LooseEnumRead.Unresolved;
            case bool:
                return LooseEnumRead.Unresolved;
            case IConvertible convertible:
                try
                {
                    decimal number = convertible.ToDecimal(System.Globalization.CultureInfo.InvariantCulture);
                    if (number != decimal.Truncate(number) || number < long.MinValue || number > long.MaxValue)
                    {
                        return LooseEnumRead.Unresolved;
                    }

                    value = (long)number;
                    return LooseEnumRead.Resolved;
                }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
                {
                    return LooseEnumRead.Unresolved;
                }
            default:
                return LooseEnumRead.Unresolved;
        }
    }

    private static LooseEnumRead TryParseLooseEnumText<TEnum>(string? text, out long value)
        where TEnum : struct, Enum
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text) || !Enum.TryParse(text.Trim(), ignoreCase: true, out TEnum member))
        {
            return LooseEnumRead.Unresolved;
        }

        value = Convert.ToInt64(member, System.Globalization.CultureInfo.InvariantCulture);
        return LooseEnumRead.Resolved;
    }

    /// <summary>
    /// True when the side needs the whole history because of a <c>CalculationMode</c> that means <see cref="RatiocatorCalculationMode.NormalizedRatio"/>, in any form a
    /// side keeps it in (see <see cref="ReadLooseEnumParameter{TEnum}"/>). The exact key decides: another mode, a value that is not a mode, or no key at all (the default is
    /// <see cref="RatiocatorCalculationMode.RollingRatio"/>) answer false. A key that differs only in case is not applied by the extractor and its value is not trusted: it answers true
    /// (a longer history is never wrong).
    /// </summary>
    private static bool NamesNormalizedRatioMode(IReadOnlyDictionary<string, object>? parameters)
    {
        LooseEnumRead read = ReadLooseEnumParameter<RatiocatorCalculationMode>(parameters, RatiocatorModeParameterName, out long member);
        return read == LooseEnumRead.VariantOnly
               || (read == LooseEnumRead.Resolved && member == (long)RatiocatorCalculationMode.NormalizedRatio);
    }

    /// <summary>
    /// True only when the exact <c>ResetPeriod</c> key holds a defined <see cref="VwapResetPeriod"/> member other than <see cref="VwapResetPeriod.Cumulative"/>
    /// (Weekly, Monthly, Quarterly, Yearly: the sums restart, so a history of the ordinary length is enough). No key at all means the default Cumulative; a key that differs only in
    /// case is not applied by the extractor (so the side stays Cumulative) and a value that is not a defined member is not trusted to restart: all of these answer false and the
    /// whole history is loaded (a longer history is never wrong).
    /// </summary>
    private static bool RestartsWithinTheHistory(IReadOnlyDictionary<string, object>? parameters)
    {
        if (ReadLooseEnumParameter<VwapResetPeriod>(parameters, VwapResetPeriodParameterName, out long member) != LooseEnumRead.Resolved)
        {
            return false;
        }

        foreach (VwapResetPeriod defined in Enum.GetValues<VwapResetPeriod>())
        {
            if (defined != VwapResetPeriod.Cumulative && (long)defined == member)
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
            ? side.Offset + GetCriteriaWindowBars(side.ResolveCriteriaName() ?? string.Empty)
            : 0;

    private static int GetMaxFlagReach(IScreeningCondition condition)
    {
        if (condition is ScreenerIndicatorEntry entry)
        {
            int left = GetFlagReach(entry.LeftHand);
            int right = entry.TargetMode == RightHandTargetMode.Indicator ? GetFlagReach(entry.RightHand) : 0;
            return Math.Max(left, right);
        }

        if (condition is BundledSignalCondition bundle && bundle.Conditions != null)
        {
            return bundle.Conditions.Select(GetMaxFlagReach).DefaultIfEmpty(0).Max();
        }

        if (condition is ScreenerConditionTreeCondition treeCondition)
        {
            return EnumerateEntries(treeCondition.Root).Select(GetMaxFlagReach).DefaultIfEmpty(0).Max();
        }

        return 0;
    }

    private static int GetFlagReach(ScreenerIndicatorSideConfig? side)
        => side != null && side.CategoryType == ScreenerItemCategoryType.Indicator && PatternFlagIndicatorCatalog.Contains(side.IndicatorType)
            ? GetMinimumBars(side.IndicatorType, side.Parameters, side.Offset)
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

    /// <summary>
    /// Gets the minimum bars required for a single indicator configuration.
    /// Pattern flag types (plan R-29) take an additive early branch returning <c>max(N_min, PatternFlagWarmupBars) + offset</c>
    /// so <c>WarmupBars = 500</c> is never multiplied by <c>EmaConvergenceMultiplier</c>.
    /// </summary>
    public static int GetMinimumBars(IndicatorType type, IReadOnlyDictionary<string, object>? parameters = null, int offset = 0)
    {
        if (PatternFlagIndicatorCatalog.TryGet(type, out PatternFlagSpec? flagSpec))
        {
            return ComputeFlagMinimumBars(flagSpec, parameters) + Math.Max(0, offset);
        }

        int warmup = ResolveIndicatorWarmupBars(type, parameters);
        return (warmup * IndicatorDefaultConstants.EmaConvergenceMultiplier) + offset + HistoryMarginBars;
    }

    private static int ComputeFlagMinimumBars(PatternFlagSpec flagSpec, IReadOnlyDictionary<string, object>? parameters)
    {
        CoreIndicatorParameterBase parameterObject = flagSpec.CreateDefaultParameter();
        ScreenerValueExtractor.TryApplyParametersBestEffort(parameterObject, parameters);
        return Math.Max(flagSpec.MinBars, Math.Max(parameterObject.GetRequiredWarmupBars(), PatternFlagParameterLimits.WarmupBars));
    }

    /// <summary>
    /// Resolves the warmup/lookback bars required for a given indicator type and its parameters.
    /// Single Source of Truth (SSoT) for indicator history sizing and multi-stage warmup calculations.
    /// </summary>
    public static int ResolveIndicatorWarmupBars(IndicatorType? type, IReadOnlyDictionary<string, object>? parameters)
    {
        if (!type.HasValue) return DefaultIndicatorPeriod;

        switch (type.Value)
        {
            case IndicatorType.Ichimoku:
                int senkouB = GetIntParameter(parameters, "SenkouBSample", "SenkouSpanBPeriod", 52);
                int kijun = GetIntParameter(parameters, "KijunSample", "KijunPeriod", 26);
                int offset = GetIntParameter(parameters, "Offset", "KijunPeriod", kijun);
                return Math.Max(senkouB, kijun) + offset;

            case IndicatorType.MassIndex:
                int emaPeriod = GetIntParameter(parameters, "EmaPeriod", "EmaPeriod", 9);
                int sumPeriod = GetIntParameter(parameters, "Period", "SumPeriod", 25);
                return (emaPeriod * 2) + sumPeriod; // default 18 + 25 = 43

            case IndicatorType.SchaffTrendCycle:
                int fast = GetIntParameter(parameters, "ShortPeriod", "FastPeriod", 23);
                int slow = GetIntParameter(parameters, "LongPeriod", "SlowPeriod", 50);
                int cycle = GetIntParameter(parameters, "CyclePeriod", "CyclePeriod", 10);
                return Math.Max(fast, slow) + (cycle * 2); // default 50 + 20 = 70

            case IndicatorType.MACD or IndicatorType.PPO or IndicatorType.PriceOscillator:
                int macdSlow = GetIntParameter(parameters, "LongPeriod", "SlowPeriod", 26);
                int macdSig = GetIntParameter(parameters, "SignalPeriod", "SignalPeriod", 9);
                return macdSlow + macdSig;

            case IndicatorType.Stoch:
                int stochK = GetIntParameter(parameters, "KPeriod", "KPeriod", 14);
                int stochSmooth = GetIntParameter(parameters, "Smooth", "SlowKPeriod", 3);
                int stochD = GetIntParameter(parameters, "DPeriod", "DPeriod", 3);
                return stochK + stochSmooth + stochD;

            default:
                if (parameters == null || parameters.Count == 0)
                {
                    return DefaultIndicatorPeriod;
                }

                int maxParam = DefaultIndicatorPeriod;
                foreach (var val in parameters.Values)
                {
                    int p = ExtractIntFromParam(val);
                    if (p > maxParam)
                    {
                        maxParam = p;
                    }
                }
                return maxParam;
        }
    }

    /// <summary>
    /// Reads an integer parameter safely from a parameter dictionary.
    /// </summary>
    public static int GetIntParameter(IReadOnlyDictionary<string, object>? parameters, string name, int defaultValue)
    {
        return GetIntParameter(parameters, name, name, defaultValue);
    }

    /// <summary>
    /// Reads an integer parameter safely from a parameter dictionary, checking a primary name first and falling back to a secondary alias.
    /// </summary>
    public static int GetIntParameter(IReadOnlyDictionary<string, object>? parameters, string primaryName, string secondaryName, int defaultValue)
    {
        if (parameters == null) return defaultValue;
        foreach (var kvp in parameters)
        {
            if (string.Equals(kvp.Key, primaryName, StringComparison.OrdinalIgnoreCase))
            {
                int val = ExtractIntFromParam(kvp.Value);
                if (val > 0) return val;
            }
        }
        if (!string.Equals(primaryName, secondaryName, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var kvp in parameters)
            {
                if (string.Equals(kvp.Key, secondaryName, StringComparison.OrdinalIgnoreCase))
                {
                    int val = ExtractIntFromParam(kvp.Value);
                    if (val > 0) return val;
                }
            }
        }
        return defaultValue;
    }

    private static int ExtractIntFromParam(object? val)
    {
        long result = 0;
        if (val is int i) result = i;
        else if (val is long l) result = l;
        else if (val is short sh) result = sh;
        else if (val is byte b) result = b;
        else if (val is JsonElement { ValueKind: JsonValueKind.Number } jsonVal && jsonVal.TryGetInt64(out long jVal))
        {
            result = jVal;
        }

        return (result > 0 && result < PeriodParameterExclusiveUpperBound) ? (int)result : 0;
    }

    private static int ExtractPeriodFromSide(ScreenerIndicatorSideConfig? side)
    {
        if (side == null) return DefaultIndicatorPeriod;
        return ResolveIndicatorWarmupBars(side.IndicatorType, side.Parameters);
    }
}
