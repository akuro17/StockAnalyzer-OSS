using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreVarianceIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(decimal[] closes)
        {
            var startDate = DateTime.Today;
            return closes.Select((close, i) => new CoreCandleData(
                startDate.AddDays(i),
                i > 0 ? closes[i-1] : close,
                close + 1,
                close - 1,
                close,
                1000
            )).ToList();
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsCorrectVariance()
        {
            var indicator = new CoreVarianceIndicator { Period = 5 };
            var closes = new decimal[] { 10, 12, 11, 13, 10, 11, 12 };
            var candles = CreateTestCandles(closes);
            indicator.Calculate(candles);

            // Period 1: {10,12,11,13,10}, Mean=11.2, Var=1.7
            // Period 2: {12,11,13,10,11}, Mean=11.4, Var=1.3
            // Period 3: {11,13,10,11,12}, Mean=11.4, Var=1.3
            var expected = new decimal?[]
            {
                null, null, null, null,
                1.7m,
                1.3m,
                1.3m
            };

            Assert.Equal(expected.Length, indicator.Values.Count);
            for (int i = 0; i < expected.Length; i++)
            {
                if (expected[i].HasValue)
                {
                    Assert.Equal(expected[i].Value, indicator.Values[i].Value, 4);
                }
                else
                {
                    Assert.Null(indicator.Values[i]);
                }
            }
        }

        [Fact]
        public void Calculate_WithNotEnoughData_ReturnsAllNulls()
        {
            var indicator = new CoreVarianceIndicator { Period = 5 };
            var candles = CreateTestCandles(new decimal[] { 10, 12, 11 });
            indicator.Calculate(candles);
            Assert.Equal(3, indicator.Values.Count);
            Assert.All(indicator.Values, v => Assert.Null(v));
        }

        [Fact]
        public void Calculate_WithConstantData_ReturnsZeroVariance()
        {
            var indicator = new CoreVarianceIndicator { Period = 5 };
            var candles = CreateTestCandles(new decimal[] { 10, 10, 10, 10, 10 });
            indicator.Calculate(candles);
            Assert.Equal(0, indicator.Values.Last().Value, 4);
        }
    }
}
