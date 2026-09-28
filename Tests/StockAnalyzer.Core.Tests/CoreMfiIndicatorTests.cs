using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volume;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreMfiIndicatorTests
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
        public void Calculate_WithValidData_ReturnsCorrectMfi()
        {
            var indicator = new CoreMfiIndicator { Period = 3 };
            var candles = CreateTestCandles(
                new decimal[] { 10, 11, 10, 11, 10 },
                new decimal[] { 8, 9, 8, 9, 8 },
                new decimal[] { 9, 10, 9, 10, 9 },
                new decimal[] { 100, 110, 120, 130, 140 }
            );

            indicator.Calculate(candles);

            // Expected values calculated manually
            // i=3: pos=2400, neg=1080 -> MFI = 100 - (100 / (1 + 2400/1080)) = 68.9655
            // i=4: pos=1300, neg=2340 -> MFI = 100 - (100 / (1 + 1300/2340)) = 35.7142
            var expected = new decimal?[] { null, null, null, 68.96551724137931034482758621m, 35.71428571428571428571428571m };

            Assert.Equal(expected.Length, indicator.Values.Count);
            Assert.Null(indicator.Values[0]);
            Assert.Null(indicator.Values[1]);
            Assert.Null(indicator.Values[2]);
            Assert.Equal((double)expected[3]!, (double)indicator.Values[3]!, 8);
            Assert.Equal((double)expected[4]!, (double)indicator.Values[4]!, 8);
        }

        [Fact]
        public void Calculate_WithOnlyPositiveFlow_Returns100()
        {
            var indicator = new CoreMfiIndicator { Period = 3 };
            var candles = CreateTestCandles(
                new decimal[] { 10, 11, 12, 13, 14 },
                new decimal[] { 8, 9, 10, 11, 12 },
                new decimal[] { 9, 10, 11, 12, 13 },
                new decimal[] { 100, 110, 120, 130, 140 }
            );

            indicator.Calculate(candles);

            Assert.Equal(100m, indicator.Values[3]);
            Assert.Equal(100m, indicator.Values[4]);
        }

        [Fact]
        public void Calculate_WithInsufficientData_ReturnsNulls()
        {
            var indicator = new CoreMfiIndicator { Period = 5 };
            var candles = CreateTestCandles(
                new decimal[] { 10, 11, 12 }, new decimal[] { 8, 9, 10 }, new decimal[] { 9, 10, 11 }, new decimal[] { 100, 110, 120 }
            );

            indicator.Calculate(candles);

            Assert.Equal(3, indicator.Values.Count);
            Assert.All(indicator.Values, v => Assert.Null(v));
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreMfiIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }
    }
}
