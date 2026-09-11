using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volume;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreAdlIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(decimal[] highs, decimal[] lows, decimal[] closes, decimal[] volumes)
        {
            var startDate = DateTime.Today;
            return highs.Select((high, i) => new CoreCandleData(
                startDate.AddDays(i),
                closes[i], // Open
                high,
                lows[i],
                closes[i],
                (long)volumes[i]
            )).ToList();
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsCorrectAdl()
        {
            var indicator = new CoreAdlIndicator();
            var candles = CreateTestCandles(
                new decimal[] { 12, 11, 13, 12 },
                new decimal[] { 10, 9, 11, 10 },
                new decimal[] { 11, 10, 12, 11 },
                new decimal[] { 100, 120, 150, 130 }
            );

            indicator.Calculate(candles);

            // MFM1 = ((11-10)-(12-11))/(12-10) = 0/2=0, ADL1=0*100=0
            // MFM2 = ((10-9)-(11-10))/(11-9) = 0/2=0, ADL2=0+0*120=0
            // MFM3 = ((12-11)-(13-12))/(13-11) = 0/2=0, ADL3=0+0*150=0
            // MFM4 = ((11-10)-(12-11))/(12-10) = 0/2=0, ADL4=0+0*130=0
            // Let's use different values
            // H=12,L=10,C=11,V=100 -> MFM=((11-10)-(12-11))/(12-10)=0, ADL=0
            // H=12,L=10,C=12,V=100 -> MFM=((12-10)-(12-12))/(12-10)=1, ADL=0+100=100
            // H=12,L=10,C=10,V=100 -> MFM=((10-10)-(12-10))/(12-10)=-1, ADL=100-100=0
            var candles2 = CreateTestCandles(
                new decimal[] { 12, 12, 12 },
                new decimal[] { 10, 10, 10 },
                new decimal[] { 11, 12, 10 },
                new decimal[] { 100, 100, 100 }
            );
            indicator.Calculate(candles2);

            var expected = new decimal?[] { 0, 100, 0 };

            Assert.Equal(expected.Length, indicator.Values.Count);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i], indicator.Values[i]);
            }
        }

        [Fact]
        public void Calculate_WithZeroRange_ReturnsNoChange()
        {
            var indicator = new CoreAdlIndicator();
            var candles = CreateTestCandles(
                new decimal[] { 10, 10, 10 },
                new decimal[] { 10, 10, 10 },
                new decimal[] { 10, 10, 10 },
                new decimal[] { 100, 120, 150 }
            );

            indicator.Calculate(candles);

            Assert.Equal(3, indicator.Values.Count);
            Assert.All(indicator.Values, v => Assert.Equal(0, v));
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreAdlIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }
    }
}
