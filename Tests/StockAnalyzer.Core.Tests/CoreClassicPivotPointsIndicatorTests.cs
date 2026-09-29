using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Chart;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreClassicPivotPointsIndicatorTests
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
            var indicator = new CoreClassicPivotPointsIndicator();
            var result = indicator.Calculate(new List<CoreCandleData>());
            Assert.True(result.IsSuccessful);
            Assert.Empty(indicator.Values);
            Assert.Empty(indicator.R1);
            Assert.Empty(indicator.R2);
            Assert.Empty(indicator.R3);
            Assert.Empty(indicator.S1);
            Assert.Empty(indicator.S2);
            Assert.Empty(indicator.S3);
        }

        [Fact]
        public void Calculate_WithSingleCandle_ReturnsAllNulls()
        {
            var indicator = new CoreClassicPivotPointsIndicator();
            var candles = new List<CoreCandleData>
            {
                new(DateTime.Today, 100, 110, 90, 105, 1000)
            };
            var result = indicator.Calculate(candles);
            Assert.True(result.IsSuccessful);
            Assert.Single(indicator.Values);
            Assert.Null(indicator.Values[0]);
            Assert.Null(indicator.R1[0]);
            Assert.Null(indicator.R2[0]);
            Assert.Null(indicator.R3[0]);
            Assert.Null(indicator.S1[0]);
            Assert.Null(indicator.S2[0]);
            Assert.Null(indicator.S3[0]);
        }

        [Fact]
        public void Calculate_WithKnownData_ComputesAllSevenLinesAccurately()
        {
            var indicator = new CoreClassicPivotPointsIndicator();
            var today = DateTime.Today;
            var candles = new List<CoreCandleData>
            {
                // Day 0: High = 110, Low = 90, Close = 100
                new(today, 95, 110, 90, 100, 1000),
                // Day 1: calculation should use Day 0 data
                new(today.AddDays(1), 101, 115, 95, 105, 1200),
                // Day 2: High = 115, Low = 95, Close = 105
                new(today.AddDays(2), 106, 120, 100, 110, 1100)
            };

            var result = indicator.Calculate(candles);
            Assert.True(result.IsSuccessful);
            Assert.Equal(3, indicator.Values.Count);

            // Index 0: Null across all series
            Assert.Null(indicator.Values[0]);
            Assert.Null(indicator.R1[0]);
            Assert.Null(indicator.R2[0]);
            Assert.Null(indicator.R3[0]);
            Assert.Null(indicator.S1[0]);
            Assert.Null(indicator.S2[0]);
            Assert.Null(indicator.S3[0]);

            // Index 1: Computed from Day 0 (H=110, L=90, C=100)
            // P = (110 + 90 + 100) / 3 = 100
            // R1 = 2*100 - 90 = 110
            // S1 = 2*100 - 110 = 90
            // diff = 110 - 90 = 20
            // R2 = 100 + 20 = 120
            // S2 = 100 - 20 = 80
            // R3 = 110 + 20 = 130
            // S3 = 90 - 20 = 70
            Assert.Equal(100m, indicator.Values[1]);
            Assert.Equal(100m, indicator.Pivot[1]);
            Assert.Equal(110m, indicator.R1[1]);
            Assert.Equal(120m, indicator.R2[1]);
            Assert.Equal(130m, indicator.R3[1]);
            Assert.Equal(90m, indicator.S1[1]);
            Assert.Equal(80m, indicator.S2[1]);
            Assert.Equal(70m, indicator.S3[1]);

            // Index 2: Computed from Day 1 (H=115, L=95, C=105)
            // P = (115 + 95 + 105) / 3 = 315 / 3 = 105
            // R1 = 2*105 - 95 = 210 - 95 = 115
            // S1 = 2*105 - 115 = 210 - 115 = 95
            // diff = 115 - 95 = 20
            // R2 = 105 + 20 = 125
            // S2 = 105 - 20 = 85
            // R3 = 115 + 20 = 135
            // S3 = 95 - 20 = 75
            Assert.Equal(105m, indicator.Values[2]);
            Assert.Equal(115m, indicator.R1[2]);
            Assert.Equal(125m, indicator.R2[2]);
            Assert.Equal(135m, indicator.R3[2]);
            Assert.Equal(95m, indicator.S1[2]);
            Assert.Equal(85m, indicator.S2[2]);
            Assert.Equal(75m, indicator.S3[2]);
        }

        [Fact]
        public void Calculate_ReturnsMultiSeriesResult_WithExpectedSeriesNames()
        {
            var indicator = new CoreClassicPivotPointsIndicator();
            var candles = CreateTestCandles(new decimal[] { 10, 11, 12, 13 });
            var result = indicator.Calculate(candles);

            Assert.True(result.IsSuccessful);
            var names = result.SeriesNames.ToList();
            Assert.Contains("Main", names);
            Assert.Contains("R1", names);
            Assert.Contains("R2", names);
            Assert.Contains("R3", names);
            Assert.Contains("S1", names);
            Assert.Contains("S2", names);
            Assert.Contains("S3", names);

            Assert.Equal(candles.Count, result.GetSeries("Main").Count);
            Assert.Equal(candles.Count, result.GetSeries("R1").Count);
            Assert.Equal(candles.Count, result.GetSeries("R2").Count);
            Assert.Equal(candles.Count, result.GetSeries("R3").Count);
            Assert.Equal(candles.Count, result.GetSeries("S1").Count);
            Assert.Equal(candles.Count, result.GetSeries("S2").Count);
            Assert.Equal(candles.Count, result.GetSeries("S3").Count);
        }
    }
}
