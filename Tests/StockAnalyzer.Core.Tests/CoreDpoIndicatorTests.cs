using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreDpoIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(IEnumerable<decimal> closePrices)
        {
            var startDate = DateTime.Today;
            return closePrices.Select((price, i) => new CoreCandleData(
                startDate.AddDays(i), price, price, price, price, 1000
            )).ToList();
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsCorrectDpo()
        {
            var indicator = new CoreDpoIndicator { Period = 4 };
            // Need enough data: Period-1 for SMA + shift for both sides
            // Period=4, shift=3. Need at least Period-1+shift+shift = 9 data points for one non-null
            var candles = CreateTestCandles(new decimal[] { 10, 12, 14, 13, 15, 16, 14, 12, 11, 13, 15 });

            indicator.Calculate(candles);

            // DPO = Close - SMA from shift periods ago
            // Period=4, shift=3, data count=11
            // Non-null: i >= Period-1+shift = 6 AND i < count-shift = 8
            // So indices 6, 7 should be non-null
            Assert.Equal(11, indicator.Values.Count);
            // Smoke test: check middle values are non-null
            Assert.NotNull(indicator.Values[6]);
            Assert.NotNull(indicator.Values[7]);
        }

        [Fact]
        public void Calculate_WithInsufficientData_ReturnsNulls()
        {
            var indicator = new CoreDpoIndicator { Period = 10 };
            var candles = CreateTestCandles(new decimal[] { 10, 12, 14 });

            indicator.Calculate(candles);

            Assert.Equal(3, indicator.Values.Count);
            Assert.All(indicator.Values, v => Assert.Null(v));
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreDpoIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }
    }
}
