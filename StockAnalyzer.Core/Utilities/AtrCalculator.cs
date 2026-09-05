using System;
using System.Collections.Generic;
using System.Linq;
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
    }
}
