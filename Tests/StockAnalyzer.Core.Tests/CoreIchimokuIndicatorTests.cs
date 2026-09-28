using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Trend;
using StockAnalyzer.Core.Models.Parameters;
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
            var indicator = new CoreIchimokuIndicator { TenkanPeriod = 3, KijunPeriod = 5, SenkouPeriod = 7, Displacement = 5 };
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
            // But it is shifted by Displacement (5 here). So plotted at 4+5 = 9.
            // Since data has 9 items (0-8), index 9 is valid in the Extended list (size 14).
            Assert.Null(indicator.SenkouSpanA[8]);
            Assert.NotNull(indicator.SenkouSpanA[9]);
            
            // Verify SenkouSpanB starts at correct index (i=6)
            // Shifted by 5 -> 6+5 = 11.
            Assert.Null(indicator.SenkouSpanB[10]);
            Assert.NotNull(indicator.SenkouSpanB[11]);
        }

        private static List<CoreCandleData> DistinctCloseCandles(int count)
            => Enumerable.Range(0, count)
                .Select(i => new CoreCandleData(DateTime.Today.AddDays(i), 100m + i, 101m + i, 99m + i, 100m + i, 1000))
                .ToList();

        [Fact]
        public void ChikouSpan_DefaultDisplacement_PlotsTheCloseOfBarTPlus26AtBarT()
        {
            // Official definition (docs/indicators_formulas.md): the close is plotted 26 bars back, so bar t carries Close[t + 26].
            var indicator = new CoreIchimokuIndicator();
            var candles = DistinctCloseCandles(40);

            indicator.Calculate(candles);

            Assert.Equal(candles.Count, indicator.ChikouSpan.Count);
            for (int i = 0; i + 26 < candles.Count; i++) Assert.Equal(candles[i + 26].Close, indicator.ChikouSpan[i]);
            for (int i = candles.Count - 26; i < candles.Count; i++) Assert.Null(indicator.ChikouSpan[i]);
        }

        [Fact]
        public void ChikouSpan_ConfiguredDisplacement_IsApplied_IndependentOfKijunPeriod()
        {
            var indicator = new CoreIchimokuIndicator();
            indicator.Configure(new CoreIchimokuParameter { Offset = 10 });
            var candles = DistinctCloseCandles(40);

            indicator.Calculate(candles);

            Assert.Equal(10, indicator.Displacement);
            for (int i = 0; i + 10 < candles.Count; i++) Assert.Equal(candles[i + 10].Close, indicator.ChikouSpan[i]);
            Assert.Null(indicator.ChikouSpan[candles.Count - 10]);
        }

        [Fact]
        public void NegativeDisplacement_AssignedDirectlyToTheIndicator_IsRejected_BecauseTheCloudWouldBePlottedIntoThePast()
        {
            var indicator = new CoreIchimokuIndicator { Displacement = -3 };

            var result = indicator.Calculate(DistinctCloseCandles(60));

            Assert.False(result.IsSuccessful);
            Assert.Contains("Displacement", result.ErrorMessage);
        }

        [Fact]
        public void NegativeDisplacement_ThroughTheParameter_IsClampedToTheMinimum_SoTheIndicatorNeverSeesIt()
        {
            var indicator = new CoreIchimokuIndicator();
            indicator.Configure(new CoreIchimokuParameter { Offset = -3 });

            var result = indicator.Calculate(DistinctCloseCandles(60));

            Assert.True(result.IsSuccessful);
            Assert.Equal(CoreIchimokuParameter.MinDisplacement, indicator.Displacement);
        }

        [Fact]
        public void SenkouSpans_DefaultDisplacement_ArePlotted26BarsAhead_ExtendingTheListBy26()
        {
            // First Senkou A input exists at bar 25 (Kijun 26 needs 26 bars) -> plotted at 25 + 26 = 51. Senkou B (52) first exists at bar 51 -> 77.
            var indicator = new CoreIchimokuIndicator();
            var candles = DistinctCloseCandles(100);

            indicator.Calculate(candles);

            Assert.Equal(candles.Count + 26, indicator.SenkouSpanA.Count);
            Assert.Null(indicator.SenkouSpanA[50]);
            Assert.NotNull(indicator.SenkouSpanA[51]);
            Assert.Equal((indicator.TenkanSen[25]!.Value + indicator.KijunSen[25]!.Value) / 2m, indicator.SenkouSpanA[51]);
            Assert.Null(indicator.SenkouSpanB[76]);
            Assert.NotNull(indicator.SenkouSpanB[77]);
        }

        [Fact]
        public void SenkouSpans_ConfiguredDisplacement_IsIndependentOfKijunPeriod()
        {
            var indicator = new CoreIchimokuIndicator();
            indicator.Configure(new CoreIchimokuParameter { Offset = 10 });   // Kijun stays 26
            var candles = DistinctCloseCandles(100);

            indicator.Calculate(candles);

            Assert.Equal(candles.Count + 10, indicator.SenkouSpanA.Count);
            Assert.Null(indicator.SenkouSpanA[34]);
            Assert.NotNull(indicator.SenkouSpanA[35]);                         // 25 + 10
            Assert.Equal((indicator.TenkanSen[25]!.Value + indicator.KijunSen[25]!.Value) / 2m, indicator.SenkouSpanA[35]);
        }

        [Fact]
        public void Displacement_DefaultMatchesTheParameterDefault()
        {
            Assert.Equal(new CoreIchimokuParameter().Offset, new CoreIchimokuIndicator().Displacement);
            Assert.Equal(CoreIchimokuParameter.DefaultDisplacement, new CoreIchimokuIndicator().Displacement);
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
