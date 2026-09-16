using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volume;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreEaseOfMovementIndicatorTests
    {
        private readonly List<CoreCandleData> _testData;

        public CoreEaseOfMovementIndicatorTests()
        {
            _testData = new List<CoreCandleData>();
            for (int i = 0; i < 30; i++)
            {
                _testData.Add(new CoreCandleData(new System.DateTime(2023, 1, 1).AddDays(i), 100 + i, 110 + i, 90 + i, 105 + i, 10000 + i * 100));
            }
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsNonNullValues()
        {
            var indicator = new CoreEaseOfMovementIndicator { Period = 14 };
            indicator.Calculate(_testData);
            var result = indicator.Values;

            Assert.NotNull(result);
            Assert.Equal(_testData.Count, result.Count);
            // The first calculated value appears after 'Period' initial EMV values are ready.
            // EMV calculation starts from the 2nd candle. So we need Period+1 candles for the first value.
            // My implementation adds nulls until i < Period, so first value is at index Period.
            Assert.True(result.Skip(indicator.Period).All(v => v.HasValue));
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreEaseOfMovementIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }

        [Fact]
        public void Calculate_WithInsufficientData_ReturnsNulls()
        {
            var indicator = new CoreEaseOfMovementIndicator { Period = 14 };
            // Needs at least Period+1 data points because EMV itself needs a previous day.
            var insufficientData = _testData.Take(14).ToList();
            indicator.Calculate(insufficientData);
            var result = indicator.Values;

            Assert.Equal(insufficientData.Count, result.Count);
            Assert.True(result.All(v => !v.HasValue));
        }
    }
}
