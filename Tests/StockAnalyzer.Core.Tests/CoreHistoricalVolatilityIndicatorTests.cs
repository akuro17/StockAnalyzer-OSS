using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreHistoricalVolatilityIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(IEnumerable<decimal> closePrices)
        {
            var startDate = DateTime.Today;
            return closePrices.Select((price, i) => new CoreCandleData(
                startDate.AddDays(i), price, price, price, price, 1000
            )).ToList();
        }

        [Fact]
        public void Calculate_WithConstantPrice_ReturnsZero()
        {
            var indicator = new CoreHistoricalVolatilityIndicator { Period = 5 };
            var candles = CreateTestCandles(new decimal[] { 10, 10, 10, 10, 10, 10, 10 });

            indicator.Calculate(candles);

            // If price doesn't change, returns are 0, so volatility is 0.
            Assert.Equal(7, indicator.Values.Count);
            Assert.Equal(0.0, (double)indicator.Values[5]!, precision: 4);
            Assert.Equal(0.0, (double)indicator.Values[6]!, precision: 4);
        }

        [Fact]
        public void Calculate_WithSufficientData_ReturnsNonZeroValue()
        {
            var indicator = new CoreHistoricalVolatilityIndicator { Period = 5 };
            var candles = CreateTestCandles(new decimal[] { 10, 10.1m, 10.2m, 10.1m, 10.3m, 10.4m, 10.5m });

            indicator.Calculate(candles);

            // Just check if it calculates a sensible, non-zero, non-null value.
            // Precise value is hard to verify without a known source.
            Assert.NotNull(indicator.Values[5]);
            Assert.True(indicator.Values[5] > 0);
            Assert.NotNull(indicator.Values[6]);
            Assert.True(indicator.Values[6] > 0);
        }

        [Fact]
        public void Calculate_WithInsufficientData_ReturnsNulls()
        {
            var indicator = new CoreHistoricalVolatilityIndicator { Period = 20 };
            var candles = CreateTestCandles(new decimal[] { 10, 12, 14 });

            indicator.Calculate(candles);

            Assert.Equal(3, indicator.Values.Count);
            Assert.All(indicator.Values, v => Assert.Null(v));
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreHistoricalVolatilityIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }
    }
}
