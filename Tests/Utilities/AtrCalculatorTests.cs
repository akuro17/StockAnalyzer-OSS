using Xunit;
using StockAnalyzer.Core.Utilities;
using StockAnalyzer.Core.Models;
using System.Collections.Generic;
using System;

namespace StockAnalyzer.Tests.Utilities
{
    public class AtrCalculatorTests
    {
        [Fact]
        public void Calculate_ShouldReturnCorrectValue_ForSimpleData()
        {
            // Arrange
            var candles = new List<CandleData>
            {
                new CandleData { Close = 100, High = 105, Low = 95, Timestamp = DateTime.Now }, // TR = 10 (H-L)
                new CandleData { Close = 110, High = 115, Low = 105, Timestamp = DateTime.Now.AddDays(1) }, // TR = Max(10, |115-100|, |105-100|) = 15
                new CandleData { Close = 105, High = 112, Low = 102, Timestamp = DateTime.Now.AddDays(2) }, // TR = Max(10, |112-110|, |102-110|) = 10
            };

            // Act
            // period = 2. We expect average of last 2 TRs: (15 + 10) / 2 = 12.5
            var result = AtrCalculator.Calculate(candles, 2);

            // Assert
            Assert.Equal(12.5m, result);
        }

        [Fact]
        public void Calculate_ShouldReturnFallback_WhenInsufficientData()
        {
            // Arrange
            var candles = new List<CandleData>
            {
                new CandleData { Close = 100, High = 105, Low = 95, Timestamp = DateTime.Now },
            };

            // Act
            var result = AtrCalculator.Calculate(candles, 14);

            // Assert
            Assert.Equal(1.0m, result); // 1% of 100
        }

        [Fact]
        public void CalculateTrueRange_WithoutPreviousClose_ReturnsHighMinusLow()
        {
            decimal tr = AtrCalculator.CalculateTrueRange(105m, 95m, null);
            Assert.Equal(10m, tr);
        }

        [Fact]
        public void CalculateTrueRange_WithPreviousClose_HandlesGapUpAndDown()
        {
            // Normal
            Assert.Equal(7m, AtrCalculator.CalculateTrueRange(108m, 101m, 102m));
            // Gap Down: prevClose 107, H 98, L 85 -> max(13, 9, 22) = 22
            Assert.Equal(22m, AtrCalculator.CalculateTrueRange(98m, 85m, 107m));
            // Gap Up: prevClose 92, H 125, L 112 -> max(13, 33, 20) = 33
            Assert.Equal(33m, AtrCalculator.CalculateTrueRange(125m, 112m, 92m));
        }

        [Fact]
        public void CalculateTrueRange_InvalidHighLow_ClampsToZero()
        {
            decimal tr = AtrCalculator.CalculateTrueRange(90m, 100m, null);
            Assert.Equal(0m, tr);
        }

        [Fact]
        public void CalculateTrueRangeSeries_CandleDataSpan_CalculatesCorrectly()
        {
            var baseDate = new DateTime(2026, 1, 1);
            var candles = new CandleData[]
            {
                new(baseDate, 100m, 105m, 95m, 102m, 1000),
                new(baseDate.AddDays(1), 103m, 108m, 101m, 107m, 1200),
                new(baseDate.AddDays(2), 90m, 98m, 85m, 92m, 1500),
                new(baseDate.AddDays(3), 115m, 125m, 112m, 120m, 1800),
            };

            Span<decimal> tr = stackalloc decimal[candles.Length];
            AtrCalculator.CalculateTrueRangeSeries(candles, tr);

            Assert.Equal(10m, tr[0]); // 105 - 95
            Assert.Equal(7m, tr[1]);  // 108 - 101
            Assert.Equal(22m, tr[2]); // 107 - 85
            Assert.Equal(33m, tr[3]); // 125 - 92
        }

        [Fact]
        public void CalculateTrueRangeSeries_CoreCandleDataSpan_CalculatesCorrectly()
        {
            var baseDate = new DateTime(2026, 1, 1);
            var candles = new CoreCandleData[]
            {
                new(baseDate, 100m, 105m, 95m, 102m, 1000),
                new(baseDate.AddDays(1), 103m, 108m, 101m, 107m, 1200),
                new(baseDate.AddDays(2), 90m, 98m, 85m, 92m, 1500),
                new(baseDate.AddDays(3), 115m, 125m, 112m, 120m, 1800),
            };

            Span<decimal> tr = stackalloc decimal[candles.Length];
            AtrCalculator.CalculateTrueRangeSeries(candles, tr);

            Assert.Equal(10m, tr[0]);
            Assert.Equal(7m, tr[1]);
            Assert.Equal(22m, tr[2]);
            Assert.Equal(33m, tr[3]);
        }

        [Fact]
        public void CalculateTrueRangeSeries_InsufficientDestination_ThrowsArgumentException()
        {
            var candles = new CandleData[3];
            var shortDest = new decimal[2];

            Assert.Throws<ArgumentException>(() =>
            {
                AtrCalculator.CalculateTrueRangeSeries(candles.AsSpan(), shortDest.AsSpan());
            });
        }
    }
}
