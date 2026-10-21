using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreDonchianChannelIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(IEnumerable<decimal> closePrices)
        {
            var startDate = DateTime.Today;
            return closePrices.Select((price, i) => new CoreCandleData(
                startDate.AddDays(i), price, price + 10, price - 10, price, 1000
            )).ToList();
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreDonchianChannelIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
            Assert.Empty(indicator.Position);
        }

        [Fact]
        public void Calculate_UsesActualHighLow_RegardlessOfPriceSource()
        {
            // Close: 10,11,12,13,14 => High (Close+10): 20..24, Low (Close-10): 0..4
            // Donchian is defined against the real High/Low, not a user-selectable Price Source
            // (verified unchanged after the RollingExtremeHelper DRY refactor).
            var indicator = new CoreDonchianChannelIndicator { Period = 3 };
            var candles = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14 });

            indicator.Calculate(candles);

            Assert.Null(indicator.UpperBand[1]);
            Assert.Equal(22m, indicator.UpperBand[2]);
            Assert.Equal(23m, indicator.UpperBand[3]);
            Assert.Equal(24m, indicator.UpperBand[4]);

            Assert.Null(indicator.LowerBand[1]);
            Assert.Equal(0m, indicator.LowerBand[2]);
            Assert.Equal(1m, indicator.LowerBand[3]);
            Assert.Equal(2m, indicator.LowerBand[4]);

            // Main series (Values) is the midpoint of Upper/Lower.
            Assert.Equal(11m, indicator.Values[2]);
            Assert.Equal(12m, indicator.Values[3]);
            Assert.Equal(13m, indicator.Values[4]);
        }

        [Fact]
        public void Calculate_Position_ComputesCorrectly()
        {
            var indicator = new CoreDonchianChannelIndicator { Period = 3 };
            var candles = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14 });

            indicator.Calculate(candles);

            Assert.Null(indicator.Position[0]);
            Assert.Null(indicator.Position[1]);

            // Index 2: Upper=22, Lower=0, Close=12 -> Position = (12 - 0) / 22 = 6/11m
            decimal expected = (12m - 0m) / (22m - 0m);
            Assert.Equal(expected, indicator.Position[2]);

            // Index 3: Upper=23, Lower=1, Close=13 -> Position = (13 - 1) / (23 - 1) = 12/22 = 6/11m
            Assert.Equal(expected, indicator.Position[3]);

            // Index 4: Upper=24, Lower=2, Close=14 -> Position = (14 - 2) / (24 - 2) = 12/22 = 6/11m
            Assert.Equal(expected, indicator.Position[4]);
        }

        [Fact]
        public void Calculate_Position_FlatWindow_ReturnsHalf()
        {
            var indicator = new CoreDonchianChannelIndicator { Period = 3 };
            var startDate = DateTime.Today;
            var flatCandles = Enumerable.Range(0, 5).Select(i => new CoreCandleData(
                startDate.AddDays(i), 100m, 100m, 100m, 100m, 1000
            )).ToList();

            indicator.Calculate(flatCandles);

            Assert.Null(indicator.Position[0]);
            Assert.Null(indicator.Position[1]);
            // Delta == 0 -> Zero division avoided, defaults to 0.5m
            Assert.Equal(0.5m, indicator.Position[2]);
            Assert.Equal(0.5m, indicator.Position[3]);
            Assert.Equal(0.5m, indicator.Position[4]);
        }

        [Fact]
        public void Calculate_SeriesOrder_PreservesExpectedSequence()
        {
            var indicator = new CoreDonchianChannelIndicator { Period = 3 };
            var candles = CreateTestCandles(new decimal[] { 10, 11, 12 });

            var result = indicator.Calculate(candles);

            var expectedOrder = new[] { "UpperBand", "Main", "LowerBand", "Position" };
            Assert.Equal(expectedOrder, result.SeriesNamesList);
        }

        [Fact]
        public void IndicatorChartSuppression_DonchianPosition_IsSuppressedForChart()
        {
            Assert.True(IndicatorChartSuppression.IsSuppressed(IndicatorType.Donchian, "Position"));
            Assert.False(IndicatorChartSuppression.IsDataWindowSuppressed(IndicatorType.Donchian, "Position"));
        }
    }
}
