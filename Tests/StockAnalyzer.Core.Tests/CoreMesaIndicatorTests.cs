using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Trend;
using StockAnalyzer.Core.Models.Parameters;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreMesaIndicatorTests
    {
        private static List<CoreCandleData> CreateCandles(int count)
        {
            var candles = new List<CoreCandleData>();
            for (int i = 0; i < count; i++)
            {
                decimal mid = 100m + (decimal)(10.0 * System.Math.Sin(2 * System.Math.PI * i / 16.0)) + 0.05m * i;
                candles.Add(new CoreCandleData(new System.DateTime(2023, 1, 1).AddDays(i), mid, mid + 1m, mid - 1m, mid, 1000));
            }
            return candles;
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreMesaIndicator();
            indicator.Calculate(new List<CoreCandleData>());

            Assert.Empty(indicator.Values);
            Assert.Empty(indicator.Fama);
        }

        [Fact]
        public void Calculate_ReturnsValueForEveryBarInBothSeries()
        {
            var candles = CreateCandles(120);
            var indicator = new CoreMesaIndicator();
            var result = indicator.Calculate(candles);

            Assert.True(result.IsSuccessful, result.ErrorMessage);
            Assert.Equal(candles.Count, indicator.Values.Count);
            Assert.Equal(candles.Count, indicator.Fama.Count);
            Assert.True(indicator.Values.All(v => v.HasValue));
            Assert.True(indicator.Fama.All(v => v.HasValue));
            Assert.Equal(candles.Count, result.GetSeries(IndicatorResult.MainSeriesName).Count);
            Assert.Equal(candles.Count, result.GetSeries("Fama").Count);
        }

        [Fact]
        public void Calculate_IsCausal_ValuesDoNotChangeWhenLaterBarsAreRemoved()
        {
            var candles = CreateCandles(120);
            var full = new CoreMesaIndicator();
            full.Calculate(candles);
            var truncated = new CoreMesaIndicator();
            truncated.Calculate(candles.Take(80).ToList());

            for (int i = 0; i < 80; i++)
            {
                Assert.Equal(full.Values[i], truncated.Values[i]);
                Assert.Equal(full.Fama[i], truncated.Fama[i]);
            }
        }

        [Theory]
        [InlineData(0.05, 0.5)]   // SlowLimit above FastLimit
        [InlineData(1.5, 0.05)]   // FastLimit above 1
        [InlineData(0.5, 0.0)]    // SlowLimit not positive
        public void Calculate_InvalidLimits_ReturnsFailure(double fast, double slow)
        {
            var indicator = new CoreMesaIndicator { FastLimit = (decimal)fast, SlowLimit = (decimal)slow };
            var result = indicator.Calculate(CreateCandles(100));

            Assert.False(result.IsSuccessful);
        }

        [Fact]
        public void Calculate_EqualLimits_IsValidAndPinsAlpha()
        {
            var indicator = new CoreMesaIndicator { FastLimit = 0.3m, SlowLimit = 0.3m };
            var result = indicator.Calculate(CreateCandles(100));

            Assert.True(result.IsSuccessful, result.ErrorMessage);
        }

        [Fact]
        public void ParameterValidate_EnforcesOrderingOfLimits()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new CoreMesaParameter { FastLimit = 0.1m, SlowLimit = 0.2m }.Validate());
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new CoreMesaParameter { FastLimit = 1.1m, SlowLimit = 0.05m }.Validate());
            new CoreMesaParameter().Validate();
        }
    }
}
