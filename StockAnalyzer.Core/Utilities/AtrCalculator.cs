using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Utilities
{
    public static class AtrCalculator
    {
        public static decimal Calculate(IEnumerable<CandleData> candles, int period = 14)
        {
            if (candles is List<CandleData> list)
            {
                return Calculate(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list), period);
            }
            if (candles is CandleData[] array)
            {
                return Calculate(array.AsSpan(), period);
            }

            // Fallback for generic IEnumerable (still needs allocation but better than before)
            var candleList = candles.ToList();
            return Calculate(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(candleList), period);
        }

        public static decimal Calculate(IReadOnlyList<CoreCandleData> candles, int period = 14)
        {
            if (candles is List<CoreCandleData> list)
            {
                return Calculate(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list), period);
            }
            // Fallback for generic IReadOnlyList
            int len = candles.Count;
            if (len < period + 1)
            {
                if (len > 0) return candles[len - 1].Close * 0.01m;
                return 0m;
            }

            decimal trSum = 0m;
            int count = 0;
            for (int i = len - 1; i >= 1 && count < period; i--)
            {
                var current = candles[i];
                var prev = candles[i - 1];
                decimal hl = current.High - current.Low;
                decimal hpc = Math.Abs(current.High - prev.Close);
                decimal lpc = Math.Abs(current.Low - prev.Close);
                trSum += Math.Max(hl, Math.Max(hpc, lpc));
                count++;
            }
            return count > 0 ? trSum / count : 0m;
        }

        public static decimal Calculate(ReadOnlySpan<CandleData> candles, int period = 14)
        {
            if (candles.Length < period + 1)
            {
                if (candles.Length > 0)
                {
                    return candles[candles.Length - 1].Close * 0.01m;
                }
                return 0m;
            }

            decimal trSum = 0m;
            int count = 0;
            
            for (int i = candles.Length - 1; i >= 1 && count < period; i--)
            {
                var current = candles[i];
                var prev = candles[i - 1];

                decimal hl = current.High - current.Low;
                decimal hpc = Math.Abs(current.High - prev.Close);
                decimal lpc = Math.Abs(current.Low - prev.Close);

                decimal tr = Math.Max(hl, Math.Max(hpc, lpc));
                trSum += tr;
                count++;
            }

            return count > 0 ? trSum / count : 0m;
        }

        public static decimal Calculate(ReadOnlySpan<CoreCandleData> candles, int period = 14)
        {
            if (candles.Length < period + 1)
            {
                if (candles.Length > 0)
                {
                    return candles[candles.Length - 1].Close * 0.01m;
                }
                return 0m;
            }

            decimal trSum = 0m;
            int count = 0;
            
            for (int i = candles.Length - 1; i >= 1 && count < period; i--)
            {
                var current = candles[i];
                var prev = candles[i - 1];

                decimal hl = current.High - current.Low;
                decimal hpc = Math.Abs(current.High - prev.Close);
                decimal lpc = Math.Abs(current.Low - prev.Close);

                decimal tr = Math.Max(hl, Math.Max(hpc, lpc));
                trSum += tr;
                count++;
            }

            return count > 0 ? trSum / count : 0m;
        }

        /// <summary>
        /// Calculates True Range for a single bar.
        /// Pure function: Zero allocation, O(1) time complexity.
        /// </summary>
        /// <param name="high">Current bar high price.</param>
        /// <param name="low">Current bar low price.</param>
        /// <param name="previousClose">Previous bar close price. If null, returns high - low (clamped >= 0).</param>
        /// <returns>Non-negative True Range value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static decimal CalculateTrueRange(decimal high, decimal low, decimal? previousClose = null)
        {
            decimal hl = high >= low ? high - low : 0m;
            if (!previousClose.HasValue)
            {
                return hl;
            }

            decimal c = previousClose.Value;
            decimal hpc = Math.Abs(high - c);
            decimal lpc = Math.Abs(low - c);

            return Math.Max(hl, Math.Max(hpc, lpc));
        }

        /// <summary>
        /// Computes True Range series directly into a caller-supplied destination span.
        /// Pure Zero-Allocation: 0 bytes heap allocated.
        /// </summary>
        /// <param name="candles">Read-only span of candle records.</param>
        /// <param name="destination">Destination span matching candles.Length.</param>
        public static void CalculateTrueRangeSeries(ReadOnlySpan<CandleData> candles, Span<decimal> destination)
        {
            if (candles.Length == 0) return;
            if (destination.Length < candles.Length)
            {
                throw new ArgumentException("Destination span is shorter than candles length.", nameof(destination));
            }

            destination[0] = CalculateTrueRange(candles[0].High, candles[0].Low, null);
            for (int i = 1; i < candles.Length; i++)
            {
                destination[i] = CalculateTrueRange(candles[i].High, candles[i].Low, candles[i - 1].Close);
            }
        }

        /// <summary>
        /// Computes True Range series for CoreCandleData directly into a caller-supplied destination span.
        /// Pure Zero-Allocation: 0 bytes heap allocated.
        /// </summary>
        public static void CalculateTrueRangeSeries(ReadOnlySpan<CoreCandleData> candles, Span<decimal> destination)
        {
            if (candles.Length == 0) return;
            if (destination.Length < candles.Length)
            {
                throw new ArgumentException("Destination span is shorter than candles length.", nameof(destination));
            }

            destination[0] = CalculateTrueRange(candles[0].High, candles[0].Low, null);
            for (int i = 1; i < candles.Length; i++)
            {
                destination[i] = CalculateTrueRange(candles[i].High, candles[i].Low, candles[i - 1].Close);
            }
        }
    }
}
