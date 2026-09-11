using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Utilities;

namespace StockAnalyzer.Core.Tests.Utilities
{
    public class AutoBoxSizeCalculatorTests
    {
        private List<CandleData> CreateCandles(int count, decimal startPrice, decimal volatility = 10m)
        {
            var candles = new List<CandleData>();
            var date = new DateTime(2023, 1, 1);
            decimal currentPrice = startPrice;

            for (int i = 0; i < count; i++)
            {
                decimal open = currentPrice;
                decimal close = open + (i % 2 == 0 ? volatility : -volatility);
                decimal high = Math.Max(open, close) + volatility * 0.1m;
                decimal low = Math.Min(open, close) - volatility * 0.1m;
                
                candles.Add(new CandleData(
                    date.AddDays(i),
                    open, high, low, close,
                    1000
                ));

                currentPrice = close;
            }
            return candles;
        }

        [Fact]
        public void Calculate_FixedMode_ReturnsFixedSize()
        {
            var candles = CreateCandles(10, 100);
            var result = AutoBoxSizeCalculator.Calculate(
                ChartSizingMode.Fixed,
                candles,
                fixedSize: 5.5m,
                atrPeriod: 14,
                atrMultiplier: 1.0m,
                roundingMode: ChartRoundingMode.None,
                fallbackMode: AutoFallbackMode.Fixed,
                currentPrice: 100m
            );

            Assert.Equal(5.5m, result);
        }

        [Fact]
        public void Calculate_PercentageMode_ReturnsCorrectPercentage()
        {
            var candles = CreateCandles(10, 200);
            var result = AutoBoxSizeCalculator.Calculate(
                ChartSizingMode.Percentage,
                candles,
                fixedSize: 0,
                atrPeriod: 14,
                atrMultiplier: 1.0m,
                roundingMode: ChartRoundingMode.None,
                fallbackMode: AutoFallbackMode.Fixed,
                currentPrice: 200m,
                percentage: 1.5m
            );

            Assert.Equal(3.0m, result); // 200 * 1.5% = 3
        }

        [Fact]
        public void Calculate_AutoAtr_ReturnsAtrBasedSize()
        {
            // Create candles with known volatility ~10
            var candles = CreateCandles(30, 100, volatility: 10m);
            
            // ATR Calculator uses High-Low, High-PrevClose, Low-PrevClose
            // Our helper creates candles with range approx 10.2 (High-Low) and gaps match close.
            // Expected ATR is roughly volatility.
            
            var result = AutoBoxSizeCalculator.Calculate(
                ChartSizingMode.AutoAtr,
                candles,
                fixedSize: 0,
                atrPeriod: 14,
                atrMultiplier: 2.0m, // 2x ATR
                roundingMode: ChartRoundingMode.None,
                fallbackMode: AutoFallbackMode.Fixed,
                currentPrice: 100m
            );

            // Volatility is 10. High-Low is ~12 (10 + 1 + 1). 
            // Result should be around 24.
            Assert.True(result > 22m && result < 26m);
        }

        [Fact]
        public void Calculate_AutoAtr_FallbackToFixed_WhenNotEnoughData()
        {
            var candles = CreateCandles(5, 100); // Less than period 14
            var result = AutoBoxSizeCalculator.Calculate(
                ChartSizingMode.AutoAtr,
                candles,
                fixedSize: 10m,
                atrPeriod: 14,
                atrMultiplier: 1.0m,
                roundingMode: ChartRoundingMode.None,
                fallbackMode: AutoFallbackMode.Fixed,
                currentPrice: 100m
            );

            Assert.Equal(10m, result);
        }

        [Fact]
        public void Calculate_AutoAtr_FallbackToPercentage_WhenNotEnoughData()
        {
            var candles = CreateCandles(5, 500); // Less than period 14
            var result = AutoBoxSizeCalculator.Calculate(
                ChartSizingMode.AutoAtr,
                candles,
                fixedSize: 0,
                atrPeriod: 14,
                atrMultiplier: 1.0m,
                roundingMode: ChartRoundingMode.None,
                fallbackMode: AutoFallbackMode.Percentage,
                currentPrice: 500m
            );

            // Default fallback percentage is 1% (hardcoded currently)
            Assert.Equal(5m, result); 
        }

        [Fact]
        public void Calculate_Rounding_NiceNumbers()
        {
            // Input 23 -> Round to Nice Number -> 25 (2.5 * 10)
            // Or 23 -> 20 / 25?
            // RoundingHelper logic:
            // 2.3 -> 2.3 < 2.3 (False) -> < 2.8 (True) -> 2.5
            // So 23 -> 25
            
            var candles = CreateCandles(10, 100);
            
            // Use Percentage mode to force a specific raw value
            // 1000 * 2.3% = 23
            var result = AutoBoxSizeCalculator.Calculate(
                ChartSizingMode.Percentage,
                candles,
                fixedSize: 0,
                atrPeriod: 14,
                atrMultiplier: 1.0m,
                roundingMode: ChartRoundingMode.NiceNumbers,
                fallbackMode: AutoFallbackMode.Fixed,
                currentPrice: 1000m,
                percentage: 2.3m 
            );

            Assert.Equal(25m, result);
        }

        [Fact]
        public void Calculate_Rounding_TickSize()
        {
            // Current Logic: 
            // Price >= 1000 -> Tick 1.0
            // Price >= 100 -> Tick 0.1
            
            var candles = CreateCandles(10, 1500);
            // Raw size: 1500 * 0.123% = 1.845
            // Tick size for 1500 (>1000) is 1.0
            // Expected Round(1.845 / 1.0) * 1.0 = 2.0
            
            var result = AutoBoxSizeCalculator.Calculate(
                ChartSizingMode.Percentage,
                candles,
                fixedSize: 0,
                atrPeriod: 14,
                atrMultiplier: 1.0m,
                roundingMode: ChartRoundingMode.TickSize,
                fallbackMode: AutoFallbackMode.Fixed,
                currentPrice: 1500m,
                percentage: 0.123m
            );

            Assert.Equal(2.0m, result);
        }
    }
}
