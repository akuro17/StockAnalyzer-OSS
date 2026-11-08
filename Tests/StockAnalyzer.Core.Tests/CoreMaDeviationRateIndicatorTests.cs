using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using StockAnalyzer.Core.Models.Parameters;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreMaDeviationRateIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(IEnumerable<decimal> closePrices)
        {
            var startDate = DateTime.Today;
            return closePrices.Select((price, i) => new CoreCandleData(
                startDate.AddDays(i), price, price + 5, price - 5, price, 1000
            )).ToList();
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreMaDeviationRateIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }

        [Fact]
        public void Calculate_WithSampleData_ReturnsValues()
        {
            var indicator = new CoreMaDeviationRateIndicator();
            var candles = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14, 15, 16 });
            indicator.Calculate(candles);
            Assert.Equal(candles.Count, indicator.Values.Count);
        }

        [Fact]
        public void Configure_WithCoreMaDeviationRateParameter_SetsPeriodAndMaType()
        {
            var indicator = new CoreMaDeviationRateIndicator();
            var param = new CoreMaDeviationRateParameter
            {
                Period = 14,
                MaType = MovingAverageType.EMA
            };

            indicator.Configure(param);

            Assert.Equal(14, indicator.Period);
            Assert.Equal(MovingAverageType.EMA, indicator.MaType);
            Assert.Contains("EMA", indicator.Name);
        }

        [Fact]
        public void Configure_WithLegacyCoreSmaParameter_PreservesBackwardCompatibility()
        {
            var indicator = new CoreMaDeviationRateIndicator();
            var param = new CoreSmaParameter { Period = 20 };

            indicator.Configure(param);

            Assert.Equal(20, indicator.Period);
            Assert.Equal(MovingAverageType.SMA, indicator.MaType);
        }

        [Fact]
        public void Calculate_WithDifferentMaTypes_ComputesSuccessfully()
        {
            var prices = Enumerable.Range(10, 50).Select(x => (decimal)x).ToArray();
            var candles = CreateTestCandles(prices);

            foreach (MovingAverageType maType in Enum.GetValues<MovingAverageType>())
            {
                var indicator = new CoreMaDeviationRateIndicator();
                indicator.Configure(new CoreMaDeviationRateParameter
                {
                    Period = 5,
                    MaType = maType
                });

                var result = indicator.Calculate(candles);
                Assert.True(result.IsSuccessful);
                Assert.Equal(candles.Count, indicator.Values.Count);
                // At least the final bar should have a valid deviation calculation
                Assert.True(indicator.Values[^1].HasValue, $"MovingAverageType.{maType} failed to compute final bar value with 50 bars.");
            }
        }

        [Fact]
        public void Parameter_Validate_ThrowsOnInvalidInput()
        {
            var paramInvalidPeriod = new CoreMaDeviationRateParameter { Period = 0 };
            Assert.Throws<ArgumentOutOfRangeException>(() => paramInvalidPeriod.Validate());

            var paramInvalidType = new CoreMaDeviationRateParameter { MaType = (MovingAverageType)999 };
            Assert.Throws<ArgumentOutOfRangeException>(() => paramInvalidType.Validate());

            var validParam = new CoreMaDeviationRateParameter { Period = 20, MaType = MovingAverageType.WMA };
            validParam.Validate(); // Does not throw
        }

        [Fact]
        public void GetDefaultSettings_DiscoversCoreMaDeviationRateParameter()
        {
            var indicator = new CoreMaDeviationRateIndicator();
            var settings = indicator.GetDefaultSettings();

            Assert.NotNull(settings.ParameterObject);
            Assert.IsType<CoreMaDeviationRateParameter>(settings.ParameterObject);
        }
    }
}
