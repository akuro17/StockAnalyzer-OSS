using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreVwmaIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(decimal[] closes, decimal[] volumes)
        {
            var startDate = DateTime.Today;
            return closes.Select((price, i) => new CoreCandleData(
                startDate.AddDays(i), price, price, price, price, (long)volumes[i]
            )).ToList();
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsCorrectVwma()
        {
            var indicator = new CoreVwmaIndicator { Period = 3 };
            var candles = CreateTestCandles(
                new decimal[] { 10, 12, 14, 13, 15 },
                new decimal[] { 100, 110, 120, 130, 140 }
            );

            indicator.Calculate(candles);

            // i=2: (10*100 + 12*110 + 14*120) / (100+110+120) = (1000+1320+1680)/330 = 4000/330 = 12.1212
            // i=3: (12*110 + 14*120 + 13*130) / (110+120+130) = (1320+1680+1690)/360 = 4690/360 = 13.0277
            // i=4: (14*120 + 13*130 + 15*140) / (120+130+140) = (1680+1690+2100)/390 = 5470/390 = 14.0256
            var expected = new decimal?[] { null, null, 12.121212121212121212121212121m, 13.027777777777777777777777778m, 14.025641025641025641025641026m };

            Assert.Equal(expected.Length, indicator.Values.Count);
            Assert.Null(indicator.Values[0]);
            Assert.Null(indicator.Values[1]);
            Assert.Equal((double)expected[2]!, (double)indicator.Values[2]!, precision: 8);
            Assert.Equal((double)expected[3]!, (double)indicator.Values[3]!, precision: 8);
            Assert.Equal((double)expected[4]!, (double)indicator.Values[4]!, precision: 8);
        }

        [Fact]
        public void Calculate_WithZeroVolume_ReturnsNull()
        {
            var indicator = new CoreVwmaIndicator { Period = 3 };
            var candles = CreateTestCandles(
                new decimal[] { 10, 12, 14 },
                new decimal[] { 0, 0, 0 }
            );

            indicator.Calculate(candles);

            Assert.Equal(3, indicator.Values.Count);
            Assert.Null(indicator.Values[0]);
            Assert.Null(indicator.Values[1]);
            Assert.Null(indicator.Values[2]);
        }

        [Fact]
        public void Calculate_WithInsufficientData_ReturnsNulls()
        {
            var indicator = new CoreVwmaIndicator { Period = 5 };
            var candles = CreateTestCandles(new decimal[] { 10, 12, 14 }, new decimal[] { 100, 100, 100 });

            indicator.Calculate(candles);

            Assert.Equal(3, indicator.Values.Count);
            Assert.All(indicator.Values, v => Assert.Null(v));
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreVwmaIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }
    }
}
