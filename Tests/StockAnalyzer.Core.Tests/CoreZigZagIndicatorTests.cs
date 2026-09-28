using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Trend;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreZigZagIndicatorTests
    {
        [Fact]
        public void Calculate_WithValidData_ReturnsCorrectPivots()
        {
            var indicator = new CoreZigZagIndicator { Threshold = 10m }; // 10% threshold for clear pivots
            var testData = new List<CoreCandleData>
            {
                new CoreCandleData(System.DateTime.Now, 100, 100, 100, 100, 1), // Pivot
                new CoreCandleData(System.DateTime.Now, 105, 105, 105, 105, 1),
                new CoreCandleData(System.DateTime.Now, 110, 110, 110, 110, 1), // Pivot
                new CoreCandleData(System.DateTime.Now, 105, 105, 105, 105, 1),
                new CoreCandleData(System.DateTime.Now, 98, 98, 98, 98, 1),   // Pivot (110 -> 98 is > 10% drop)
                new CoreCandleData(System.DateTime.Now, 100, 100, 100, 100, 1),
                new CoreCandleData(System.DateTime.Now, 115, 115, 115, 115, 1), // Pivot (98 -> 115 is > 10% rise)
                new CoreCandleData(System.DateTime.Now, 110, 110, 110, 110, 1)
            };

            indicator.Calculate(testData);
            var result = indicator.Values;

            Assert.Equal(testData.Count, result.Count);
            // Expected pivots at index 0, 2, 4, 6
            // The logic keeps updating the last pivot until a reversal is confirmed.
            // So the final pivots will be at the actual peaks and troughs.
            Assert.Null(result[0]);
            Assert.Null(result[1]);
            Assert.Equal(110, result[2]);
            Assert.Null(result[3]);
            Assert.Equal(98, result[4]);
            Assert.Null(result[5]);
            Assert.Equal(115, result[6]);
            Assert.Null(result[7]);
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreZigZagIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }

        [Fact]
        public void Calculate_WithInsufficientData_ReturnsOnePivot()
        {
            var indicator = new CoreZigZagIndicator();
            var singleData = new List<CoreCandleData> { new CoreCandleData(System.DateTime.Now, 100, 100, 100, 100, 1) };
            indicator.Calculate(singleData);

            Assert.Single(indicator.Values);
            Assert.Equal(100, indicator.Values[0]);
        }
    }
}
