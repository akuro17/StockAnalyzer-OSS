using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreRviVolatilityIndicatorTests
    {
        private readonly List<CoreCandleData> _testData;

        public CoreRviVolatilityIndicatorTests()
        {
            _testData = new List<CoreCandleData>();
            for (int i = 0; i < 40; i++)
            {
                // Create some volatility
                decimal close = 100 + 10 * (decimal)System.Math.Sin(i / 5.0);
                _testData.Add(new CoreCandleData(new System.DateTime(2023, 1, 1).AddDays(i), close - 2, close + 5, close - 5, close, 1000));
            }
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsNonNullValues()
        {
            var indicator = new CoreRviVolatilityIndicator { Period = 10 };
            indicator.Calculate(_testData);
            var result = indicator.Values;

            Assert.NotNull(result);
            Assert.Equal(_testData.Count, result.Count);

            // First value requires Period for StdDev, then another Period for the EMA of StdDev
            // StdDev first value at Period-1. StdDev diff starts at Period. EMA needs `Period` of those diffs.
            // So, first non-null is at (Period-1) + 1 + (Period-1) = 2*Period -1
            int firstNonNull = 2 * indicator.Period - 1;

            Assert.True(result.Skip(firstNonNull).All(v => v.HasValue));
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreRviVolatilityIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }

        [Fact]
        public void Calculate_WithInsufficientData_ReturnsNulls()
        {
            var indicator = new CoreRviVolatilityIndicator { Period = 10 };
            var insufficientData = _testData.Take(18).ToList();
            indicator.Calculate(insufficientData);
            var result = indicator.Values;

            Assert.Equal(insufficientData.Count, result.Count);
            Assert.True(result.All(v => !v.HasValue));
        }
    }
}
