using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreCVarIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(decimal[] closes)
        {
            var startDate = DateTime.Today;
            return closes.Select((close, i) => new CoreCandleData(
                startDate.AddDays(i),
                i > 0 ? closes[i-1] : close,
                close + 1,
                close - 1,
                close,
                1000
            )).ToList();
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsCorrectCVar()
        {
            var indicator = new CoreCVarIndicator { Period = 20, ConfidenceLevel = 0.95 };
            var closes = new decimal[] {
                100, 101, 102, 101, 100, 99, 98, 99, 100, 101,
                102, 103, 102, 101, 100, 99, 98, 97, 96, 95, 94
            };
            var candles = CreateTestCandles(closes);
            var result = indicator.Calculate(candles);

            Assert.True(result.IsSuccessful);

            // VaR index is floor((1-0.95)*20) = 1.
            // CVaR is the average of returns at indices 0 and 1.
            // Smallest returns are (94-95)/95 = -0.010526... and (95-96)/96 = -0.010416...
            // Average is (-0.010526... + -0.010416...)/2 = -0.010471...
            var expectedLastValue = -0.01047149122807017543859649123M;

            Assert.Equal(closes.Length, result.MainValues.Count);
            Assert.Null(result.MainValues[19]);
            Assert.NotNull(result.MainValues[20]);
            Assert.Equal(expectedLastValue, result.MainValues[20].Value, 8);
        }

        [Fact]
        public void Calculate_WithNotEnoughData_ReturnsAllNulls()
        {
            var indicator = new CoreCVarIndicator { Period = 10 };
            var candles = CreateTestCandles(new decimal[] { 100, 101, 102, 103, 104 });
            var result = indicator.Calculate(candles);
            
            Assert.Equal(5, result.MainValues.Count);
            Assert.All(result.MainValues, v => Assert.Null(v));
        }
    }
}
