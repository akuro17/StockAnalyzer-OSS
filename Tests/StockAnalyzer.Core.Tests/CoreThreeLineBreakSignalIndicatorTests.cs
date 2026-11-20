using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
#pragma warning disable CS0618 // Deliberately testing obsolete ThreeLineBreakSignal
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

        [Fact]
        public void Calculate_FlatPrices_ProducesHistogramSeries()
        {
            var tlb = new CoreThreeLineBreakSignalIndicator();
            var baseDate = DateTime.UtcNow.Date;
            var flatCandles = new List<CoreCandleData>();
            for (int i = 0; i < 10; i++)
            {
                flatCandles.Add(new(baseDate.AddDays(i), 100m, 100m, 100m, 100m, 1000));
            }

            var result = tlb.Calculate(flatCandles);
            Assert.True(result.IsSuccessful);
            Assert.True(result.HasSeries("Histogram"));
            Assert.Equal(10, result.GetSeries("Histogram").Count);
        }
    }
#pragma warning restore CS0618
}
