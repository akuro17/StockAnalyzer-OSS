using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreKeltnerChannelIndicatorTests
    {
        // Close and Open are set far apart so a Price-Source-driven center line is
        // trivially distinguishable from a hardcoded-Close center line.
        private static List<CoreCandleData> CreateTestCandles(int count)
        {
            var startDate = DateTime.Today;
            var candles = new List<CoreCandleData>();
            for (int i = 0; i < count; i++)
            {
                decimal close = 10m + i;
                decimal open = 500m + i; // deliberately far from Close
                candles.Add(new CoreCandleData(startDate.AddDays(i), open, close + 10, close - 10, close, 1000));
            }
            return candles;
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreKeltnerChannelIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }

        [Fact]
        public void Calculate_DefaultPriceSource_IsClose()
        {
            var indicator = new CoreKeltnerChannelIndicator();
            Assert.Equal(PriceType.Close, indicator.PriceSource);
        }

        [Fact]
        public void Calculate_ChangingPriceSourceToOpen_ChangesCenterLine()
        {
            var candles = CreateTestCandles(30);

            var closeBased = new CoreKeltnerChannelIndicator { EmaPeriod = 5, AtrPeriod = 5, PriceSource = PriceType.Close };
            closeBased.Calculate(candles);

            var openBased = new CoreKeltnerChannelIndicator { EmaPeriod = 5, AtrPeriod = 5, PriceSource = PriceType.Open };
            openBased.Calculate(candles);

            int lastIndex = candles.Count - 1;
            Assert.NotNull(closeBased.Values[lastIndex]);
            Assert.NotNull(openBased.Values[lastIndex]);
            // Before the fix, PriceSource was ignored and both would be identical (Close-based).
            Assert.NotEqual(closeBased.Values[lastIndex], openBased.Values[lastIndex]);
        }
    }
}
