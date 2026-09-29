using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;

namespace StockAnalyzer.Core.Tests.Models.Indicators.MovingAverages
{
    public class CoreFRAMAIndicatorTests
    {
        [Fact]
        public void Calculate_WithSampleData_ReturnsValidFrama()
        {
            var candles = new List<CoreCandleData>();
            decimal price = 100m;
            for(int i=0; i<50; i++) {
                candles.Add(new CoreCandleData(DateTime.Today.AddDays(i), price, price+(i%5), price-(i%3), price+1, 1000));
                price += 1m;
            }

            var indicator = new CoreFRAMAIndicator();
            var result = indicator.Calculate(candles);

            Assert.True(result.IsSuccessful);
            var mainValues = result.MainValues;
            
            Assert.Equal(50, mainValues.Count);
            
            // First Period-1 values should be null
            for(int i=0; i<15; i++)
            {
                Assert.Null(mainValues[i]);
            }
            
            // Following values should be non-null and NOT equal to the exact close price
            for(int i=16; i<50; i++)
            {
                Assert.NotNull(mainValues[i]);
                Assert.NotEqual(candles[i].Close, mainValues[i].Value);
            }
        }
    }
}
