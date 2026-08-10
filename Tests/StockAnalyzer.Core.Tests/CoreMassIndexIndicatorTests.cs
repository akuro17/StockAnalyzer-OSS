using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreMassIndexIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(decimal[] highs, decimal[] lows)
        {
            var startDate = DateTime.Today;
            return highs.Select((high, i) => new CoreCandleData(
                startDate.AddDays(i),
                (high+lows[i])/2, // Open
                high,
                lows[i],
                (high+lows[i])/2, // Close
                1000
            )).ToList();
        }

        [Fact]
        public void Calculate_WithSufficientData_ReturnsNonEmptyValues()
        {
            // Calculation is complex. Test that it produces non-null values with enough data.
            // EMA(9) of EMA(9) needs 9+9-2 = 16 data points for first ratio.
            // Sum of 25 ratios needs 16+25-1 = 40 data points for first Mass Index value.
            var indicator = new CoreMassIndexIndicator { Period = 25 };
            var highs = Enumerable.Range(1, 50).Select(i => (decimal)(i*1.1 + Math.Sin(i)));
            var lows = Enumerable.Range(1, 50).Select(i => (decimal)(i*0.9 - Math.Sin(i)));
            var candles = CreateTestCandles(highs.ToArray(), lows.ToArray());

            indicator.Calculate(candles);

            Assert.Equal(50, indicator.Values.Count);
            Assert.All(indicator.Values.Take(40), Assert.Null);
            Assert.NotNull(indicator.Values[40]);
            Assert.All(indicator.Values.Skip(40), v => Assert.NotNull(v));
        }

        [Fact]
        public void Calculate_WithInsufficientData_ReturnsAllNulls()
        {
            var indicator = new CoreMassIndexIndicator { Period = 25 };
            var candles = CreateTestCandles(new decimal[] { 10, 12, 14 }, new decimal[] { 8, 9, 10 });

            indicator.Calculate(candles);

            Assert.Equal(3, indicator.Values.Count);
            Assert.All(indicator.Values, v => Assert.Null(v));
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreMassIndexIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }
    }
}
