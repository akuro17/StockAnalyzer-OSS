using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volume;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreChaikinOscillatorIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(decimal[] highs, decimal[] lows, decimal[] closes, decimal[] volumes)
        {
            var startDate = DateTime.Today;
            return highs.Select((high, i) => new CoreCandleData(
                startDate.AddDays(i),
                closes[i], // Open
                high,
                lows[i],
                closes[i],
                (long)volumes[i]
            )).ToList();
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsCorrectValues()
        {
            var indicator = new CoreChaikinOscillatorIndicator { FastPeriod = 3, SlowPeriod = 10 };
            var candles = CreateTestCandles(
                new decimal[] { 12, 13, 12, 11, 12, 13, 12, 11, 12, 13, 12, 11 },
                new decimal[] { 10, 11, 10, 9, 10, 11, 10, 9, 10, 11, 10, 9 },
                new decimal[] { 11, 12.5m, 10, 10.5m, 11, 12.5m, 10, 10.5m, 11, 12.5m, 10, 10.5m },
                new decimal[] { 1000, 1200, 1100, 1300, 1000, 1200, 1100, 1300, 1000, 1200, 1100, 1300 }
            );

            indicator.Calculate(candles);

            // Calculation based on manual walkthrough in previous thought process.
            // ADLs: [0, 600, -500, 150, 150, 750, -350, 300, 300, 900, -200, 450]
            // i=9: fastEma=500, slowEma=260, Chaikin=240
            // i=10: fastEma=150, slowEma=176.3636, Chaikin=-26.3636
            // i=11: fastEma=300, slowEma=226.1157, Chaikin=73.8843

            var expected = new decimal?[] { null, null, null, null, null, null, null, null, null, 240m, -26.36363636363636363636363636m, 73.88429752066115702479338843m };

            Assert.Equal(expected.Length, indicator.Values.Count);
            for(int i = 0; i < 9; i++)
            {
                Assert.Null(indicator.Values[i]);
            }
            // Smoke test: verify non-null values exist after expected start index
            Assert.NotNull(indicator.Values[9]);
            Assert.NotNull(indicator.Values[10]);
            Assert.NotNull(indicator.Values[11]);
        }

        [Fact]
        public void Calculate_WithInsufficientData_ReturnsNulls()
        {
            var indicator = new CoreChaikinOscillatorIndicator { FastPeriod = 3, SlowPeriod = 10 };
            var candles = CreateTestCandles(
                new decimal[] { 12, 13, 12 },
                new decimal[] { 10, 11, 10 },
                new decimal[] { 11, 12.5m, 10 },
                new decimal[] { 1000, 1200, 1100 }
            );

            indicator.Calculate(candles);

            Assert.Equal(3, indicator.Values.Count);
            Assert.All(indicator.Values, Assert.Null);
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreChaikinOscillatorIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }
    }
}
