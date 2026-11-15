using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volume;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreAnchoredVWAPIndicatorTests
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
            var indicator = new CoreAnchoredVWAPIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }

        [Fact]
        public void Calculate_WithSampleData_ReturnsValues()
        {
            var indicator = new CoreAnchoredVWAPIndicator();
            var candles = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14, 15, 16 });
            indicator.Calculate(candles);
            Assert.Equal(candles.Count, indicator.Values.Count);
        }

        // WebAiExpansion plan 12 T2: a bar with a negative volume (invalid data that the stored-data loaders do not reject) counts as volume 0.
        private static List<CoreCandleData> NegativeVolumeCandles(long volumeOfTheMiddleBar)
        {
            var startDate = new DateTime(2024, 1, 2);
            return new List<CoreCandleData>
            {
                new CoreCandleData(startDate, 10m, 12m, 8m, 10m, 100),
                new CoreCandleData(startDate.AddDays(1), 20m, 22m, 18m, 20m, volumeOfTheMiddleBar),
                new CoreCandleData(startDate.AddDays(2), 30m, 32m, 28m, 30m, 300),
            };
        }

        [Fact]
        public void Calculate_ABarWithNegativeVolume_BehavesLikeABarWithVolumeZero_MainAndBands()
        {
            var negative = new CoreAnchoredVWAPIndicator { ShowBands = true };
            var zero = new CoreAnchoredVWAPIndicator { ShowBands = true };

            var negativeResult = negative.Calculate(NegativeVolumeCandles(-500));
            var zeroResult = zero.Calculate(NegativeVolumeCandles(0));

            Assert.Equal(zeroResult.GetSeries("Main"), negativeResult.GetSeries("Main"));
            Assert.Equal(zeroResult.GetSeries("UpperBand"), negativeResult.GetSeries("UpperBand"));
            Assert.Equal(zeroResult.GetSeries("LowerBand"), negativeResult.GetSeries("LowerBand"));
            // Typical prices 10, 20, 30 from the first bar: the middle bar adds nothing, so bar 3 is (10*100 + 30*300) / 400 = 25.
            Assert.Equal(10m, negativeResult.GetSeries("Main")[1]);
            Assert.Equal(25m, negativeResult.GetSeries("Main")[2]);
        }

        [Fact]
        public void Calculate_ANegativeVolumeOnTheAnchorBar_KeepsTheVwapNullWhileTheCumulativeVolumeIsZero()
        {
            var candles = new List<CoreCandleData>
            {
                new CoreCandleData(new DateTime(2024, 1, 2), 10m, 12m, 8m, 10m, -50),
                new CoreCandleData(new DateTime(2024, 1, 3), 20m, 22m, 18m, 20m, 100),
            };

            var result = new CoreAnchoredVWAPIndicator { AnchorIndex = 0 }.Calculate(candles);

            Assert.Null(result.GetSeries("Main")[0]);
            Assert.Equal(20m, result.GetSeries("Main")[1]);
        }
    }
}
