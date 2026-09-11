using System;
using System.Linq;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Evaluates and determines the optimal candle history length required for screening conditions.
/// Differentiates full-history indicators (Anchored VWAP, OBV, ATH/ATL) from standard indicators (SMA, RSI, MACD).
/// </summary>
public static class ScreenerHistoryRequirements
{
    public const int DefaultStandardCandleCount = 500;

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

        int required = (maxPeriod * 3) + maxOffset + 100;
        return Math.Max(DefaultStandardCandleCount, required);
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

        return 200;
    }

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

        return 0;
    }

    private static int ExtractPeriodFromSide(ScreenerIndicatorSideConfig? side)
    {
        if (side?.Parameters == null) return 20;

        int maxP = 20;
        foreach (var val in side.Parameters.Values)
        {
            if (val is int iVal && iVal > maxP && iVal < 10000)
            {
                maxP = iVal;
            }
            else if (val is long lVal && lVal > maxP && lVal < 10000)
            {
                maxP = (int)lVal;
            }
        }
        return maxP;
    }
}
