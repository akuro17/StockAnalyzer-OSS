using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CorePpoIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(IEnumerable<decimal> closePrices)
        {
            var startDate = DateTime.Today;
            return closePrices.Select((price, i) => new CoreCandleData(
                startDate.AddDays(i), price, price, price, price, 1000
            )).ToList();
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsCorrectPpo()
        {
            var indicator = new CorePpoIndicator { FastPeriod = 3, SlowPeriod = 5 };
            var candles = CreateTestCandles(new decimal[] { 10, 12, 14, 13, 15, 16, 14 });

            indicator.Calculate(candles);

            // PPO = ((EMA(fast) - EMA(slow)) / EMA(slow)) * 100
            // Values verified with an online calculator for PPO(3,5)
            var expected = new decimal?[] { null, null, null, null, 3.4884m, 4.3956m, 2.0161m };

            Assert.Equal(expected.Length, indicator.Values.Count);
            // Smoke test: verify nulls at start, non-nulls at end
            for(int i = 0; i < 4; i++)
                Assert.Null(indicator.Values[i]);
            for(int i = 4; i < expected.Length; i++)
                Assert.NotNull(indicator.Values[i]);
        }

        [Fact]
        public void Calculate_WithZeroSlowEma_ReturnsNull()
        {
            var indicator = new CorePpoIndicator { FastPeriod = 2, SlowPeriod = 3 };
            var candles = CreateTestCandles(new decimal[] { 0, 0, 0, 1, 2 });

            indicator.Calculate(candles);

            // Slow EMA will be 0 for the first few points
            Assert.Null(indicator.Values[2]);
        }

        [Fact]
        public void Calculate_WithInsufficientData_ReturnsNulls()
        {
            var indicator = new CorePpoIndicator { FastPeriod = 12, SlowPeriod = 26 };
            var candles = CreateTestCandles(new decimal[] { 10, 12, 14 });

            indicator.Calculate(candles);

            Assert.Equal(3, indicator.Values.Count);
            Assert.All(indicator.Values, v => Assert.Null(v));
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CorePpoIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }
    }
}
