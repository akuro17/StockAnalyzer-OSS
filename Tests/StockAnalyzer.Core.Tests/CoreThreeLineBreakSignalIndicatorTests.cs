using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreThreeLineBreakSignalIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(IEnumerable<decimal> closePrices)
        {
            var startDate = DateTime.Today;
            return closePrices.Select((price, i) => new CoreCandleData(
                startDate.AddDays(i), price, price + 5, price - 5, price, 1000
            )).ToList();
        }

        [Fact]
        public void Calculate_ReturnsSuccess_DetectsUptrend()
        {
            var indicator = new CoreThreeLineBreakSignalIndicator();
            var candles = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14 });
            var result = indicator.Calculate(candles);
            
            Assert.True(result.IsSuccessful);
            Assert.True(result.HasSeries("Histogram"));
            Assert.Equal(5, result.GetSeries("Histogram").Count);
        }
    }
}
