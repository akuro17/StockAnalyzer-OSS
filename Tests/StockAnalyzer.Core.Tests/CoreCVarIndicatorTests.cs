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

        // 60 bars without a missing value; deterministic, non-monotonic closes.
        private static decimal[] SixtyCloses() => Enumerable.Range(0, 60).Select(i => 100m + (i * 7 % 13) + i * 0.5m).ToArray();

        // Independent re-computation of the documented rule: window of Period returns ending at bar b, ascending sort,
        // index floor((1 - confidence) * Period) clamped to [0, Period - 1], mean of the first index + 1 values.
        private static decimal? ExpectedCVar(decimal[] closes, int b, int period, double confidence)
        {
            if (b < period) return null;
            var window = new List<decimal>();
            for (int k = b - period + 1; k <= b; k++) window.Add((closes[k] - closes[k - 1]) / closes[k - 1]);
            window.Sort();
            int index = (int)Math.Floor((1 - confidence) * period);
            index = Math.Clamp(index, 0, period - 1);
            return window.Take(index + 1).Sum() / (index + 1);
        }

        [Theory]
        [InlineData(10, 0.95)]
        [InlineData(20, 0.95)]
        [InlineData(15, 0.80)]
        public void Calculate_WithoutMissingValues_MatchesTheDocumentedRule(int period, double confidence)
        {
            var closes = SixtyCloses();
            var result = new CoreCVarIndicator { Period = period, ConfidenceLevel = confidence }.Calculate(CreateTestCandles(closes));

            Assert.Equal(closes.Length, result.MainValues.Count);
            for (int b = 0; b < closes.Length; b++)
            {
                var expected = ExpectedCVar(closes, b, period, confidence);
                if (expected is null) { Assert.Null(result.MainValues[b]); continue; }
                Assert.NotNull(result.MainValues[b]);
                Assert.Equal(expected.Value, result.MainValues[b]!.Value, 12);
            }
        }

        [Fact]
        public void Calculate_WithAZeroPriceBar_IsNullInEveryWindowThatContainsTheUndefinedReturn()
        {
            const int period = 10;
            const int zeroBar = 30;
            var closes = SixtyCloses();
            closes[zeroBar] = 0m;
            var candles = closes.Select((close, i) => new CoreCandleData(DateTime.Today.AddDays(i), close, close, close, close, 1000)).ToList();

            var result = new CoreCVarIndicator { Period = period, ConfidenceLevel = 0.95 }.Calculate(candles);

            // The return of bar zeroBar + 1 divides by the zero price (undefined); it belongs to the windows ending at zeroBar + 1 .. zeroBar + period.
            for (int b = 0; b < closes.Length; b++)
            {
                bool inUndefinedWindow = b >= zeroBar + 1 && b <= zeroBar + period;
                if (b < period || inUndefinedWindow) Assert.Null(result.MainValues[b]);
                else Assert.NotNull(result.MainValues[b]);
            }
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
