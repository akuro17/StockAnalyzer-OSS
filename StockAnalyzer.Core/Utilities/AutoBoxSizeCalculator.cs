using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.MathUtils;

namespace StockAnalyzer.Core.Utilities
{
    public static class AutoBoxSizeCalculator
    {
        public static decimal Calculate(
            ChartSizingMode mode,
            IEnumerable<CandleData> candles,
            decimal fixedSize,
            int atrPeriod,
            decimal atrMultiplier,
            ChartRoundingMode roundingMode,
            AutoFallbackMode fallbackMode,
            decimal currentPrice,
            decimal percentage = 1.0m)
        {
            if (candles is List<CandleData> list)
            {
                return Calculate(mode, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list), fixedSize, atrPeriod, atrMultiplier, roundingMode, fallbackMode, currentPrice, percentage);
            }
            if (candles is CandleData[] array)
            {
                return Calculate(mode, array.AsSpan(), fixedSize, atrPeriod, atrMultiplier, roundingMode, fallbackMode, currentPrice, percentage);
            }

            // Fallback for generic IEnumerable
            var candleList = candles.ToList();
            return Calculate(mode, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(candleList), fixedSize, atrPeriod, atrMultiplier, roundingMode, fallbackMode, currentPrice, percentage);
        }

        public static decimal Calculate(
            ChartSizingMode mode,
            IReadOnlyList<CoreCandleData> candles,
            decimal fixedSize,
            int atrPeriod,
            decimal atrMultiplier,
            ChartRoundingMode roundingMode,
            AutoFallbackMode fallbackMode,
            decimal currentPrice,
            decimal percentage = 1.0m)
        {
            if (candles is List<CoreCandleData> list)
            {
                return Calculate(mode, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list), fixedSize, atrPeriod, atrMultiplier, roundingMode, fallbackMode, currentPrice, percentage);
            }

            // Fallback for generic IReadOnlyList
            int len = candles.Count;
            if (currentPrice <= 0) return ChartConstants.DefaultBoxSize;

            if (!TryResolveNonAtrSize(mode, fixedSize, percentage, currentPrice, out decimal calculatedSize))
            {
                decimal atr = len > atrPeriod ? AtrCalculator.Calculate(candles, atrPeriod) : 0m;
                calculatedSize = ResolveAtrSize(atr, atrMultiplier, fallbackMode, fixedSize, currentPrice);
            }

            return PostProcessCalculatedSize(calculatedSize, roundingMode, currentPrice);
        }

        public static decimal Calculate(
            ChartSizingMode mode,
            ReadOnlySpan<CandleData> candles,
            decimal fixedSize,
            int atrPeriod,
            decimal atrMultiplier,
            ChartRoundingMode roundingMode,
            AutoFallbackMode fallbackMode,
            decimal currentPrice,
            decimal percentage = 1.0m)
        {
            if (currentPrice <= 0) return ChartConstants.DefaultBoxSize;

            if (!TryResolveNonAtrSize(mode, fixedSize, percentage, currentPrice, out decimal calculatedSize))
            {
                decimal atr = candles.Length > atrPeriod ? AtrCalculator.Calculate(candles, atrPeriod) : 0m;
                calculatedSize = ResolveAtrSize(atr, atrMultiplier, fallbackMode, fixedSize, currentPrice);
            }

            return PostProcessCalculatedSize(calculatedSize, roundingMode, currentPrice);
        }

        public static decimal Calculate(
            ChartSizingMode mode,
            ReadOnlySpan<CoreCandleData> candles,
            decimal fixedSize,
            int atrPeriod,
            decimal atrMultiplier,
            ChartRoundingMode roundingMode,
            AutoFallbackMode fallbackMode,
            decimal currentPrice,
            decimal percentage = 1.0m)
        {
            if (currentPrice <= 0) return ChartConstants.DefaultBoxSize;

            if (!TryResolveNonAtrSize(mode, fixedSize, percentage, currentPrice, out decimal calculatedSize))
            {
                decimal atr = candles.Length > atrPeriod ? AtrCalculator.Calculate(candles, atrPeriod) : 0m;
                calculatedSize = ResolveAtrSize(atr, atrMultiplier, fallbackMode, fixedSize, currentPrice);
            }

            return PostProcessCalculatedSize(calculatedSize, roundingMode, currentPrice);
        }

        /// <summary>
        /// Fixed and Percentage sizing, shared by every overload. Returns false (size 0) for any other mode, which the callers resolve from the ATR.
        /// </summary>
        private static bool TryResolveNonAtrSize(ChartSizingMode mode, decimal fixedSize, decimal percentage, decimal currentPrice, out decimal size)
        {
            if (mode == ChartSizingMode.Fixed)
            {
                size = fixedSize > 0 ? fixedSize : ChartConstants.DefaultBoxSize;
                return true;
            }

            if (mode == ChartSizingMode.Percentage)
            {
                size = currentPrice * (percentage / ChartConstants.PercentageDivisor);
                return true;
            }

            size = 0m;
            return false;
        }

        /// <summary>
        /// ATR sizing with the configured fallback; <paramref name="atr"/> is 0 when there are not enough bars. The ATR itself is computed by the
        /// caller only in this mode, so Fixed and Percentage never pay for it.
        /// </summary>
        private static decimal ResolveAtrSize(decimal atr, decimal atrMultiplier, AutoFallbackMode fallbackMode, decimal fixedSize, decimal currentPrice)
        {
            decimal size = atr > 0 ? atr * atrMultiplier : 0m;
            if (size <= 0)
            {
                size = fallbackMode == AutoFallbackMode.Fixed
                    ? (fixedSize > 0 ? fixedSize : ChartConstants.DefaultBoxSize)
                    : currentPrice * ChartConstants.DefaultFallbackPercentage;
            }

            return size;
        }

        /// <summary>
        /// Same computation as <see cref="Calculate(ChartSizingMode, ReadOnlySpan{CoreCandleData}, decimal, int, decimal, ChartRoundingMode, AutoFallbackMode, decimal, decimal)"/>, except that a size that is not
        /// positive after the rounding post-processing is REPORTED (returns false, <paramref name="size"/> = 0) instead of being replaced by <see cref="ChartConstants.DefaultBoxSize"/>.
        /// A non-positive <paramref name="currentPrice"/> also returns false. When it returns true, <paramref name="size"/> equals what <c>Calculate</c> returns for the same arguments.
        /// Used by the time-series overlays of the block charts, which must wait for a usable size instead of silently drawing with a substituted one.
        /// </summary>
        public static bool TryCalculateStrict(
            ChartSizingMode mode,
            ReadOnlySpan<CoreCandleData> candles,
            decimal fixedSize,
            int atrPeriod,
            decimal atrMultiplier,
            ChartRoundingMode roundingMode,
            AutoFallbackMode fallbackMode,
            decimal currentPrice,
            decimal percentage,
            out decimal size)
        {
            size = 0m;
            if (currentPrice <= 0) return false;

            if (!TryResolveNonAtrSize(mode, fixedSize, percentage, currentPrice, out decimal calculatedSize))
            {
                decimal atr = candles.Length > atrPeriod ? AtrCalculator.Calculate(candles, atrPeriod) : 0m;
                calculatedSize = ResolveAtrSize(atr, atrMultiplier, fallbackMode, fixedSize, currentPrice);
            }

            calculatedSize = QuantizeCalculatedSize(calculatedSize, roundingMode, currentPrice);
            if (calculatedSize <= 0) return false;

            size = calculatedSize;
            return true;
        }

        private static decimal PostProcessCalculatedSize(decimal calculatedSize, ChartRoundingMode roundingMode, decimal currentPrice)
        {
            calculatedSize = QuantizeCalculatedSize(calculatedSize, roundingMode, currentPrice);
            return calculatedSize > 0 ? calculatedSize : ChartConstants.DefaultBoxSize;
        }

        /// <summary>The rounding post-processing alone: the result may be zero (a size smaller than the tick, floored). Shared by <c>Calculate</c> (which then substitutes the default) and <see cref="TryCalculateStrict"/>.</summary>
        private static decimal QuantizeCalculatedSize(decimal calculatedSize, ChartRoundingMode roundingMode, decimal currentPrice)
        {
            if (calculatedSize > 0 && roundingMode != ChartRoundingMode.None)
            {
                if (roundingMode == ChartRoundingMode.NiceNumbers)
                {
                    calculatedSize = RoundingHelper.RoundToNiceNumber(calculatedSize);
                }
                else
                {
                    decimal step = EstimateTickSize(currentPrice);
                    if (roundingMode == ChartRoundingMode.TickSize ||
                        roundingMode == ChartRoundingMode.Floor ||
                        roundingMode == ChartRoundingMode.Ceiling ||
                        roundingMode == ChartRoundingMode.Round)
                    {
                        calculatedSize = ChartMath.Quantize(calculatedSize, step, roundingMode);
                    }
                }
            }

            return calculatedSize;
        }

        public static decimal EstimateTickSize(decimal price)
        {
            return ChartMath.EstimateTickSize(price);
        }
    }
}
