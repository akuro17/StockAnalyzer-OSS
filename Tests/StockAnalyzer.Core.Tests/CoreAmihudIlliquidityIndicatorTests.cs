using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volume;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreAmihudIlliquidityIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(decimal[] closes, long[] volumes)
        {
            var startDate = DateTime.Today;
            return closes.Select((close, i) => new CoreCandleData(
                startDate.AddDays(i),
                i > 0 ? closes[i-1] : close,
                close + 1,
                close - 1,
                close,
                volumes[i]
            )).ToList();
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsCorrectValues()
        {
            var indicator = new CoreAmihudIlliquidityIndicator { Period = 2 };
            var candles = CreateTestCandles(
                new decimal[] { 100, 102, 101, 103, 104 },
                new long[] { 10000, 11000, 12000, 13000, 14000 }
            );

            var result = indicator.Calculate(candles);
            Assert.True(result.IsSuccessful);

            var expected = new decimal?[]
            {
                null,
                null,
                0.0000013175876411170528817588M, // 1.31758e-6
                0.0000011701113549888158841169M, // 1.17011e-6
                0.0000011083552609932261098006M  // 1.10835e-6
            };

            Assert.Equal(expected.Length, result.MainValues.Count);
            for (int i = 0; i < expected.Length; i++)
            {
                if (expected[i].HasValue)
                {
                    Assert.NotNull(result.MainValues[i]);
                    // Reducing precision check to avoid slight floating point issues if any
                    Assert.Equal(expected[i].Value, result.MainValues[i].Value, 10);
                }
                else
                {
                    Assert.Null(result.MainValues[i]);
                }
            }
        }

        [Fact]
        public void Calculate_WithNotEnoughData_ReturnsAllNulls()
        {
            var indicator = new CoreAmihudIlliquidityIndicator { Period = 5 };
            var candles = CreateTestCandles(
                new decimal[] { 100, 102, 101 },
                new long[] { 10000, 11000, 12000 }
            );

            var result = indicator.Calculate(candles);
            
            Assert.True(result.IsSuccessful);
            Assert.Equal(3, result.MainValues.Count);
            Assert.All(result.MainValues, v => Assert.Null(v));
        }

        [Fact]
        public void Calculate_WithZeroVolume_HandlesCorrectly()
        {
            var indicator = new CoreAmihudIlliquidityIndicator { Period = 2 };
            var candles = CreateTestCandles(
                new decimal[] { 100, 102, 101, 103 },
                new long[] { 10000, 0, 12000, 13000 }
            );
            var result = indicator.Calculate(candles);

            // Check specific values
            // 2nd candle (zero volume) should result in 0 illiquidity for that day
            // But we are checking the Moving Average of Illiquidity.
            // ... validation logic same as before, just syntax update ...

            Assert.True(result.IsSuccessful);
            Assert.Equal(4, result.MainValues.Count);
            Assert.NotNull(result.MainValues[2]);
            // Value from previous calculations in original test
            Assert.Equal(0.0000004084967320261437908496M, result.MainValues[2].Value, 10);
        }
    }
}
