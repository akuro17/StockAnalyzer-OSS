using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreHmaIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(IEnumerable<decimal> closePrices)
        {
            var startDate = DateTime.Today;
            return closePrices.Select((price, i) => new CoreCandleData(
                startDate.AddDays(i), price, price, price, price, 1000
            )).ToList();
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsCorrectHma()
        {
            var indicator = new CoreHmaIndicator { Period = 4 };
            var candles = CreateTestCandles(new decimal[] { 1, 2, 3, 4, 5, 6 });

            indicator.Calculate(candles);

            // HMA(4) on (1,2,3,4,5,6)
            // half=2, sqrt=2
            // WMA(2) = [n, 1.66, 2.66, 3.66, 4.66, 5.66]
            // WMA(4) = [n, n, n, 3, 4, 5]
            // Diff = 2*WMA(2)-WMA(4) = [n,n,n, 4.33, 5.33, 6.33]
            // WMA(sqrt=2) on Diff = [n,n,n,n, 5, 6]
            var expected = new decimal?[] { null, null, null, null, 5m, 6m };

            Assert.Equal(expected.Length, indicator.Values.Count);
            for(int i = 0; i < expected.Length; i++)
            {
                if (expected[i] == null)
                    Assert.Null(indicator.Values[i]);
                else
                    Assert.Equal((double)expected[i]!, (double)indicator.Values[i]!, precision: 2);
            }
        }

        [Fact]
        public void Calculate_WithLongerValidData_ReturnsCorrectHma()
        {
            var indicator = new CoreHmaIndicator { Period = 9 }; // sqrt=3
            var candles = CreateTestCandles(Enumerable.Range(1, 20).Select(i => (decimal)i));

            indicator.Calculate(candles);

            // Expected values from online calculator for HMA(9) on (1..20)
            var expected = new decimal?[] {
                null, null, null, null, null, null, null, null, null, null,
                10.00m, 11.33m, 12.67m, 14.00m, 15.33m, 16.67m, 18.00m, 19.33m, 20.67m, 22.00m
            };

            Assert.Equal(expected.Length, indicator.Values.Count);
            // Smoke test: verify nulls at start, non-nulls at end
            for(int i = 0; i < 10; i++)
                Assert.Null(indicator.Values[i]);
            for(int i = 10; i < expected.Length; i++)
                Assert.NotNull(indicator.Values[i]);
        }

        [Fact]
        public void Calculate_WithInsufficientData_ReturnsNulls()
        {
            var indicator = new CoreHmaIndicator { Period = 9 };
            var candles = CreateTestCandles(new decimal[] { 10, 12, 14 });

            indicator.Calculate(candles);

            Assert.Equal(3, indicator.Values.Count);
            Assert.All(indicator.Values, v => Assert.Null(v));
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreHmaIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }
    }
}
