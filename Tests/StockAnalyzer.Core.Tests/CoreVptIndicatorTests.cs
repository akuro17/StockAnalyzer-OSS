using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volume;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreVptIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(decimal[] closes, long[] volumes)
        {
            var startDate = DateTime.Today;
            return closes.Select((close, i) => new CoreCandleData(
                startDate.AddDays(i),
                i > 0 ? closes[i-1] : close, // Open
                close + 1, // High
                close - 1, // Low
                close,
                volumes[i]
            )).ToList();
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsCorrectVpt()
        {
            var indicator = new CoreVptIndicator();
            var candles = CreateTestCandles(
                new decimal[] { 100, 102, 101, 103, 103 },
                new long[] { 1000, 1100, 1200, 1300, 1400 }
            );

            indicator.Calculate(candles);

            var expected = new decimal?[]
            {
                0,
                22,
                10.235294117647058823529411765M,
                35.977868375072801397786837507M,
                35.977868375072801397786837507M
            };

            Assert.Equal(expected.Length, indicator.Values.Count);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.True(expected[i].HasValue, $"Expected value at index {i} is null.");
                Assert.True(indicator.Values[i].HasValue, $"Indicator value at index {i} is null.");
                Assert.Equal(expected[i].Value, indicator.Values[i].Value, 8);
            }
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreVptIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }

        [Fact]
        public void Calculate_WithSingleCandle_ReturnsSingleZero()
        {
            var indicator = new CoreVptIndicator();
            var candles = CreateTestCandles(new decimal[] { 100 }, new long[] { 1000 });
            indicator.Calculate(candles);
            Assert.Single(indicator.Values);
            Assert.Equal(0, indicator.Values[0]);
        }
    }
}
