using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreNatrIndicatorTests
    {
        private readonly List<CoreCandleData> _testData;

        public CoreNatrIndicatorTests()
        {
            _testData = new List<CoreCandleData>();
            for (int i = 0; i < 30; i++)
            {
                _testData.Add(new CoreCandleData(new System.DateTime(2023, 1, 1).AddDays(i), 100 + i, 110 + i, 90 + i, 105 + i, 1000));
            }
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsNonNullValues()
        {
            var indicator = new CoreNatrIndicator { Period = 14 };
            indicator.Calculate(_testData);
            var result = indicator.Values;

            Assert.NotNull(result);
            Assert.Equal(_testData.Count, result.Count);

            // ATR calculation determines the first non-null value
            var atr = new CoreAtrIndicator { Period = indicator.Period };
            atr.Calculate(_testData);
            int firstNonNull = atr.Values.ToList().FindIndex(v => v.HasValue);

            Assert.True(result.Skip(firstNonNull).All(v => v.HasValue));
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreNatrIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }

        [Fact]
        public void Calculate_WithInsufficientData_ReturnsNulls()
        {
            var indicator = new CoreNatrIndicator { Period = 14 };
            // ATR needs Period + 1 to calculate first value
            var insufficientData = _testData.Take(14).ToList();
            indicator.Calculate(insufficientData);
            var result = indicator.Values;

            Assert.Equal(insufficientData.Count, result.Count);
            Assert.True(result.All(v => !v.HasValue));
        }

        [Fact]
        public void Calculate_WithZeroClose_ReturnsNull()
        {
            var indicator = new CoreNatrIndicator { Period = 14 };
            var dataWithZeroClose = _testData.ToList();
            // Replace a value after the initial null period with a zero-close candle
            int indexToReplace = indicator.Period + 2;
            dataWithZeroClose[indexToReplace] = new CoreCandleData(dataWithZeroClose[indexToReplace].Timestamp, 10, 10, 10, 0, 1000);

            indicator.Calculate(dataWithZeroClose);
            var result = indicator.Values;

            Assert.Null(result[indexToReplace]);
        }
    }
}
