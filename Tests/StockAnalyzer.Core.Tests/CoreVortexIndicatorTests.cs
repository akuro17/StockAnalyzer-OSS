using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Trend;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreVortexIndicatorTests
    {
        private readonly List<CoreCandleData> _testData;

        public CoreVortexIndicatorTests()
        {
            _testData = new List<CoreCandleData>();
            for (int i = 0; i < 30; i++)
            {
                _testData.Add(new CoreCandleData(new System.DateTime(2023, 1, 1).AddDays(i), 100 + i, 110 + i, 90 + i, 105 + i, 1000 + i * 10));
            }
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsNonNullValues()
        {
            var indicator = new CoreVortexIndicator { Period = 14 };
            indicator.Calculate(_testData);
            var result = indicator.Values;
            var viPlus = indicator.VIPlus;
            var viMinus = indicator.VIMinus;

            Assert.NotNull(result);
            Assert.Equal(_testData.Count, result.Count);
            Assert.Equal(_testData.Count, viPlus.Count);
            Assert.Equal(_testData.Count, viMinus.Count);

            Assert.True(result.Skip(indicator.Period).All(v => v.HasValue));
            Assert.True(viPlus.Skip(indicator.Period).All(v => v.HasValue));
            Assert.True(viMinus.Skip(indicator.Period).All(v => v.HasValue));
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreVortexIndicator();
            indicator.Calculate(new List<CoreCandleData>());

            Assert.Empty(indicator.Values);
            Assert.Empty(indicator.VIPlus);
            Assert.Empty(indicator.VIMinus);
        }

        [Fact]
        public void Calculate_WithInsufficientData_ReturnsNulls()
        {
            var indicator = new CoreVortexIndicator { Period = 14 };
            var insufficientData = _testData.Take(13).ToList();
            indicator.Calculate(insufficientData);

            Assert.Equal(insufficientData.Count, indicator.Values.Count);
            Assert.True(indicator.Values.All(v => !v.HasValue));
            Assert.True(indicator.VIPlus.All(v => !v.HasValue));
            Assert.True(indicator.VIMinus.All(v => !v.HasValue));
        }
    }
}
