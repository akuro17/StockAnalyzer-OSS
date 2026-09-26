using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Screener;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// Pure, per-bar evaluator for a sequence of <see cref="BacktestConditionEntry"/> values. Reads only
/// through <see cref="IndicatorSeriesSet.ValueAt"/> at <c>barIndex - side.Offset</c> — never a future
/// index — so no lookahead is structurally possible. Mirrors <c>ScreenerIndicatorEntry.IsMet</c>'s
/// per-entry comparison math (same <see cref="ComparisonOperator"/> switch), but chains entries as
/// booleans left-to-right (each entry's <see cref="BacktestConditionEntry.LogicalOperator"/> says how
/// to combine with the NEXT entry) instead of per-ticker set operations, since Backtest evaluates a
/// single symbol's single current bar rather than scanning many tickers at once. See
/// Y:\Temp\sa_implementation_plan_BacktestComparisonSignals.md section 4.2.
/// </summary>
public static class BacktestConditionEvaluator
{
    public static bool Evaluate(
        IReadOnlyList<BacktestConditionEntry> entries,
        IReadOnlyDictionary<BacktestConditionSide, string> sideToIndicatorKey,
        IndicatorSeriesSet indicators,
        int barIndex)
    {
        if (entries.Count == 0) return false;

        bool result = EvaluateSingle(entries[0], sideToIndicatorKey, indicators, barIndex);
        for (int i = 1; i < entries.Count; i++)
        {
            bool next = EvaluateSingle(entries[i], sideToIndicatorKey, indicators, barIndex);
            result = entries[i - 1].LogicalOperator == LogicalOperator.And
                ? result && next
                : result || next;
        }
        return result;
    }

    private static bool EvaluateSingle(
        BacktestConditionEntry entry,
        IReadOnlyDictionary<BacktestConditionSide, string> sideToIndicatorKey,
        IndicatorSeriesSet indicators,
        int barIndex)
    {
        decimal? left = ReadSide(entry.Left, sideToIndicatorKey, indicators, barIndex);
        if (left is null) return false;

        decimal right;
        switch (entry.TargetMode)
        {
            case RightHandTargetMode.NumericValue:
                right = entry.RightNumericValue;
                break;
            case RightHandTargetMode.Indicator:
                if (entry.Right is null)
                {
                    throw new InvalidOperationException(
                        "BacktestConditionEntry.TargetMode is Indicator but Right is null.");
                }
                decimal? rightValue = ReadSide(entry.Right, sideToIndicatorKey, indicators, barIndex);
                if (rightValue is null) return false;
                right = rightValue.Value;
                break;
            default:
                // StringValue has no backing field on BacktestConditionEntry (indicator-only conditions) —
                // fail loudly rather than silently comparing against a non-existent value.
                throw new NotSupportedException(
                    $"RightHandTargetMode.{entry.TargetMode} is not supported for Backtest conditions (indicator-only, no string series).");
        }

        return entry.Operator switch
        {
            ComparisonOperator.GreaterThan => left.Value > right,
            ComparisonOperator.GreaterThanOrEqual => left.Value >= right,
            ComparisonOperator.LessThan => left.Value < right,
            ComparisonOperator.LessThanOrEqual => left.Value <= right,
            ComparisonOperator.Equal => left.Value == right,
            ComparisonOperator.NotEqual => left.Value != right,
            _ => throw new NotSupportedException(
                $"ComparisonOperator.{entry.Operator} is not supported for Backtest conditions (string-oriented, no string series).")
        };
    }

    private static decimal? ReadSide(
        BacktestConditionSide side,
        IReadOnlyDictionary<BacktestConditionSide, string> sideToIndicatorKey,
        IndicatorSeriesSet indicators,
        int barIndex)
    {
        if (!sideToIndicatorKey.TryGetValue(side, out string? key))
        {
            throw new KeyNotFoundException("BacktestConditionSide was not mapped to an indicator key.");
        }
        return indicators.ValueAt(key, barIndex - side.Offset);
    }
}
