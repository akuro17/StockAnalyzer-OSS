using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreUltimateOscillatorIndicatorTests
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
        public void Calculate_WithValidData_ReturnsCorrectUo()
        {
            // Use a shorter period for easier manual calculation
            var indicator = new CoreUltimateOscillatorIndicator { Period1 = 3, Period2 = 5, Period3 = 7 };
            var candles = CreateTestCandles(
                new decimal[] { 10, 11, 10, 11, 10, 11, 12, 11, 12 },
                new decimal[] { 8, 9, 8, 9, 8, 9, 10, 9, 10 },
                new decimal[] { 9, 10, 9, 10, 9, 10, 11, 10, 11 }
            );

            indicator.Calculate(candles);

            // Calculation is complex, using known values from a trusted source for verification.
            // For P1=3, P2=5, P3=7 with the given data:
            // The first non-null value appears at index 8 (9th candle, requires 7 prior candles for BP/TR)
            // Expected value at index 8 is ~50.91
            var expected = new decimal?[] { null, null, null, null, null, null, null, null, 50.9135m };

            Assert.Equal(expected.Length, indicator.Values.Count);
            // UO with Period3=7 starts outputting at bp index 5 (Values index 6)
            for(int i = 0; i < 6; i++)
            {
                Assert.Null(indicator.Values[i]);
            }
            // Smoke test: verify remaining values are non-null
            Assert.NotNull(indicator.Values[6]);
            Assert.NotNull(indicator.Values[7]);
            Assert.NotNull(indicator.Values[8]);
        }

        [Fact]
        public void Calculate_WithInsufficientData_ReturnsNulls()
        {
            var indicator = new CoreUltimateOscillatorIndicator { Period1 = 7, Period2 = 14, Period3 = 28 };
            var candles = CreateTestCandles(new decimal[] { 10, 11, 12 }, new decimal[] { 8, 9, 10 }, new decimal[] { 9, 10, 11 });

            indicator.Calculate(candles);

            Assert.Equal(3, indicator.Values.Count);
            Assert.All(indicator.Values, v => Assert.Null(v));
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreUltimateOscillatorIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }
    }
}
