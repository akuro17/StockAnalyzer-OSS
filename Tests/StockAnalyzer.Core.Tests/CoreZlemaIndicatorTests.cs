using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreZlemaIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(IEnumerable<decimal> closePrices)
        {
            var startDate = DateTime.Today;
            return closePrices.Select((price, i) => new CoreCandleData(
                startDate.AddDays(i), price, price, price, price, 1000
            )).ToList();
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsCorrectZlema()
        {
            var indicator = new CoreZlemaIndicator { Period = 3 };
            var candles = CreateTestCandles(new decimal[] { 10, 12, 14, 13, 15 });

            indicator.Calculate(candles);

            // Based on manual calculation:
            // lag = 1
            // emaData = [10, 14, 16, 12, 17]
            // ZLEMA:
            // i=2: SMA(10,14,16) = 13.333
            // i=3: EMA from previous = 12.666
            // i=4: EMA from previous = 14.833
            var expected = new decimal?[] { null, null, 13.333333m, 12.666667m, 14.833333m };

            Assert.Equal(expected.Length, indicator.Values.Count);
            Assert.Null(indicator.Values[0]);
            Assert.Null(indicator.Values[1]);
            Assert.Equal((double)expected[2]!, (double)indicator.Values[2]!, precision: 6);
            Assert.Equal((double)expected[3]!, (double)indicator.Values[3]!, precision: 6);
            Assert.Equal((double)expected[4]!, (double)indicator.Values[4]!, precision: 6);
        }

        [Fact]
        public void Calculate_WithInsufficientData_ReturnsNulls()
        {
            var indicator = new CoreZlemaIndicator { Period = 5 };
            var candles = CreateTestCandles(new decimal[] { 10, 12, 14 });

            indicator.Calculate(candles);

            Assert.Equal(3, indicator.Values.Count);
            Assert.All(indicator.Values, v => Assert.Null(v));
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreZlemaIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }
    }
}
