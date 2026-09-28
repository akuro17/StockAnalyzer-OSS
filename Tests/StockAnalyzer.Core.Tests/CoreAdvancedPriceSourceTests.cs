using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Tests
{
    /// <summary>
    /// Verifies that the Advanced-category indicators fixed to honor Price Source
    /// (previously hardcoded to Close) actually change output when Price Source changes,
    /// while none of them had prior dedicated tests to catch a regression here.
    /// </summary>
    public class CoreAdvancedPriceSourceTests
    {
        private static List<CoreCandleData> CreateTestCandles(int count)
        {
            // Close is a steady ramp; Open is a sawtooth on a different base so that
            // shape-sensitive statistics (slope, std-dev-based Z-score, etc.) genuinely
            // differ between the two Price Sources, not just a parallel-shifted value.
            var startDate = DateTime.Today;
            var candles = new List<CoreCandleData>();
            for (int i = 0; i < count; i++)
            {
                decimal close = 10m + i;
                decimal open = 500m + (i % 5) * 20m;
                candles.Add(new CoreCandleData(startDate.AddDays(i), open, close + 10, close - 10, close, 1000));
            }
            return candles;
        }

        private static void AssertPriceSourceIsEffective(CoreIndicatorBase closeBased, CoreIndicatorBase openBased, List<CoreCandleData> candles)
        {
            closeBased.PriceSource = PriceType.Close;
            openBased.PriceSource = PriceType.Open;

            closeBased.Calculate(candles);
            openBased.Calculate(candles);

            int lastIndex = candles.Count - 1;
            Assert.NotNull(closeBased.Values[lastIndex]);
            Assert.NotNull(openBased.Values[lastIndex]);
            Assert.NotEqual(closeBased.Values[lastIndex], openBased.Values[lastIndex]);
        }

        [Fact]
        public void WilderSmoothing_PriceSourceIsEffective()
        {
            var candles = CreateTestCandles(30);
            AssertPriceSourceIsEffective(
                new CoreWilderSmoothingIndicator { Period = 5 },
                new CoreWilderSmoothingIndicator { Period = 5 },
                candles);
        }

        [Fact]
        public void CoppockCurve_PriceSourceIsEffective()
        {
            var candles = CreateTestCandles(40);
            AssertPriceSourceIsEffective(
                new CoreCoppockCurveIndicator { LongRocPeriod = 5, ShortRocPeriod = 3, WmaPeriod = 3 },
                new CoreCoppockCurveIndicator { LongRocPeriod = 5, ShortRocPeriod = 3, WmaPeriod = 3 },
                candles);
        }

        [Fact]
        public void ZScore_PriceSourceIsEffective()
        {
            var candles = CreateTestCandles(30);
            AssertPriceSourceIsEffective(
                new CoreZScoreIndicator { Period = 5 },
                new CoreZScoreIndicator { Period = 5 },
                candles);
        }

        [Fact]
        public void RegressionSlope_PriceSourceIsEffective()
        {
            var candles = CreateTestCandles(30);
            AssertPriceSourceIsEffective(
                new CoreRegressionSlopeIndicator { Period = 5 },
                new CoreRegressionSlopeIndicator { Period = 5 },
                candles);
        }

        [Fact]
        public void LinearRegression_PriceSourceIsEffective()
        {
            var candles = CreateTestCandles(30);
            AssertPriceSourceIsEffective(
                new CoreLinearRegressionIndicator { Period = 5 },
                new CoreLinearRegressionIndicator { Period = 5 },
                candles);
        }

        [Fact]
        public void KylesLambda_PriceSourceIsEffective()
        {
            var candles = CreateTestCandles(30);
            AssertPriceSourceIsEffective(
                new CoreKylesLambdaIndicator { Period = 5 },
                new CoreKylesLambdaIndicator { Period = 5 },
                candles);
        }
    }
}
