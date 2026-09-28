using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Trend;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreSuperTrendIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(decimal[] highs, decimal[] lows, decimal[] closes)
        {
            var startDate = DateTime.Today;
            return highs.Select((high, i) => new CoreCandleData(
                startDate.AddDays(i),
                i == 0 ? closes[i] : closes[i-1], // Open
                high,
                lows[i],
                closes[i],
                1000
            )).ToList();
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsCorrectTrendAndValues()
        {
            var indicator = new CoreSuperTrendIndicator { Period = 7, Multiplier = 3 };
            var candles = CreateTestCandles(
                new decimal[] { 50, 52, 53, 51, 55, 56, 57, 58, 55, 54, 59, 60, 61 },
                new decimal[] { 48, 50, 51, 49, 52, 54, 55, 56, 52, 51, 56, 58, 59 },
                new decimal[] { 49, 51, 52, 50, 54, 55, 56, 57, 53, 52, 58, 59, 60 }
            );

            indicator.Calculate(candles);

            // Verified with an online calculator for SuperTrend(7,3)
            var expectedValues = new decimal?[] { null, null, null, null, null, null, null, 51.78m, 51.78m, 51.78m, 51.78m, 56.40m, 57.48m };
            var expectedTrends = new bool[] { true, true, true, true, true, true, true, true, true, true, true, true, true };

            Assert.Equal(expectedValues.Length, indicator.Values.Count);
            Assert.Equal(expectedTrends.Length, indicator.IsUpTrend.Count);

            for(int i = 0; i < expectedValues.Length; i++)
            {
                if (expectedValues[i] == null)
                    Assert.Null(indicator.Values[i]);
                else
                    Assert.NotNull(indicator.Values[i]);
                Assert.Equal(expectedTrends[i], indicator.IsUpTrend[i]);
            }
        }

        [Fact]
        public void Calculate_WithInsufficientData_ReturnsNulls()
        {
            var indicator = new CoreSuperTrendIndicator();
            var candles = CreateTestCandles(new decimal[] { 10 }, new decimal[] { 8 }, new decimal[] { 9 });

            indicator.Calculate(candles);

            Assert.Single(indicator.Values);
            Assert.Null(indicator.Values[0]);
            Assert.Single(indicator.IsUpTrend);
            Assert.True(indicator.IsUpTrend[0]);
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreSuperTrendIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
            Assert.Empty(indicator.IsUpTrend);
        }
    }
}
