using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volume;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreVolumeRocIndicatorTests
    {
        private readonly List<CoreCandleData> _testData;

        public CoreVolumeRocIndicatorTests()
        {
            _testData = new List<CoreCandleData>();
            for (int i = 0; i < 30; i++)
            {
                _testData.Add(new CoreCandleData(new System.DateTime(2023, 1, 1).AddDays(i), 100, 110, 90, 105, 1000 + i * 100));
            }
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsNonNullValues()
        {
            var indicator = new CoreVolumeRocIndicator { Period = 14 };
            indicator.Calculate(_testData);
            var result = indicator.Values;

            Assert.NotNull(result);
            Assert.Equal(_testData.Count, result.Count);
            Assert.True(result.Skip(indicator.Period).All(v => v.HasValue));
        }

        [Fact]
        public void Calculate_WithZeroVolume_ReturnsNull()
        {
            var indicator = new CoreVolumeRocIndicator { Period = 5 };
            var dataWithZeroVol = new List<CoreCandleData>
            {
                new CoreCandleData(System.DateTime.Now, 100, 100, 100, 100, 0), // This will be the divisor
                new CoreCandleData(System.DateTime.Now, 100, 100, 100, 100, 100),
                new CoreCandleData(System.DateTime.Now, 100, 100, 100, 100, 100),
                new CoreCandleData(System.DateTime.Now, 100, 100, 100, 100, 100),
                new CoreCandleData(System.DateTime.Now, 100, 100, 100, 100, 100),
                new CoreCandleData(System.DateTime.Now, 100, 100, 100, 100, 100) // This will be calculated
            };

            indicator.Calculate(dataWithZeroVol);

            Assert.Null(indicator.Values.Last());
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreVolumeRocIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }

        [Fact]
        public void Calculate_WithInsufficientData_ReturnsNulls()
        {
            var indicator = new CoreVolumeRocIndicator { Period = 14 };
            var insufficientData = _testData.Take(14).ToList();
            indicator.Calculate(insufficientData);
            var result = indicator.Values;

            Assert.Equal(insufficientData.Count, result.Count);
            Assert.True(result.All(v => !v.HasValue));
        }
    }
}
