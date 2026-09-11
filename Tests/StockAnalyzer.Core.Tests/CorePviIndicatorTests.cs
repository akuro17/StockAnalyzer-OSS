using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volume;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CorePviIndicatorTests
    {
        private readonly List<CoreCandleData> _testData;

        public CorePviIndicatorTests()
        {
            _testData = new List<CoreCandleData>
            {
                new CoreCandleData(new System.DateTime(2023, 1, 1), 100, 105, 98, 102, 10000),
                new CoreCandleData(new System.DateTime(2023, 1, 2), 102, 108, 101, 107, 8000),   // Volume decreased, PVI unchanged
                new CoreCandleData(new System.DateTime(2023, 1, 3), 107, 110, 105, 108, 12000), // Volume increased, PVI changes
                new CoreCandleData(new System.DateTime(2023, 1, 4), 108, 112, 107, 110, 9000),  // Volume decreased, PVI unchanged
                new CoreCandleData(new System.DateTime(2023, 1, 5), 110, 115, 109, 112, 11000), // Volume increased, PVI changes
                new CoreCandleData(new System.DateTime(2023, 1, 6), 112, 112, 108, 110, 10000)  // Volume decreased, PVI unchanged
            };
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsCorrectLogic()
        {
            var indicator = new CorePviIndicator();
            indicator.Calculate(_testData);
            var result = indicator.Values;

            Assert.Equal(_testData.Count, result.Count);
            Assert.Equal(1000, result[0]); // Initial value
            Assert.Equal(result[0], result[1]); // Volume decreased
            Assert.NotEqual(result[1], result[2]); // Volume increased
            Assert.Equal(result[2], result[3]); // Volume decreased
            Assert.NotEqual(result[3], result[4]); // Volume increased
            Assert.Equal(result[4], result[5]); // Volume decreased
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CorePviIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }

        [Fact]
        public void Calculate_WithSingleDataPoint_ReturnsOneValue()
        {
            var indicator = new CorePviIndicator();
            var singleData = new List<CoreCandleData> { _testData[0] };
            indicator.Calculate(singleData);

            Assert.Single(indicator.Values);
            Assert.Equal(1000, indicator.Values[0]);
        }
    }
}
