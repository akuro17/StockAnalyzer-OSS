using System;
using System.Collections.Generic;
using Xunit;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Tests.Models
{
    public class ComparisonPriceRangeCalculatorTests
    {
        private ComparisonAlignedData CreateTestData(string primarySymbol, string otherSymbol, decimal[] primaryCloses, decimal[] otherCloses)
        {
            var timestamps = new DateTime[primaryCloses.Length];
            for (int i = 0; i < timestamps.Length; i++) 
                timestamps[i] = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i);

            var series = new Dictionary<string, CandleData?[]>();
            
            var primaryCandles = new CandleData?[primaryCloses.Length];
            for (int i = 0; i < primaryCloses.Length; i++) 
                primaryCandles[i] = new CandleData(timestamps[i], 0, 0, 0, primaryCloses[i], 0);
            series[primarySymbol] = primaryCandles;

            var otherCandles = new CandleData?[otherCloses.Length];
            for (int i = 0; i < otherCloses.Length; i++) 
                otherCandles[i] = new CandleData(timestamps[i], 0, 0, 0, otherCloses[i], 0);
            series[otherSymbol] = otherCandles;

            return new ComparisonAlignedData(primarySymbol, timestamps, series, new List<string>());
        }

        [Fact]
        public void GetPriceRange_SpreadMode_ShouldExcludePrimarySymbolFromScaling()
        {
            // Arrange
            var primaryCloses = new decimal[] { 100m, 105m, 110m };
            var otherCloses = new decimal[] { 90m, 92m, 95m };
            var data = CreateTestData("BASE", "OTHER", primaryCloses, otherCloses);
            
            // Verify data state
            Assert.Equal(0, Array.BinarySearch(data.Timestamps, data.Timestamps[0]));
            
            var calculator = new ComparisonPriceRangeCalculator(data, 0, ComparisonMode.Spread);
            
            // Act
            var candle0 = new CoreCandleData(data.Timestamps[0], 0, 0, 0, 0, 0);
            var range0 = calculator.GetPriceRange(candle0);
            
            // Assert
            // If it returns (0,0), then it failed found or index.
            Assert.NotEqual(0m, range0.High); 
            Assert.Equal(-9.0m, range0.High);
            Assert.Equal(-11.0m, range0.Low);
        }

        [Fact]
        public void GetPriceRange_PerformanceMode_ShouldIncludePrimarySymbolInScaling()
        {
            // Arrange
            var primaryCloses = new decimal[] { 100m, 110m }; // +10%
            var otherCloses = new decimal[] { 100m, 90m };   // -10%
            var data = CreateTestData("BASE", "OTHER", primaryCloses, otherCloses);
            
            var calculator = new ComparisonPriceRangeCalculator(data, 0, ComparisonMode.Performance);
            
            // Act
            var candle1 = new CoreCandleData(data.Timestamps[1], 0, 0, 0, 0, 0);
            var range1 = calculator.GetPriceRange(candle1);
            
            // Assert
            Assert.Equal(10m, range1.High);
            Assert.Equal(-10m, range1.Low);
        }
    }
}
