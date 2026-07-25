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
    }
}
