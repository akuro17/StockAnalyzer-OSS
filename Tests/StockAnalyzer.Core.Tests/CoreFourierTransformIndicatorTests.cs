using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreFourierTransformIndicatorTests
    {
        private static List<CoreCandleData> CreateSineCandles(int count, int period, decimal amplitude)
        {
            var candles = new List<CoreCandleData>();
            for (int i = 0; i < count; i++)
            {
                decimal mid = 100m + (decimal)(System.Math.Sin(2 * System.Math.PI * i / period) * (double)amplitude);
                candles.Add(new CoreCandleData(new System.DateTime(2023, 1, 1).AddDays(i), mid, mid + 1m, mid - 1m, mid, 1000));
            }
            return candles;
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreFourierTransformIndicator();
            indicator.Calculate(new List<CoreCandleData>());

            Assert.Empty(indicator.Values);
        }

        [Fact]
        public void Calculate_LeavesBarsBeforeFirstFullWindowEmpty()
        {
            const int period = 8;
            var candles = CreateSineCandles(30, period, 10m);
            var indicator = new CoreFourierTransformIndicator { TargetPeriod = period };
            var result = indicator.Calculate(candles);

            Assert.True(result.IsSuccessful, result.ErrorMessage);
            Assert.Equal(candles.Count, indicator.Values.Count);
            Assert.True(indicator.Values.Take(period - 1).All(v => v == null));
            Assert.True(indicator.Values.Skip(period - 1).All(v => v.HasValue));
        }

        [Fact]
        public void Calculate_SineAtTargetPeriod_ReturnsInputAmplitude()
        {
            const int period = 16;
            var candles = CreateSineCandles(100, period, 10m);
            var indicator = new CoreFourierTransformIndicator { TargetPeriod = period };
            indicator.Calculate(candles);

            Assert.InRange(indicator.Values[99]!.Value, 9.99m, 10.01m);
        }

        [Fact]
        public void Calculate_TargetPeriodBelowTwo_IsClampedToTwo()
        {
            var candles = CreateSineCandles(20, 4, 5m);
            var clamped = new CoreFourierTransformIndicator { TargetPeriod = 0 };
            var two = new CoreFourierTransformIndicator { TargetPeriod = 2 };
            clamped.Calculate(candles);
            two.Calculate(candles);

            Assert.Equal(two.Values, clamped.Values);
        }

        [Fact]
        public void Calculate_IsCausal_ValuesDoNotChangeWhenLaterBarsAreRemoved()
        {
            var candles = CreateSineCandles(60, 10, 7m);
            var full = new CoreFourierTransformIndicator { TargetPeriod = 10 };
            full.Calculate(candles);
            var truncated = new CoreFourierTransformIndicator { TargetPeriod = 10 };
            truncated.Calculate(candles.Take(40).ToList());

            Assert.Equal(full.Values.Take(40), truncated.Values);
        }
    }
}
