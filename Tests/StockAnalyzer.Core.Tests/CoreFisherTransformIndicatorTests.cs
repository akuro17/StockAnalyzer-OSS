using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreFisherTransformIndicatorTests
    {
        private readonly List<CoreCandleData> _testData;

        public CoreFisherTransformIndicatorTests()
        {
            _testData = new List<CoreCandleData>
            {
                new CoreCandleData(new System.DateTime(2023, 1, 1), 100, 110, 90, 105, 1000),
                new CoreCandleData(new System.DateTime(2023, 1, 2), 105, 115, 95, 110, 1200),
                new CoreCandleData(new System.DateTime(2023, 1, 3), 110, 120, 100, 115, 1100),
                new CoreCandleData(new System.DateTime(2023, 1, 4), 115, 125, 105, 120, 1300),
                new CoreCandleData(new System.DateTime(2023, 1, 5), 120, 130, 110, 125, 1400),
                new CoreCandleData(new System.DateTime(2023, 1, 6), 125, 135, 115, 130, 1500),
                new CoreCandleData(new System.DateTime(2023, 1, 7), 130, 140, 120, 135, 1600),
                new CoreCandleData(new System.DateTime(2023, 1, 8), 135, 145, 125, 140, 1700),
                new CoreCandleData(new System.DateTime(2023, 1, 9), 140, 150, 130, 145, 1800),
                new CoreCandleData(new System.DateTime(2023, 1, 10), 145, 155, 135, 150, 1900),
                new CoreCandleData(new System.DateTime(2023, 1, 11), 150, 160, 140, 155, 2000),
                new CoreCandleData(new System.DateTime(2023, 1, 12), 155, 165, 145, 160, 2100),
                new CoreCandleData(new System.DateTime(2023, 1, 13), 160, 170, 150, 165, 2200),
                new CoreCandleData(new System.DateTime(2023, 1, 14), 165, 175, 155, 170, 2300),
                new CoreCandleData(new System.DateTime(2023, 1, 15), 170, 180, 160, 175, 2400)
            };
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsNonNullValues()
        {
            var indicator = new CoreFisherTransformIndicator { Period = 10 };
            indicator.Calculate(_testData);
            var result = indicator.Values;

            Assert.NotNull(result);
            Assert.Equal(_testData.Count, result.Count);
            Assert.True(result.Skip(indicator.Period - 1).All(v => v.HasValue));
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreFisherTransformIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            var result = indicator.Values;

            Assert.Empty(result);
        }

        [Fact]
        public void Calculate_WithInsufficientData_ReturnsNulls()
        {
            var indicator = new CoreFisherTransformIndicator { Period = 10 };
            var insufficientData = _testData.Take(9).ToList();
            indicator.Calculate(insufficientData);
            var result = indicator.Values;

            Assert.Equal(insufficientData.Count, result.Count);
            Assert.True(result.All(v => !v.HasValue));
        }
    }
}
