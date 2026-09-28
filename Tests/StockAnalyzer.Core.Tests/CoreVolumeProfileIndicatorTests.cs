using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Chart;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreVolumeProfileIndicatorTests
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
            var indicator = new CoreVolumeProfileIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }

        [Fact]
        public void Calculate_WithSampleData_ReturnsValues()
        {
            var indicator = new CoreVolumeProfileIndicator();
            var candles = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14, 15, 16 });
            indicator.Calculate(candles);
            Assert.Equal(candles.Count, indicator.Values.Count);
        }

        [Fact]
        public void VolumeProfile_IsOverlay_IsTrue_And_CategoryIsChart()
        {
            var indicator = new CoreVolumeProfileIndicator();
            Assert.True(indicator.IsOverlay);

            var defaultSettings = indicator.GetDefaultSettings();
            Assert.True(defaultSettings.IsOverlay);
            Assert.Equal(CoreIndicatorCategory.Chart, defaultSettings.Category);
            Assert.Equal(IndicatorType.VolumeProfile, defaultSettings.TypeEnum);
        }
    }
}
