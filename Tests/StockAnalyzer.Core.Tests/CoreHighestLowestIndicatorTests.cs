using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Trend;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreHighestLowestIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(IEnumerable<decimal> closePrices)
        {
            var startDate = DateTime.Today;
            return closePrices.Select((price, i) => new CoreCandleData(
                startDate.AddDays(i), price, price + 10, price - 10, price, 1000
            )).ToList();
        }

        [Fact]
        public void Highest_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreHighestIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }

        [Fact]
        public void Lowest_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreLowestIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }

        [Fact]
        public void Highest_DefaultPriceSource_IsHigh()
        {
            var indicator = new CoreHighestIndicator();
            Assert.Equal(PriceType.High, indicator.PriceSource);
        }

        [Fact]
        public void Lowest_DefaultPriceSource_IsLow()
        {
            var indicator = new CoreLowestIndicator();
            Assert.Equal(PriceType.Low, indicator.PriceSource);
        }

        [Fact]
        public void Highest_ComputesRollingMaxOfHighField_ByDefault()
        {
            // Close: 10,11,12,13,14 => High (Close+10): 20,21,22,23,24
            var indicator = new CoreHighestIndicator { Period = 3 };
            var candles = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14 });

            indicator.Calculate(candles);

            Assert.Null(indicator.Values[0]);
            Assert.Null(indicator.Values[1]);
            Assert.Equal(22m, indicator.Values[2]);
            Assert.Equal(23m, indicator.Values[3]);
            Assert.Equal(24m, indicator.Values[4]);
        }

        [Fact]
        public void Lowest_ComputesRollingMinOfLowField_ByDefault()
        {
            // Close: 10,11,12,13,14 => Low (Close-10): 0,1,2,3,4
            var indicator = new CoreLowestIndicator { Period = 3 };
            var candles = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14 });

            indicator.Calculate(candles);

            Assert.Null(indicator.Values[0]);
            Assert.Null(indicator.Values[1]);
            Assert.Equal(0m, indicator.Values[2]);
            Assert.Equal(1m, indicator.Values[3]);
            Assert.Equal(2m, indicator.Values[4]);
        }

        [Fact]
        public void Highest_PriceSourceCanBeChangedToClose()
        {
            var indicator = new CoreHighestIndicator { Period = 3, PriceSource = PriceType.Close };
            var candles = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14 });

            indicator.Calculate(candles);

            // Rolling max of Close itself (not High), confirming Price Source is now effective.
            Assert.Equal(12m, indicator.Values[2]);
            Assert.Equal(13m, indicator.Values[3]);
            Assert.Equal(14m, indicator.Values[4]);
        }

        [Fact]
        public void Highest_ProducesMainSeries_ForIndicatorOnIndicatorChaining()
        {
            var indicator = new CoreHighestIndicator { Period = 3 };
            var candles = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14 });

            var result = indicator.Calculate(candles);

            // Unlike the old HighLowBand (which returned only {High, Mid, Low} with no "Main" key
            // and silently broke Indicator-on-Indicator chaining), Highest/Lowest must expose MainValues.
            Assert.True(result.IsSuccessful);
            Assert.Equal(candles.Count, result.MainValues.Count);
        }

        [Fact]
        public void Lowest_ProducesMainSeries_ForIndicatorOnIndicatorChaining()
        {
            var indicator = new CoreLowestIndicator { Period = 3 };
            var candles = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14 });

            var result = indicator.Calculate(candles);

            Assert.True(result.IsSuccessful);
            Assert.Equal(candles.Count, result.MainValues.Count);
        }
    }
}
