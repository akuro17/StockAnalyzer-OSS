using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreSmmaIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(IEnumerable<decimal> closePrices)
        {
            var startDate = DateTime.Today;
            return closePrices.Select((price, i) => new CoreCandleData(
                startDate.AddDays(i), price, price, price, price, 1000
            )).ToList();
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsCorrectSmma()
        {
            var indicator = new CoreSmmaIndicator { Period = 3 };
            var candles = CreateTestCandles(new decimal[] { 10, 12, 14, 13, 15 });

            indicator.Calculate(candles);

            // SMMA(i) = (SMMA(i-1) * (n-1) + CLOSE(i)) / n
            // i=2: SMA = (10+12+14)/3 = 12
            // i=3: (12 * 2 + 13)/3 = 37/3 = 12.333
            // i=4: (12.333 * 2 + 15)/3 = (24.666 + 15)/3 = 39.666/3 = 13.222
            var expected = new decimal?[] { null, null, 12m, 12.333333333333333333333333333m, 13.222222222222222222222222222m };

            Assert.Equal(expected.Length, indicator.Values.Count);
            Assert.Null(indicator.Values[0]);
            Assert.Null(indicator.Values[1]);
            Assert.Equal((double)expected[2]!, (double)indicator.Values[2]!, precision: 8);
            Assert.Equal((double)expected[3]!, (double)indicator.Values[3]!, precision: 8);
            Assert.Equal((double)expected[4]!, (double)indicator.Values[4]!, precision: 8);
        }

        [Fact]
        public void Calculate_WithInsufficientData_ReturnsNulls()
        {
            var indicator = new CoreSmmaIndicator { Period = 5 };
            var candles = CreateTestCandles(new decimal[] { 10, 12, 14 });

            indicator.Calculate(candles);

            Assert.Equal(3, indicator.Values.Count);
            Assert.All(indicator.Values, v => Assert.Null(v));
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreSmmaIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }
    }
}
