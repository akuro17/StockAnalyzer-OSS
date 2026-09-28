using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreKstIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(IEnumerable<decimal> closePrices)
        {
            var startDate = DateTime.Today;
            return closePrices.Select((price, i) => new CoreCandleData(
                startDate.AddDays(i), price, price, price, price, 1000
            )).ToList();
        }

        [Fact]
        public void Calculate_WithSufficientData_ReturnsNonEmptyValues()
        {
            // KST calculation is long and complex, making manual verification tedious and error-prone.
            // This test verifies that with sufficient data, the indicator produces non-null output.
            // A more precise test would require a known-good implementation or test data from a trusted source.
            var indicator = new CoreKstIndicator();
            // Need at least ROC(30) + SMA(15) = 44 periods for the first value.
            var candles = CreateTestCandles(Enumerable.Range(1, 50).Select(i => (decimal)(i + Math.Sin(i) * 5)));

            indicator.Calculate(candles);

            Assert.Equal(50, indicator.Values.Count);
            // KST needs ROC(30)+SMA(15) warmup - smoke test: check we have some non-null values at end
            Assert.NotNull(indicator.Values[49]);
            // First several values should be null
            Assert.All(indicator.Values.Take(30), Assert.Null);
        }

        [Fact]
        public void Calculate_WithInsufficientData_ReturnsAllNulls()
        {
            var indicator = new CoreKstIndicator();
            var candles = CreateTestCandles(new decimal[] { 10, 12, 14, 13, 15, 16, 14, 12 });

            indicator.Calculate(candles);

            Assert.Equal(8, indicator.Values.Count);
            Assert.All(indicator.Values, v => Assert.Null(v));
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreKstIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }
    }
}
