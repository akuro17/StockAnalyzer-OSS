using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreGarmanKlassVolatilityIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(IEnumerable<decimal> closePrices)
        {
            var startDate = DateTime.Today;
            return closePrices.Select((price, i) => new CoreCandleData(
                startDate.AddDays(i), price, price + 5, price - 5, price, 1000
            )).ToList();
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreGarmanKlassVolatilityIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }

        [Fact]
        public void Calculate_WithSampleData_ReturnsValues()
        {
            var indicator = new CoreGarmanKlassVolatilityIndicator();
            var candles = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14, 15, 16 });
            indicator.Calculate(candles);
            Assert.Equal(candles.Count, indicator.Values.Count);
        }

        [Fact]
        public void Calculate_WithNegativeCloseInOneBar_KeepsSeriesAndNullsOnlyThatBar()
        {
            var indicator = new CoreGarmanKlassVolatilityIndicator();
            var candles = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14 });
            candles[2] = candles[2] with { Close = -1m };

            var result = indicator.Calculate(candles);

            Assert.True(result.IsSuccessful);
            Assert.Equal(candles.Count, indicator.Values.Count);
            Assert.Null(indicator.Values[2]);
            foreach (var i in new[] { 0, 1, 3, 4 })
            {
                Assert.NotNull(indicator.Values[i]);
            }
        }

        [Fact]
        public void Calculate_WithNegativeHighInOneBar_KeepsSeriesAndNullsOnlyThatBar()
        {
            var indicator = new CoreGarmanKlassVolatilityIndicator();
            var candles = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14 });
            candles[3] = candles[3] with { High = -2m };

            var result = indicator.Calculate(candles);

            Assert.True(result.IsSuccessful);
            Assert.Equal(candles.Count, indicator.Values.Count);
            Assert.Null(indicator.Values[3]);
            Assert.NotNull(indicator.Values[4]);
        }

        [Fact]
        public void Calculate_ValidBars_MatchSingleBarFormula()
        {
            var indicator = new CoreGarmanKlassVolatilityIndicator();
            var candles = new List<CoreCandleData>
            {
                new(DateTime.Today, 100m, 104m, 98m, 103m, 1000),
                new(DateTime.Today.AddDays(1), 103m, 105m, 99m, 100m, 1000),
                new(DateTime.Today.AddDays(2), 100m, -3m, 99m, 101m, 1000),
                new(DateTime.Today.AddDays(3), 101m, 110m, 97m, 109m, 1000),
            };

            indicator.Calculate(candles);

            // Independent oracle: sigma = sqrt(0.5*ln(H/L)^2 - (2 ln2 - 1)*ln(C/O)^2), a fraction (not percent).
            foreach (var i in new[] { 0, 1, 3 })
            {
                var c = candles[i];
                var hl = Math.Log((double)c.High / (double)c.Low);
                var co = Math.Log((double)c.Close / (double)c.Open);
                var expected = Math.Sqrt(0.5 * hl * hl - (2.0 * Math.Log(2.0) - 1.0) * co * co);
                Assert.Equal(expected, (double)indicator.Values[i]!, 12);
            }
            Assert.Null(indicator.Values[2]);
        }
    }
}
