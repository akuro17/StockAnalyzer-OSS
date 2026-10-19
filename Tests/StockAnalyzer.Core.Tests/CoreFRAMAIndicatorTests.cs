using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreFRAMAIndicatorTests
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
            var indicator = new CoreFRAMAIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }

        [Fact]
        public void Calculate_WithSampleData_ReturnsValues()
        {
            var indicator = new CoreFRAMAIndicator();
            var candles = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14, 15, 16 });
            indicator.Calculate(candles);
            Assert.Equal(candles.Count, indicator.Values.Count);
        }

        [Fact]
        public void Calculate_WithPeriodOne_ReturnsNullsWithoutThrowing()
        {
            var indicator = new CoreFRAMAIndicator { Period = 1 };
            var candles = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14, 15, 16 });
            var result = indicator.Calculate(candles);

            Assert.True(result.IsSuccessful);
            Assert.Equal(candles.Count, indicator.Values.Count);
            Assert.All(indicator.Values, v => Assert.Null(v));
        }

        [Fact]
        public void Calculate_SeriesPath_WithPeriodOne_ReturnsNullsWithoutThrowing()
        {
            var indicator = new CoreFRAMAIndicator { Period = 1 };
            var series = new decimal?[] { 10m, 11m, 12m, 13m, 14m, 15m, 16m };
            var result = indicator.CalculateSeries(series);

            Assert.True(result.IsSuccessful);
            Assert.Equal(series.Length, indicator.Values.Count);
            Assert.All(indicator.Values, v => Assert.Null(v));
        }
    }
}
