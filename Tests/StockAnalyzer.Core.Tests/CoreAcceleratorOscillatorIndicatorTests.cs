using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreAcceleratorOscillatorIndicatorTests
    {
        private readonly List<CoreCandleData> _testData;

        public CoreAcceleratorOscillatorIndicatorTests()
        {
            _testData = new List<CoreCandleData>();
            for (int i = 0; i < 50; i++)
            {
                _testData.Add(new CoreCandleData(new System.DateTime(2023, 1, 1).AddDays(i), 100 + i, 110 + i, 90 + i, 105 + i, 1000));
            }
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsNonNullValues()
        {
            var indicator = new CoreAcceleratorOscillatorIndicator { FastPeriod = 5, SlowPeriod = 34, SmoothPeriod = 5 };
            indicator.Calculate(_testData);
            var result = indicator.Values;

            Assert.NotNull(result);
            Assert.Equal(_testData.Count, result.Count);

            // First non-null value depends on AO and the smoothing period
            int firstNonNull = indicator.SlowPeriod + indicator.SmoothPeriod - 2;
            Assert.True(result.Skip(firstNonNull).All(v => v.HasValue));
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreAcceleratorOscillatorIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }

        [Fact]
        public void Calculate_WithInsufficientData_ReturnsNulls()
        {
            var indicator = new CoreAcceleratorOscillatorIndicator { FastPeriod = 5, SlowPeriod = 34, SmoothPeriod = 5 };
            int insufficientCount = indicator.SlowPeriod + indicator.SmoothPeriod - 3;
            var insufficientData = _testData.Take(insufficientCount).ToList();
            indicator.Calculate(insufficientData);
            var result = indicator.Values;

            Assert.Equal(insufficientData.Count, result.Count);
            Assert.True(result.All(v => !v.HasValue));
        }
    }
}
