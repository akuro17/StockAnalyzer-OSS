using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volume;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CorePvtIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(decimal[] closes, long[] volumes)
        {
            var startDate = DateTime.Today;
            return closes.Select((close, i) => new CoreCandleData(
                startDate.AddDays(i),
                i > 0 ? closes[i-1] : close,
                close + 1,
                close - 1,
                close,
                volumes[i]
            )).ToList();
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsCorrectPvt()
        {
            var indicator = new CorePvtIndicator();
            var candles = CreateTestCandles(
                new decimal[] { 100, 102, 101, 103, 103 },
                new long[] { 1000, 1100, 1200, 1300, 1400 }
            );

            indicator.Calculate(candles);

            // 1. 0
            // 2. 0 + (102-100)/100 * 1100 = 22
            // 3. 22 + (101-102)/102 * 1200 = 10.235
            // 4. 10.235 + (103-101)/101 * 1300 = 35.97
            // 5. 35.97 + (103-103)/103 * 1400 = 35.97
            var expected = new decimal?[]
            {
                0m,
                22m,
                10.235294117647058823529411765m,
                35.977868375072801397786837507m,
                35.977868375072801397786837507m
            };

            Assert.Equal(expected.Length, indicator.Values.Count);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i].Value, indicator.Values[i].Value, 8);
            }
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CorePvtIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }
    }
}
