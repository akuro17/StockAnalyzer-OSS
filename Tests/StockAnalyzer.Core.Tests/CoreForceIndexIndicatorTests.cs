using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volume;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreForceIndexIndicatorTests
    {
        private readonly List<CoreCandleData> _testData;

        public CoreForceIndexIndicatorTests()
        {
            _testData = new List<CoreCandleData>
            {
                new CoreCandleData(new System.DateTime(2023, 1, 1), 100, 105, 98, 102, 10000),
                new CoreCandleData(new System.DateTime(2023, 1, 2), 102, 108, 101, 107, 12000),
                new CoreCandleData(new System.DateTime(2023, 1, 3), 107, 110, 105, 108, 8000),
                new CoreCandleData(new System.DateTime(2023, 1, 4), 108, 112, 107, 106, 9000), // Close went down
            };
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsCorrectValues()
        {
            var indicator = new CoreForceIndexIndicator();
            indicator.Calculate(_testData);
            var result = indicator.Values;

            Assert.Equal(_testData.Count, result.Count);
            Assert.Null(result[0]);
            Assert.Equal((107 - 102) * 12000, result[1]);
            Assert.Equal((108 - 107) * 8000, result[2]);
            Assert.Equal((106 - 108) * 9000, result[3]);
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreForceIndexIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }

        [Fact]
        public void Calculate_WithInsufficientData_ReturnsOneNull()
        {
            var indicator = new CoreForceIndexIndicator();
            var insufficientData = new List<CoreCandleData> { _testData[0] };
            indicator.Calculate(insufficientData);

            Assert.Single(indicator.Values);
            Assert.Null(indicator.Values[0]);
        }
    }
}
