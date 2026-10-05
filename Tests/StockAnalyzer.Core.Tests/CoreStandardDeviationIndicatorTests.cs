using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreStandardDeviationIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(decimal[] closes)
        {
            var startDate = DateTime.Today;
            return closes.Select((close, i) => new CoreCandleData(
                startDate.AddDays(i),
                i > 0 ? closes[i-1] : close,
                close + 1,
                close - 1,
                close,
                1000
            )).ToList();
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsCorrectStdDev()
        {
            var indicator = new CoreStandardDeviationIndicator { Period = 5 };
            var closes = new decimal[] { 10, 12, 11, 13, 10, 11, 12 };
            var candles = CreateTestCandles(closes);
            indicator.Calculate(candles);

            // Period 1: Var=1.7, StdDev=sqrt(1.7)=1.3038
            // Period 2: Var=1.3, StdDev=sqrt(1.3)=1.1402
            // Period 3: Var=1.3, StdDev=sqrt(1.3)=1.1402
            var expected = new decimal?[]
            {
                null, null, null, null,
                1.30384048104052974295438038m,
                1.14017542509913797913304524m,
                1.14017542509913797913304524m
            };

            Assert.Equal(expected.Length, indicator.Values.Count);
            for (int i = 0; i < expected.Length; i++)
            {
                if (expected[i].HasValue)
                {
                    Assert.Equal(expected[i].Value, indicator.Values[i].Value, 8);
                }
                else
                {
                    Assert.Null(indicator.Values[i]);
                }
            }
        }

        [Fact]
        public void Calculate_WithNotEnoughData_ReturnsAllNulls()
        {
            var indicator = new CoreStandardDeviationIndicator { Period = 5 };
            var candles = CreateTestCandles(new decimal[] { 10, 12, 11 });
            indicator.Calculate(candles);
            Assert.Equal(3, indicator.Values.Count);
            Assert.All(indicator.Values, v => Assert.Null(v));
        }

        [Fact]
        public void Calculate_WithConstantData_ReturnsZeroStdDev()
        {
            var indicator = new CoreStandardDeviationIndicator { Period = 5 };
            var candles = CreateTestCandles(new decimal[] { 10, 10, 10, 10, 10 });
            indicator.Calculate(candles);
            Assert.Equal(0, indicator.Values.Last().Value, 4);
        }

        [Fact]
        public void Calculate_WithPeriodOne_DoesNotDivideByZero_ReturnsZero()
        {
            var indicator = new CoreStandardDeviationIndicator { Period = 1 };
            var candles = CreateTestCandles(new decimal[] { 10, 12, 14 });
            var result = indicator.Calculate(candles);

            Assert.True(result.IsSuccessful);
            Assert.Equal(3, indicator.Values.Count);
            // Single period has 0 variance / standard deviation
            Assert.All(indicator.Values, v => Assert.Equal(0m, v));
        }

        [Fact]
        public void Calculate_WithIsPopulationTrue_CalculatesPopulationVariance()
        {
            // Closes: [10, 12, 14], Mean = 12. SumSqDiff = (10-12)^2 + (12-12)^2 + (14-12)^2 = 8
            // Population Variance = 8 / 3 = 2.66666667, StdDev = Sqrt(8/3) ~ 1.63299316m
            var indicator = new CoreStandardDeviationIndicator { Period = 3, IsPopulation = true };
            var candles = CreateTestCandles(new decimal[] { 10, 12, 14 });
            indicator.Calculate(candles);

            Assert.Equal(3, indicator.Values.Count);
            Assert.Null(indicator.Values[0]);
            Assert.Null(indicator.Values[1]);
            Assert.Equal(1.63299316m, indicator.Values[2]!.Value, 6);
        }

        [Fact]
        public void Period_Setter_ClampsToMinimumOfOne()
        {
            var indicator = new CoreStandardDeviationIndicator { Period = 0 };
            Assert.Equal(1, indicator.Period);

            indicator.Period = -5;
            Assert.Equal(1, indicator.Period);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(10001)]
        public void Parameter_Validate_ThrowsArgumentOutOfRangeException(int invalidPeriod)
        {
            var param = new StockAnalyzer.Core.Models.Parameters.CoreStandardDeviationParameter { Period = invalidPeriod };
            Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        }
    }
}
