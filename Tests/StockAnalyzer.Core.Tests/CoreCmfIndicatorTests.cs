using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volume;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreCmfIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(decimal[] highs, decimal[] lows, decimal[] closes, long[] volumes)
        {
            var startDate = DateTime.Today;
            return highs.Select((high, i) => new CoreCandleData(
                startDate.AddDays(i),
                i > 0 ? closes[i - 1] : closes[i], // Open
                high,
                lows[i],
                closes[i],
                volumes[i]
            )).ToList();
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsCorrectCmf()
        {
            var indicator = new CoreCmfIndicator { Period = 3 };
            var candles = CreateTestCandles(
                new decimal[] { 10, 11, 12, 13, 14 },
                new decimal[] { 8, 9, 10, 11, 12 },
                new decimal[] { 9, 10, 11, 12, 13 },
                new long[] { 1000, 1100, 1200, 1300, 1400 }
            );

            var result = indicator.Calculate(candles);

            Assert.True(result.IsSuccessful);

            var expected = new decimal?[]
            {
                null,
                null,
                0.0m,
                0.0m,
                0.0m
            };

            Assert.Equal(expected.Length, result.MainValues.Count);
            for (int i = 0; i < expected.Length; i++)
            {
                if (expected[i].HasValue)
                {
                    Assert.Equal(expected[i].Value, result.MainValues[i].Value, 4);
                }
                else
                {
                    Assert.Null(result.MainValues[i]);
                }
            }
        }

        [Fact]
        public void Calculate_WithIncreasingPrices_ReturnsPositiveCmf()
        {
            var indicator = new CoreCmfIndicator { Period = 3 };
            var candles = CreateTestCandles(
                new decimal[] { 10, 11, 12, 13, 14 },
                new decimal[] { 8, 9, 10, 11, 12 },
                new decimal[] { 10, 11, 12, 13, 14 }, // Close at high
                new long[] { 1000, 1100, 1200, 1300, 1400 }
            );

            var result = indicator.Calculate(candles);
            Assert.True(result.IsSuccessful);

            var expected = new decimal?[]
            {
                null,
                null,
                1.0m,
                1.0m,
                1.0m
            };

            Assert.Equal(expected.Length, result.MainValues.Count);
            for (int i = 2; i < expected.Length; i++) // Start check from the first valid value
            {
                 Assert.Equal(expected[i].Value, result.MainValues[i].Value, 4);
            }
        }

        [Fact]
        public void Calculate_WithNotEnoughData_ReturnsAllNulls()
        {
            var indicator = new CoreCmfIndicator { Period = 5 };
            var candles = CreateTestCandles(
                new decimal[] { 10, 11, 12 },
                new decimal[] { 8, 9, 10 },
                new decimal[] { 9, 10, 11 },
                new long[] { 1000, 1100, 1200 }
            );
            var result = indicator.Calculate(candles);

            Assert.Equal(3, result.MainValues.Count);
            Assert.All(result.MainValues, v => Assert.Null(v));
        }
    }
}
