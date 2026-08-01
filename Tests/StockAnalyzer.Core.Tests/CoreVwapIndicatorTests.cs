using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volume;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreVwapIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(decimal[] highs, decimal[] lows, decimal[] closes, decimal[] volumes)
        {
            var startDate = DateTime.Today;
            return closes.Select((price, i) => new CoreCandleData(
                startDate.AddDays(i),
                closes[i], // Open
                highs[i],
                lows[i],
                closes[i],
                (long)volumes[i]
            )).ToList();
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsCorrectVwap()
        {
            var indicator = new CoreVwapIndicator();
            var candles = CreateTestCandles(
                new decimal[] { 10, 11, 12 },
                new decimal[] { 8, 9, 10 },
                new decimal[] { 9, 10, 11 },
                new decimal[] { 100, 150, 200 }
            );

            indicator.Calculate(candles);

            // TP1 = (10+8+9)/3 = 9, TPV1 = 9*100 = 900, CV1 = 100, VWAP1 = 900/100 = 9
            // TP2 = (11+9+10)/3 = 10, TPV2 = 10*150 = 1500, CTPV2 = 900+1500=2400, CV2 = 100+150=250, VWAP2 = 2400/250 = 9.6
            // TP3 = (12+10+11)/3 = 11, TPV3 = 11*200 = 2200, CTPV3 = 2400+2200=4600, CV3 = 250+200=450, VWAP3 = 4600/450 = 10.222...
            var expected = new decimal?[] { 9m, 9.6m, 10.222222222222222222222222222m };

            Assert.Equal(expected.Length, indicator.Values.Count);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal((double)expected[i]!, (double)indicator.Values[i]!, precision: 8);
            }
        }

        [Fact]
        public void Calculate_WithZeroVolume_ReturnsNull()
        {
            var indicator = new CoreVwapIndicator();
            var candles = CreateTestCandles(
                new decimal[] { 10 }, new decimal[] { 8 }, new decimal[] { 9 }, new decimal[] { 0 }
            );

            indicator.Calculate(candles);

            Assert.Single(indicator.Values);
            Assert.Null(indicator.Values[0]);
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreVwapIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }
    }
}
