using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreConnorsRsiIndicatorTests
    {
        private readonly List<CoreCandleData> _testData;

        public CoreConnorsRsiIndicatorTests()
        {
            _testData = new List<CoreCandleData>();
            for (int i = 0; i < 110; i++)
            {
                _testData.Add(new CoreCandleData(new System.DateTime(2023, 1, 1).AddDays(i), 100 + i % 20, 110 + i % 20, 90 + i % 20, 105 + i % 20, 1000 + i * 10));
            }
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsNonNullValues()
        {
            var indicator = new CoreConnorsRsiIndicator { PercentRankPeriod = 100 };
            indicator.Calculate(_testData);
            var result = indicator.Values;

            Assert.NotNull(result);
            Assert.Equal(_testData.Count, result.Count);
            // First non-null value should appear at index `PercentRankPeriod`
            Assert.True(result.Skip(indicator.PercentRankPeriod).All(v => v.HasValue));
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreConnorsRsiIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            var result = indicator.Values;

            Assert.Empty(result);
        }

        [Fact]
        public void Calculate_WithInsufficientData_ReturnsNulls()
        {
            var indicator = new CoreConnorsRsiIndicator { PercentRankPeriod = 100 };
            var insufficientData = _testData.Take(100).ToList(); // Exactly one less than required
            indicator.Calculate(insufficientData);
            var result = indicator.Values;

            Assert.Equal(insufficientData.Count, result.Count);
            Assert.True(result.All(v => !v.HasValue));
        }
    }
}
