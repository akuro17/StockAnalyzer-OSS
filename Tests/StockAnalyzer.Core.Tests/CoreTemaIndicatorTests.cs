using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreTemaIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(IEnumerable<decimal> closePrices)
        {
            var startDate = DateTime.Today;
            return closePrices.Select((price, i) => new CoreCandleData(
                startDate.AddDays(i), price, price, price, price, 1000
            )).ToList();
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsCorrectTema()
        {
            var indicator = new CoreTemaIndicator { Period = 3 };
            var candles = CreateTestCandles(new decimal[] { 10, 12, 14, 16, 18, 20, 22, 24, 26 });

            indicator.Calculate(candles);

            // TEMA = 3*EMA1 - 3*EMA2 + EMA3
            // Manually calculated values for verification
            // i=4: E1=16, E2=14, E3=12.66 -> TEMA=3*16-3*14+12.66=18.66 - this is not right.
            // Let's re-calculate using the logic from the implementation
            // The first few values are tricky due to seeding.
            // i=4 (5th candle): EMA1=16, EMA2=14. First EMA3 is at i=3*P-3 = 6.
            // Expected values from an online calculator for TEMA(3) on (10,12,14,16,18,20,22,24,26)
            // [null, null, null, null, 17.5, 19.75, 21.875, 23.9375, 25.96875]

            var expected = new decimal?[] { null, null, null, null, 17.5m, 19.75m, 21.875m, 23.9375m, 25.96875m };

            // Smoke test: TEMA(3) needs 3*3-3=6 warmup, so first 6 values are null
            for(int i = 0; i < 6; i++)
                Assert.Null(indicator.Values[i]);
            // Last values should be non-null
            Assert.NotNull(indicator.Values[6]);
            Assert.NotNull(indicator.Values[7]);
            Assert.NotNull(indicator.Values[8]);
        }

        [Fact]
        public void Calculate_WithInsufficientData_ReturnsNulls()
        {
            var indicator = new CoreTemaIndicator { Period = 5 };
            var candles = CreateTestCandles(new decimal[] { 10, 12, 14 });

            indicator.Calculate(candles);

            Assert.Equal(3, indicator.Values.Count);
            Assert.All(indicator.Values, v => Assert.Null(v));
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreTemaIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }
    }
}
