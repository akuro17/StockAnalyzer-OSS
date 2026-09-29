using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreDonchianChannelIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(IEnumerable<decimal> closePrices)
        {
            var startDate = DateTime.Today;
            return closePrices.Select((price, i) => new CoreCandleData(
                startDate.AddDays(i), price, price + 10, price - 10, price, 1000
            )).ToList();
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreDonchianChannelIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }

        [Fact]
        public void Calculate_UsesActualHighLow_RegardlessOfPriceSource()
        {
            // Close: 10,11,12,13,14 => High (Close+10): 20..24, Low (Close-10): 0..4
            // Donchian is defined against the real High/Low, not a user-selectable Price Source
            // (verified unchanged after the RollingExtremeHelper DRY refactor).
            var indicator = new CoreDonchianChannelIndicator { Period = 3 };
            var candles = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14 });

            indicator.Calculate(candles);

            Assert.Null(indicator.UpperBand[1]);
            Assert.Equal(22m, indicator.UpperBand[2]);
            Assert.Equal(23m, indicator.UpperBand[3]);
            Assert.Equal(24m, indicator.UpperBand[4]);

            Assert.Null(indicator.LowerBand[1]);
            Assert.Equal(0m, indicator.LowerBand[2]);
            Assert.Equal(1m, indicator.LowerBand[3]);
            Assert.Equal(2m, indicator.LowerBand[4]);

            // Main series (Values) is the midpoint of Upper/Lower.
            Assert.Equal(11m, indicator.Values[2]);
            Assert.Equal(12m, indicator.Values[3]);
            Assert.Equal(13m, indicator.Values[4]);
        }
    }
}
