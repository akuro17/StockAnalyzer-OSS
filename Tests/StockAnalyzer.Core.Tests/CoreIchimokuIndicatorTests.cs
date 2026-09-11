using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Trend;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreIchimokuIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(decimal[] highs, decimal[] lows)
        {
            var startDate = DateTime.Today;
            return highs.Select((high, i) => new CoreCandleData(
                startDate.AddDays(i),
                (high+lows[i])/2, (high+lows[i])/2, high, lows[i], 1000
            )).ToList();
        }

        [Fact]
        public void Calculate_WithSufficientData_CalculatesAllComponents()
        {
            // Using shorter periods for testability
            var indicator = new CoreIchimokuIndicator { TenkanPeriod = 3, KijunPeriod = 5, SenkouPeriod = 7 };
            var highs = new decimal[] { 10, 11, 12, 11, 12, 13, 14, 13, 12 };
            var lows =  new decimal[] {  8,  9, 10,  9, 10, 11, 12, 11, 10 };
            var candles = CreateTestCandles(highs, lows);

            var result = indicator.Calculate(candles);

            // Assert success first to get error message if it fails
            Assert.True(result.IsSuccessful, result.ErrorMessage);

            Assert.Equal(candles.Count, indicator.Values.Count);
            Assert.Equal(candles.Count, indicator.TenkanSen.Count);
            Assert.Equal(candles.Count, indicator.KijunSen.Count);
            
            // Ichimoku projects into the future (Cloud), so lists might be longer than input.
            Assert.True(indicator.SenkouSpanA.Count >= candles.Count, "SenkouSpanA count should cover at least input candles");
            Assert.True(indicator.SenkouSpanB.Count >= candles.Count, "SenkouSpanB count should cover at least input candles");
            
            Assert.Equal(candles.Count, indicator.ChikouSpan.Count); 

            // --- Smoke test verification ---
            // Verify Tenkan starts at correct index
            Assert.Null(indicator.TenkanSen[1]);
            Assert.NotNull(indicator.TenkanSen[2]);
            
            // Verify Kijun starts at correct index  
            Assert.Null(indicator.KijunSen[3]);
            Assert.NotNull(indicator.KijunSen[4]);
            
            // Verify SenkouSpanA has value after both Tenkan and Kijun are available (i=4)
            // But it is shifted by KijunPeriod (5). So plotted at 4+5 = 9.
            // Since data has 9 items (0-8), index 9 is valid in the Extended list (size 14).
            Assert.Null(indicator.SenkouSpanA[8]);
            Assert.NotNull(indicator.SenkouSpanA[9]);
            
            // Verify SenkouSpanB starts at correct index (i=6)
            // Shifted by 5 -> 6+5 = 11.
            Assert.Null(indicator.SenkouSpanB[10]);
            Assert.NotNull(indicator.SenkouSpanB[11]);
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmptyLists()
        {
            var indicator = new CoreIchimokuIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
            Assert.Empty(indicator.TenkanSen);
            Assert.Empty(indicator.KijunSen);
            Assert.Empty(indicator.SenkouSpanA);
            Assert.Empty(indicator.SenkouSpanB);
            Assert.Empty(indicator.ChikouSpan);
        }
    }
}
