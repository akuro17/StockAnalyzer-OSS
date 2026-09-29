using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Trend;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreQStickIndicatorTests
    {
        private readonly List<CoreCandleData> _testData;

        public CoreQStickIndicatorTests()
        {
            _testData = new List<CoreCandleData>();
            for (int i = 0; i < 20; i++)
            {
                // Alternating positive and negative candles
                decimal open = 100 + i;
                decimal close = (i % 2 == 0) ? open + 5 : open - 5;
                _testData.Add(new CoreCandleData(new System.DateTime(2023, 1, 1).AddDays(i), open, close + 5, open - 5, close, 1000));
            }
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsNonNullValues()
        {
            var indicator = new CoreQStickIndicator { Period = 8 };
            indicator.Calculate(_testData);
            var result = indicator.Values;

            Assert.NotNull(result);
            Assert.Equal(_testData.Count, result.Count);
            Assert.True(result.Skip(indicator.Period - 1).All(v => v.HasValue));
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreQStickIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }

        [Fact]
        public void Calculate_WithInsufficientData_ReturnsNulls()
        {
            var indicator = new CoreQStickIndicator { Period = 8 };
            var insufficientData = _testData.Take(7).ToList();
            indicator.Calculate(insufficientData);
            var result = indicator.Values;

            Assert.Equal(insufficientData.Count, result.Count);
            Assert.True(result.All(v => !v.HasValue));
        }
    }
}
