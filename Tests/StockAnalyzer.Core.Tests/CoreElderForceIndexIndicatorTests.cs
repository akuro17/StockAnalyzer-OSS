using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreElderForceIndexIndicatorTests
    {
        private readonly List<CoreCandleData> _testData;

        public CoreElderForceIndexIndicatorTests()
        {
            _testData = new List<CoreCandleData>();
            for (int i = 0; i < 30; i++)
            {
                _testData.Add(new CoreCandleData(new System.DateTime(2023, 1, 1).AddDays(i), 100 + i, 110 + i, 90 + i, 105 + i, 1000 + i * 100));
            }
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsNonNullValues()
        {
            var indicator = new CoreElderForceIndexIndicator { Period = 13 };
            indicator.Calculate(_testData);
            var result = indicator.Values;

            Assert.NotNull(result);
            Assert.Equal(_testData.Count, result.Count);
            Assert.True(result.Skip(indicator.Period).All(v => v.HasValue));
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreElderForceIndexIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            var result = indicator.Values;

            Assert.Empty(result);
        }

        [Fact]
        public void Calculate_WithInsufficientData_ReturnsNulls()
        {
            var indicator = new CoreElderForceIndexIndicator { Period = 13 };
            var insufficientData = _testData.Take(13).ToList();
            indicator.Calculate(insufficientData);
            var result = indicator.Values;

            Assert.Equal(insufficientData.Count, result.Count);
            Assert.True(result.All(v => !v.HasValue));
        }
    }
}
