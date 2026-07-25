using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volume;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreObvIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(decimal[] closes, decimal[] volumes)
        {
            var startDate = DateTime.Today;
            return closes.Select((price, i) => new CoreCandleData(
                startDate.AddDays(i),
                price, // Open
                price, // High
                price, // Low
                price, // Close
                (long)volumes[i] // Volume
            )).ToList();
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsCorrectObv()
        {
            var indicator = new CoreObvIndicator();
            var candles = CreateTestCandles(
                new decimal[] { 10, 11, 10, 9, 10 },
                new decimal[] { 1000, 1100, 1200, 1300, 1400 }
            );

            indicator.Calculate(candles);

            var expected = new decimal?[] { 0, 1100, -100, -1400, 0 };
            Assert.Equal(expected.Length, indicator.Values.Count);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i], indicator.Values[i]);
            }
        }

        [Fact]
        public void Calculate_WithNoPriceChange_ObvStaysSame()
        {
            var indicator = new CoreObvIndicator();
            var candles = CreateTestCandles(
                new decimal[] { 10, 10, 10, 10, 10 },
                new decimal[] { 1000, 1100, 1200, 1300, 1400 }
            );

            indicator.Calculate(candles);

            var expected = new decimal?[] { 0, 0, 0, 0, 0 };
            Assert.Equal(expected.Length, indicator.Values.Count);
            Assert.All(indicator.Values, v => Assert.Equal(0, v));
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreObvIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }
    }
}
