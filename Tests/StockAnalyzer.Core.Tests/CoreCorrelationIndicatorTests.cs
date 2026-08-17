using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Statistics;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreCorrelationIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(decimal[] closes)
        {
            var startDate = DateTime.Today;
            return closes.Select((close, i) => new CoreCandleData(
                startDate.AddDays(i),
                i > 0 ? closes[i-1] : close,
                close + 1,
                close - 1,
                close,
                1000
            )).ToList();
        }

        [Fact]
        public void Calculate_WithPerfectlyCorrelatedData_ReturnsOne()
        {
            var seriesA = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14 });
            var seriesB = CreateTestCandles(new decimal[] { 20, 22, 24, 26, 28 });
            var indicator = new CoreCorrelationIndicator(5, seriesB);
            indicator.Calculate(seriesA);

            Assert.NotNull(indicator.Values.Last());
            Assert.Equal(1, indicator.Values.Last().Value, 4);
        }

        [Fact]
        public void Calculate_WithPerfectlyInverselyCorrelatedData_ReturnsMinusOne()
        {
            var seriesA = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14 });
            var seriesB = CreateTestCandles(new decimal[] { 30, 28, 26, 24, 22 });
            var indicator = new CoreCorrelationIndicator(5, seriesB);
            indicator.Calculate(seriesA);

            Assert.NotNull(indicator.Values.Last());
            Assert.Equal(-1, indicator.Values.Last().Value, 4);
        }

        [Fact]
        public void Calculate_WithUncorrelatedData_ReturnsZero()
        {
            var seriesA = CreateTestCandles(new decimal[] { 10, 10, 10, 10, 10 });
            var seriesB = CreateTestCandles(new decimal[] { 20, 22, 21, 23, 20 });
            var indicator = new CoreCorrelationIndicator(5, seriesB);
            indicator.Calculate(seriesA);

            Assert.NotNull(indicator.Values.Last());
            Assert.Equal(0, indicator.Values.Last().Value, 4);
        }

        [Fact]
        public void Calculate_WithDifferentLengths_HandlesCorrectly()
        {
            var seriesA = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14, 15 });
            var seriesB = CreateTestCandles(new decimal[] { 20, 22, 24, 26, 28 });
            var indicator = new CoreCorrelationIndicator(5, seriesB);
            indicator.Calculate(seriesA);

            Assert.Equal(6, indicator.Values.Count);
            Assert.NotNull(indicator.Values[4]);
            Assert.Equal(1, indicator.Values[4].Value, 4);
            Assert.Null(indicator.Values[5]);
        }
    }
}
