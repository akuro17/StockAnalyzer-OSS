using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreMtfLaggedEmaIndicatorTests
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
            var indicator = new CoreMtfLaggedEmaIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }

        [Fact]
        public void Calculate_WithSampleData_ReturnsValues()
        {
            var indicator = new CoreMtfLaggedEmaIndicator();
            var candles = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14, 15, 16 });
            indicator.Calculate(candles);
            Assert.Equal(candles.Count, indicator.Values.Count);
        }

        [Fact]
        public void Parameter_Validate_WithLagLessThanOne_ThrowsArgumentOutOfRangeException()
        {
            var param = new StockAnalyzer.Core.Models.Parameters.CoreMtfLaggedEmaParameter { Lag = 0 };
            Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        }

        [Fact]
        public void Indicator_LagSetterAndConfigure_EnforcesMinimumOfOne()
        {
            var indicator = new CoreMtfLaggedEmaIndicator { Lag = 0 };
            Assert.Equal(1, indicator.Lag);

            indicator.Configure(new StockAnalyzer.Core.Models.Parameters.CoreMtfLaggedEmaParameter { Lag = 0 });
            Assert.Equal(1, indicator.Lag);
        }

        [Fact]
        public void Indicator_TimeFrameMultiplierSetterAndConfigure_EnforcesMinimumOfOne()
        {
            var indicator = new CoreMtfLaggedEmaIndicator { TimeFrameMultiplier = 0 };
            Assert.Equal(1, indicator.TimeFrameMultiplier);

            indicator.TimeFrameMultiplier = -2;
            Assert.Equal(1, indicator.TimeFrameMultiplier);

            indicator.Configure(new StockAnalyzer.Core.Models.Parameters.CoreMtfLaggedEmaParameter { TimeFrameMultiplier = 0 });
            Assert.Equal(1, indicator.TimeFrameMultiplier);
        }
    }
}
