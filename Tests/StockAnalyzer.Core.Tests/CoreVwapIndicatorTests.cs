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

        [Fact]
        public void DefaultPriceSource_IsTypical()
        {
            var indicator = new CoreVwapIndicator();
            Assert.Equal(PriceType.Typical, indicator.PriceSource);
        }

        [Fact]
        public void GetDefaultSettings_PriceSource_IsTypical()
        {
            var indicator = new CoreVwapIndicator();
            var settings = indicator.GetDefaultSettings();
            Assert.Equal(PriceType.Typical, settings.PriceSource);
        }

        [Fact]
        public void Calculate_WithClosePriceSource_DiffersFromTypical()
        {
            // Candles where High != Close, so Typical != Close
            var candles = CreateTestCandles(
                new decimal[] { 20, 22 },
                new decimal[] { 8,  9  },
                new decimal[] { 9,  10 },
                new decimal[] { 100, 100 }
            );

            var typicalIndicator = new CoreVwapIndicator { PriceSource = PriceType.Typical };
            typicalIndicator.Calculate(candles);

            var closeIndicator = new CoreVwapIndicator { PriceSource = PriceType.Close };
            closeIndicator.Calculate(candles);

            // Typical prices: (20+8+9)/3=12.333..., (22+9+10)/3=13.666...
            // Close prices: 9, 10
            // Results should differ because High != Close
            Assert.NotEqual(
                (double)typicalIndicator.Values.Last()!.Value,
                (double)closeIndicator.Values.Last()!.Value,
                precision: 4);
        }

        [Fact]
        public void Calculate_WithOpenPriceSource_UsesOpenPrice()
        {
            // Open == Close in CreateTestCandles, so use explicit construction to test Open separately
            var startDate = DateTime.Today;
            var candles = new List<CoreCandleData>
            {
                new CoreCandleData(startDate,          100m, 120m, 80m, 110m, 1000),
                new CoreCandleData(startDate.AddDays(1), 110m, 130m, 90m, 120m, 1000)
            };

            var openIndicator = new CoreVwapIndicator { PriceSource = PriceType.Open };
            openIndicator.Calculate(candles);

            // VWAP(Open) bar1: 100*1000/1000 = 100
            // VWAP(Open) bar2: (100*1000 + 110*1000) / 2000 = 105
            Assert.Equal(100m, openIndicator.Values[0]);
            Assert.Equal(105m, openIndicator.Values[1]);
        }
    }
}
