using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreVortexSignalIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(IEnumerable<decimal> closePrices)
        {
            var startDate = DateTime.Today;
            return closePrices.Select((price, i) => new CoreCandleData(
                startDate.AddDays(i), price, price + 5, price - 5, price, 1000
            )).ToList();
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreVortexSignalIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }

        [Fact]
        public void Calculate_WithSampleData_ReturnsValues()
        {
            var indicator = new CoreVortexSignalIndicator();
            var candles = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14, 15, 16 });
            indicator.Calculate(candles);
            Assert.Equal(candles.Count, indicator.Values.Count);
        }

        [Fact]
        public void Calculate_AggregatesExactPeriodTransitions_IncludingCurrentBar()
        {
            var indicator = new CoreVortexSignalIndicator { Period = 2 };
            var baseDate = DateTime.Today;
            var candles = new List<CoreCandleData>
            {
                new CoreCandleData(baseDate, 9m, 10m, 8m, 9m, 100),
                new CoreCandleData(baseDate.AddDays(1), 11m, 12m, 10m, 11m, 100),
                new CoreCandleData(baseDate.AddDays(2), 14m, 15m, 13m, 14m, 100)
            };

            var result = indicator.Calculate(candles);

            Assert.True(result.IsSuccessful);
            Assert.Equal(3, indicator.Values.Count);
            Assert.Null(indicator.Values[0]);
            Assert.Null(indicator.Values[1]);
            Assert.Equal(1m, indicator.Values[2]);
        }
    }
}
