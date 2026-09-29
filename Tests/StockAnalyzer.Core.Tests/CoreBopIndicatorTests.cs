using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volume;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreBopIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(decimal[] opens, decimal[] highs, decimal[] lows, decimal[] closes)
        {
            var startDate = DateTime.Today;
            return highs.Select((high, i) => new CoreCandleData(
                startDate.AddDays(i),
                opens[i],
                high,
                lows[i],
                closes[i],
                1000 // Volume is not used in BOP
            )).ToList();
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsCorrectBop()
        {
            var indicator = new CoreBopIndicator();
            var candles = CreateTestCandles(
                new decimal[] { 10, 11, 10, 12 },
                new decimal[] { 12, 12, 12, 13 },
                new decimal[] { 8, 10, 8, 11 },
                new decimal[] { 11, 10, 11, 11.5m }
            );

            indicator.Calculate(candles);

            // 1. (11-10)/(12-8) = 1/4 = 0.25
            // 2. (10-11)/(12-10) = -1/2 = -0.5
            // 3. (11-10)/(12-8) = 1/4 = 0.25
            // 4. (11.5-12)/(13-11) = -0.5/2 = -0.25
            var expected = new decimal?[] { 0.25m, -0.5m, 0.25m, -0.25m };

            Assert.Equal(expected.Length, indicator.Values.Count);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i], indicator.Values[i]);
            }
        }

        [Fact]
        public void Calculate_WithZeroRange_ReturnsZero()
        {
            var indicator = new CoreBopIndicator();
            var candles = CreateTestCandles(
                new decimal[] { 10 },
                new decimal[] { 10 },
                new decimal[] { 10 },
                new decimal[] { 10 }
            );
            indicator.Calculate(candles);

            Assert.Single(indicator.Values);
            Assert.Equal(0, indicator.Values[0]);
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreBopIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }
    }
}
