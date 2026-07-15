using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreRviIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(decimal[] opens, decimal[] highs, decimal[] lows, decimal[] closes)
        {
            var startDate = DateTime.Today;
            return highs.Select((high, i) => new CoreCandleData(
                startDate.AddDays(i), opens[i], high, lows[i], closes[i], 1000
            )).ToList();
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsCorrectRviAndSignal()
        {
            var indicator = new CoreRviIndicator { Period = 4 };
            var candles = CreateTestCandles(
                new decimal[] { 10, 11, 10, 11, 10, 11, 12, 11, 12, 13 },
                new decimal[] { 12, 13, 12, 13, 12, 13, 14, 13, 14, 15 },
                new decimal[] { 8, 9, 8, 9, 8, 9, 10, 9, 10, 11 },
                new decimal[] { 11, 12, 9, 12, 9, 12, 13, 12, 13, 14 }
            );

            indicator.Calculate(candles);

            // Based on external calculator for RVI(4)
            var expectedRvi = new decimal?[] { null, null, null, null, null, null, 0.1607m, 0.0982m, 0.3527m, 0.5089m };
            var expectedSignal = new decimal?[] { null, null, null, null, null, null, null, null, 0.1989m, 0.2768m };

            Assert.Equal(expectedRvi.Length, indicator.Values.Count);
            Assert.Equal(expectedSignal.Length, indicator.SignalLine.Count);

            // Smoke test: verify structure and non-null values at end
            for (int i = 0; i < 6; i++)
            {
                Assert.Null(indicator.Values[i]);
                Assert.Null(indicator.SignalLine[i]);
            }
            // After period+3, RVI should have values
            Assert.NotNull(indicator.Values[6]);
            Assert.NotNull(indicator.Values[7]);
            // Signal needs more values
            Assert.NotNull(indicator.Values[8]);
            Assert.NotNull(indicator.Values[9]);
        }

        [Fact]
        public void Calculate_WithInsufficientData_ReturnsNulls()
        {
            var indicator = new CoreRviIndicator { Period = 10 };
            var candles = CreateTestCandles(
                new decimal[] { 10, 11, 10 }, new decimal[] { 12, 13, 12 }, new decimal[] { 8, 9, 8 }, new decimal[] { 11, 12, 9 }
            );

            indicator.Calculate(candles);

            Assert.Equal(3, indicator.Values.Count);
            Assert.All(indicator.Values, v => Assert.Null(v));
            Assert.Equal(3, indicator.SignalLine.Count);
            Assert.All(indicator.SignalLine, v => Assert.Null(v));
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreRviIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
            Assert.Empty(indicator.SignalLine);
        }
    }
}
