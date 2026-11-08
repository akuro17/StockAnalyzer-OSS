using System;
using System.Collections.Generic;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Computes synthetic OHLCV candles from two aligned series based on a binary operation (+, -, /).
/// Enforces decimal precision, OHLC ordering invariants, and non-negative leg liquidity volume rules.
/// </summary>
public static class SyntheticCandleCalculator
{
    private static decimal Round8(decimal value) => Math.Round(value, 8, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Computes synthetic candles for the given aligned dataset and expression.
    /// Excludes bars where either side is missing, division by zero occurs, or decimal overflow is encountered.
    /// </summary>
    public static IReadOnlyList<CandleData> ComputeCandles(
        ComparisonAlignedData alignedData,
        SyntheticSymbolExpression expression)
    {
        ArgumentNullException.ThrowIfNull(alignedData);

        if (!expression.IsOperatorSupported || alignedData.Timestamps.Length == 0)
        {
            return Array.Empty<CandleData>();
        }

        if (!alignedData.Series.TryGetValue(expression.LeftSymbol, out var leftSeries) ||
            !alignedData.Series.TryGetValue(expression.RightSymbol, out var rightSeries) ||
            leftSeries == null || rightSeries == null)
        {
            return Array.Empty<CandleData>();
        }

        int count = alignedData.Timestamps.Length;
        var result = new List<CandleData>(count);

        for (int i = 0; i < count; i++)
        {
            DateTime timestamp = alignedData.Timestamps[i];
            CandleData? leftOpt = i < leftSeries.Length ? leftSeries[i] : null;
            CandleData? rightOpt = i < rightSeries.Length ? rightSeries[i] : null;

            if (!leftOpt.HasValue || !rightOpt.HasValue)
            {
                continue;
            }

            CandleData left = leftOpt.Value;
            CandleData right = rightOpt.Value;

            if (expression.Operation == SeriesOperation.Divide)
            {
                if (right.Low <= 0m || right.High <= 0m || right.Open <= 0m || right.Close <= 0m)
                {
                    continue;
                }
            }

            decimal? oRaw = SeriesArithmetic.Apply(expression.Operation, left.Open, right.Open, out bool divZeroO);
            decimal? cRaw = SeriesArithmetic.Apply(expression.Operation, left.Close, right.Close, out bool divZeroC);

            if (divZeroO || divZeroC || !oRaw.HasValue || !cRaw.HasValue)
            {
                continue;
            }

            decimal open = Round8(oRaw.Value);
            decimal close = Round8(cRaw.Value);
            decimal high;
            decimal low;

            try
            {
                if (expression.Operation == SeriesOperation.Subtract)
                {
                    decimal hCandidate = left.High - right.Low;
                    decimal lCandidate = left.Low - right.High;
                    high = Round8(Math.Max(hCandidate, Math.Max(open, close)));
                    low = Round8(Math.Min(lCandidate, Math.Min(open, close)));
                }
                else if (expression.Operation == SeriesOperation.Divide)
                {
                    if (right.Low <= 0m || right.High <= 0m)
                    {
                        continue;
                    }
                    decimal hCandidate = left.High / right.Low;
                    decimal lCandidate = left.Low / right.High;
                    high = Round8(Math.Max(hCandidate, Math.Max(open, close)));
                    low = Round8(Math.Min(lCandidate, Math.Min(open, close)));
                }
                else
                {
                    // SeriesOperation.Add
                    decimal hCandidate = left.High + right.High;
                    decimal lCandidate = left.Low + right.Low;
                    high = Round8(Math.Max(hCandidate, Math.Max(open, close)));
                    low = Round8(Math.Min(lCandidate, Math.Min(open, close)));
                }
            }
            catch (OverflowException)
            {
                continue;
            }

            // Invariant guarantee: Low <= Min(Open, Close) <= Max(Open, Close) <= High
            high = Math.Max(high, Math.Max(open, close));
            low = Math.Min(low, Math.Min(open, close));

            long vA = Math.Max(0L, left.Volume);
            long vB = Math.Max(0L, right.Volume);
            long volume;

            if (expression.Operation == SeriesOperation.Add)
            {
                try
                {
                    volume = checked(vA + vB);
                }
                catch (OverflowException)
                {
                    volume = long.MaxValue;
                }
            }
            else
            {
                // Subtract and Divide: minimum leg liquidity to prevent negative volume and distortion
                volume = Math.Min(vA, vB);
            }

            result.Add(new CandleData(timestamp, open, high, low, close, volume));
        }

        return result;
    }
}
