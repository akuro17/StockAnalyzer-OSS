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

            decimal calculatedSize = 0m;
            if (mode == ChartSizingMode.Fixed)
            {
                calculatedSize = fixedSize > 0 ? fixedSize : ChartConstants.DefaultBoxSize;
            }
            else if (mode == ChartSizingMode.Percentage)
            {
                calculatedSize = currentPrice * (percentage / ChartConstants.PercentageDivisor);
            }
            else
            {
                bool sufficientlyData = len > atrPeriod;
                if (sufficientlyData)
                {
                    decimal atr = AtrCalculator.Calculate(candles, atrPeriod);
                    if (atr > 0) calculatedSize = atr * atrMultiplier;
                }

                if (calculatedSize <= 0)
                {
                    if (fallbackMode == AutoFallbackMode.Fixed)
                        calculatedSize = fixedSize > 0 ? fixedSize : ChartConstants.DefaultBoxSize;
                    else
                        calculatedSize = currentPrice * ChartConstants.DefaultFallbackPercentage;
                }
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

            decimal calculatedSize = 0m;
            if (mode == ChartSizingMode.Fixed)
            {
                calculatedSize = fixedSize > 0 ? fixedSize : ChartConstants.DefaultBoxSize;
            }
            else if (mode == ChartSizingMode.Percentage)
            {
                calculatedSize = currentPrice * (percentage / ChartConstants.PercentageDivisor);
            }
            else
            {
                bool sufficientlyData = candles.Length > atrPeriod;
                if (sufficientlyData)
                {
                    decimal atr = AtrCalculator.Calculate(candles, atrPeriod);
                    if (atr > 0) calculatedSize = atr * atrMultiplier;
                }

                if (calculatedSize <= 0)
                {
                    if (fallbackMode == AutoFallbackMode.Fixed)
                        calculatedSize = fixedSize > 0 ? fixedSize : ChartConstants.DefaultBoxSize;
                    else
                        calculatedSize = currentPrice * ChartConstants.DefaultFallbackPercentage;
                }
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

            decimal calculatedSize = 0m;
            if (mode == ChartSizingMode.Fixed)
            {
                calculatedSize = fixedSize > 0 ? fixedSize : ChartConstants.DefaultBoxSize;
            }
            else if (mode == ChartSizingMode.Percentage)
            {
                calculatedSize = currentPrice * (percentage / ChartConstants.PercentageDivisor);
            }
            else
            {
                bool sufficientlyData = candles.Length > atrPeriod;
                if (sufficientlyData)
                {
                    decimal atr = AtrCalculator.Calculate(candles, atrPeriod);
                    if (atr > 0) calculatedSize = atr * atrMultiplier;
                }

                if (calculatedSize <= 0)
                {
                    if (fallbackMode == AutoFallbackMode.Fixed)
                        calculatedSize = fixedSize > 0 ? fixedSize : ChartConstants.DefaultBoxSize;
                    else
                        calculatedSize = currentPrice * ChartConstants.DefaultFallbackPercentage;
                }
            }

            return PostProcessCalculatedSize(calculatedSize, roundingMode, currentPrice);
        }

        private static decimal PostProcessCalculatedSize(decimal calculatedSize, ChartRoundingMode roundingMode, decimal currentPrice)
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

            return calculatedSize > 0 ? calculatedSize : ChartConstants.DefaultBoxSize;
        }

        public static decimal EstimateTickSize(decimal price)
        {
            return ChartMath.EstimateTickSize(price);
        }
    }
}
