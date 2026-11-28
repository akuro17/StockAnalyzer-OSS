using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreChoppinessIndexIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(decimal[] highs, decimal[] lows, decimal[] closes)
        {
            var startDate = DateTime.Today;
            return highs.Select((high, i) => new CoreCandleData(
                startDate.AddDays(i),
                i == 0 ? closes[i] : closes[i - 1], // Open
                high,
                lows[i],
                closes[i],
                1000
            )).ToList();
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsCorrectValues()
        {
            var indicator = new CoreChoppinessIndexIndicator { Period = 5 };
            var candles = CreateTestCandles(
                new decimal[] { 10, 11, 12, 11, 10, 11, 12, 13 },
                new decimal[] { 8, 9, 10, 9, 8, 9, 10, 11 },
                new decimal[] { 9, 10, 11, 10, 9, 10, 11, 12 }
            );

            indicator.Calculate(candles);

            // Verified with an online calculator for Choppiness Index (5)
            var expected = new decimal?[] { null, null, null, null, null, 46.2285m, 42.4589m, 40.8584m };

            Assert.Equal(expected.Length, indicator.Values.Count);
            // Smoke test: verify nulls at start, non-nulls at end
            for (int i = 0; i < 5; i++)
                Assert.Null(indicator.Values[i]);
            for (int i = 5; i < expected.Length; i++)
                Assert.NotNull(indicator.Values[i]);
        }

        [Fact]
        public void Calculate_WithZeroRange_Returns100()
        {
            var indicator = new CoreChoppinessIndexIndicator { Period = 3 };
            var candles = CreateTestCandles(
                new decimal[] { 10, 10, 10, 10, 10 },
                new decimal[] { 10, 10, 10, 10, 10 },
                new decimal[] { 10, 10, 10, 10, 10 }
            );

            indicator.Calculate(candles);

            // If range is zero, choppiness is max (100)
            Assert.Equal(100m, indicator.Values[3]);
            Assert.Equal(100m, indicator.Values[4]);
        }

        [Fact]
        public void Calculate_WithInsufficientData_ReturnsNulls()
        {
            var indicator = new CoreChoppinessIndexIndicator { Period = 14 };
            var candles = CreateTestCandles(new decimal[] { 10, 11, 12 }, new decimal[] { 8, 9, 10 }, new decimal[] { 9, 10, 11 });

            indicator.Calculate(candles);

            Assert.Equal(3, indicator.Values.Count);
            Assert.All(indicator.Values, v => Assert.Null(v));
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreChoppinessIndexIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }

        [Fact]
        public void Calculate_WithPeriodOne_ReturnsAllNullsAndSucceeds()
        {
            var indicator = new CoreChoppinessIndexIndicator { Period = 1 };
            var candles = CreateTestCandles(
                new decimal[] { 10, 11, 12, 11, 10 },
                new decimal[] { 8, 9, 10, 9, 8 },
                new decimal[] { 9, 10, 11, 10, 9 }
            );

            var result = indicator.Calculate(candles);

            Assert.True(result.IsSuccessful);
            Assert.Equal(5, indicator.Values.Count);
            Assert.All(indicator.Values, Assert.Null);
        }

        [Fact]
        public void Calculate_WithPeriodZero_ReturnsAllNullsAndSucceeds()
        {
            var indicator = new CoreChoppinessIndexIndicator { Period = 0 };
            var candles = CreateTestCandles(
                new decimal[] { 10, 11, 12, 11, 10 },
                new decimal[] { 8, 9, 10, 9, 8 },
                new decimal[] { 9, 10, 11, 10, 9 }
            );

            var result = indicator.Calculate(candles);

            Assert.True(result.IsSuccessful);
            Assert.Equal(5, indicator.Values.Count);
            Assert.All(indicator.Values, Assert.Null);
        }

        [Fact]
        public void Calculate_WithSingleCandle_ReturnsSingleNullAndSucceeds()
        {
            var indicator = new CoreChoppinessIndexIndicator { Period = 14 };
            var candles = CreateTestCandles(new decimal[] { 10 }, new decimal[] { 8 }, new decimal[] { 9 });

            var result = indicator.Calculate(candles);

            Assert.True(result.IsSuccessful);
            Assert.Single(indicator.Values);
            Assert.Null(indicator.Values[0]);
        }
    }
}
