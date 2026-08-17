using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Trend;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreElderRayIndicatorTests
    {
        private readonly List<CoreCandleData> _testData;

        public CoreElderRayIndicatorTests()
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
            var indicator = new CoreElderRayIndicator { Period = 13 };
            indicator.Calculate(_testData);

            Assert.NotNull(indicator.Values);
            Assert.Equal(_testData.Count, indicator.Values.Count);
            Assert.Equal(_testData.Count, indicator.BullPower.Count);
            Assert.Equal(_testData.Count, indicator.BearPower.Count);

            // EMA period determines the first non-null value
            var ema = new CoreEmaIndicator { Period = indicator.Period };
            ema.Calculate(_testData);
            int firstNonNull = ema.Values.ToList().FindIndex(v => v.HasValue);

            Assert.True(indicator.Values.Skip(firstNonNull).All(v => v.HasValue));
            Assert.True(indicator.BullPower.Skip(firstNonNull).All(v => v.HasValue));
            Assert.True(indicator.BearPower.Skip(firstNonNull).All(v => v.HasValue));
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreElderRayIndicator();
            indicator.Calculate(new List<CoreCandleData>());

            Assert.Empty(indicator.Values);
            Assert.Empty(indicator.BullPower);
            Assert.Empty(indicator.BearPower);
        }

        [Fact]
        public void Calculate_WithInsufficientData_ReturnsNulls()
        {
            var indicator = new CoreElderRayIndicator { Period = 13 };
            var insufficientData = _testData.Take(12).ToList();
            indicator.Calculate(insufficientData);

            Assert.Equal(insufficientData.Count, indicator.Values.Count);
            Assert.True(indicator.Values.All(v => !v.HasValue));
            Assert.True(indicator.BullPower.All(v => !v.HasValue));
            Assert.True(indicator.BearPower.All(v => !v.HasValue));
        }
    }
}
