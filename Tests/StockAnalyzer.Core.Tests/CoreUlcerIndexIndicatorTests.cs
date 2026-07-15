using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreUlcerIndexIndicatorTests
    {
        private readonly List<CoreCandleData> _testData;

        public CoreUlcerIndexIndicatorTests()
        {
            _testData = new List<CoreCandleData>();
            for (int i = 0; i < 30; i++)
            {
                // Create some volatility
                decimal close = 100 + 5 * (decimal)System.Math.Sin(i);
                _testData.Add(new CoreCandleData(new System.DateTime(2023, 1, 1).AddDays(i), close - 2, close + 2, close - 5, close, 1000));
            }
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsNonNullValues()
        {
            var indicator = new CoreUlcerIndexIndicator { Period = 14 };
            indicator.Calculate(_testData);
            var result = indicator.Values;

            Assert.NotNull(result);
            Assert.Equal(_testData.Count, result.Count);
            Assert.True(result.Skip(indicator.Period - 1).All(v => v.HasValue));
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreUlcerIndexIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }

        [Fact]
        public void Calculate_WithInsufficientData_ReturnsNulls()
        {
            var indicator = new CoreUlcerIndexIndicator { Period = 14 };
            var insufficientData = _testData.Take(13).ToList();
            indicator.Calculate(insufficientData);
            var result = indicator.Values;

            Assert.Equal(insufficientData.Count, result.Count);
            Assert.True(result.All(v => !v.HasValue));
        }
    }
}
