using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Trend;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests
{
    public class CoreSSAIndicatorTests
    {
        private readonly List<CoreCandleData> _testData;

        public CoreSSAIndicatorTests()
        {
            _testData = new List<CoreCandleData>();
            for (int i = 0; i < 70; i++)
            {
                decimal price = 100m + (decimal)Math.Sin(i * 0.2) * 10m + i * 0.5m;
                _testData.Add(new CoreCandleData(
                    new DateTime(2023, 1, 1).AddDays(i),
                    price - 1m,
                    price + 2m,
                    price - 2m,
                    price,
                    1000 + i * 10
                ));
            }
        }

        [Fact]
        public void Calculate_WithValidData_ReturnsSuccessAndValues()
        {
            var indicator = new CoreSSAIndicator { WindowSize = 16, EmbeddingDimension = 6, NumComponents = 2 };
            var result = indicator.Calculate(_testData);

            Assert.True(result.IsSuccessful);
            Assert.Equal(_testData.Count, indicator.Values.Count);

            for (int i = 0; i < 15; i++)
            {
                Assert.Null(indicator.Values[i]);
            }

            for (int i = 15; i < _testData.Count; i++)
            {
                Assert.NotNull(indicator.Values[i]);
                Assert.True(indicator.Values[i] > 0m);
            }
        }

        [Fact]
        public void IsOverlay_IsTrue()
        {
            var indicator = new CoreSSAIndicator();
            Assert.True(indicator.IsOverlay);
        }

        [Fact]
        public void IndicatorFactory_IsRegistered_ReturnsTrue()
        {
            Assert.True(IndicatorFactory.Default.IsRegistered(IndicatorType.SSA));
            var indicator = IndicatorFactory.Default.Create(IndicatorType.SSA);
            Assert.NotNull(indicator);
            Assert.IsType<CoreSSAIndicator>(indicator);
        }

        [Fact]
        public void DefaultSettings_HasCorrectValues()
        {
            var settingsList = DefaultCoreIndicatorSettings.GetDefault();
            var ssaSettings = settingsList.FirstOrDefault(s => s.TypeEnum == IndicatorType.SSA);
            Assert.NotNull(ssaSettings);
            Assert.True(ssaSettings.IsOverlay);
            Assert.Equal(CoreIndicatorCategory.Trend, ssaSettings.Category);
            Assert.Equal(IndicatorDefaultConstants.SsaColor, ssaSettings.Color);
            Assert.IsType<CoreSSAParameter>(ssaSettings.ParameterObject);
        }

        [Fact]
        public void Parameter_Validation_EnforcesConstraints()
        {
            var param = new CoreSSAParameter
            {
                WindowSize = 64,
                EmbeddingDimension = 20,
                NumComponents = 2
            };
            param.Validate(); // Valid, should not throw

            // WindowSize < 4
            param.WindowSize = 3;
            Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
            param.WindowSize = 64;

            // EmbeddingDimension < 2
            param.EmbeddingDimension = 1;
            Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());

            // EmbeddingDimension > WindowSize / 2
            param.EmbeddingDimension = 33;
            Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
            param.EmbeddingDimension = 20;

            // NumComponents < 1
            param.NumComponents = 0;
            Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());

            // NumComponents > min(L, K)
            param.NumComponents = 21; // L=20, maxR = 20
            Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        }

        [Fact]
        public void Parameter_Configure_AppliesToIndicator()
        {
            var indicator = new CoreSSAIndicator();
            var param = new CoreSSAParameter
            {
                WindowSize = 32,
                EmbeddingDimension = 10,
                NumComponents = 3
            };
            indicator.Configure(param);

            Assert.Equal(32, indicator.WindowSize);
            Assert.Equal(10, indicator.EmbeddingDimension);
            Assert.Equal(3, indicator.NumComponents);
            Assert.Equal("SSA (32, 10, 3)", indicator.Name);
        }

        [Fact]
        public async Task CalculateAsync_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreSSAIndicator();
            var result = await indicator.CalculateAsync(new List<CoreCandleData>(), null!);

            Assert.True(result.IsSuccessful);
            Assert.Empty(indicator.Values);
        }

        [Fact]
        public async Task CalculateAsync_WithValidData_ReturnsSuccessAndValues()
        {
            var indicator = new CoreSSAIndicator { WindowSize = 16, EmbeddingDimension = 6, NumComponents = 2 };
            var result = await indicator.CalculateAsync(_testData, null!);

            Assert.True(result.IsSuccessful);
            Assert.Equal(_testData.Count, indicator.Values.Count);
            Assert.Equal(_testData.Count, result.GetSeries(IndicatorResult.MainSeriesName).Count);

            // First WindowSize - 1 bars should be null
            for (int i = 0; i < 15; i++)
            {
                Assert.Null(indicator.Values[i]);
            }
            // Bar WindowSize - 1 onwards should have finite non-null values
            for (int i = 15; i < _testData.Count; i++)
            {
                Assert.NotNull(indicator.Values[i]);
            }
        }

        [Fact]
        public void Calculate_Causality_StrictNonRepaintingTest()
        {
            var indicator = new CoreSSAIndicator { WindowSize = 16, EmbeddingDimension = 6, NumComponents = 2 };

            // Run 1: with 40 bars
            var data40 = _testData.Take(40).ToList();
            var result40 = indicator.Calculate(data40);
            Assert.True(result40.IsSuccessful);
            var values40 = indicator.Values.ToList();

            // Run 2: with 70 bars (additional 30 future bars)
            var result70 = indicator.Calculate(_testData);
            Assert.True(result70.IsSuccessful);
            var values70 = indicator.Values.ToList();

            // Strict Non-Repaint Causality Verification:
            // The historical output values for bars 0..39 MUST be bit-level identical regardless of future bars 40..69!
            for (int i = 0; i < 40; i++)
            {
                Assert.Equal(values40[i], values70[i]);
            }
        }

        [Fact]
        public void Calculate_FlatConstantSeries_ReturnsConstant()
        {
            var flatData = new List<CoreCandleData>();
            for (int i = 0; i < 30; i++)
            {
                flatData.Add(new CoreCandleData(new DateTime(2023, 1, 1).AddDays(i), 100m, 100m, 100m, 100m, 1000));
            }

            var indicator = new CoreSSAIndicator { WindowSize = 10, EmbeddingDimension = 4, NumComponents = 2 };
            var result = indicator.Calculate(flatData);

            Assert.True(result.IsSuccessful);
            for (int i = 9; i < 30; i++)
            {
                Assert.Equal(100m, indicator.Values[i]);
            }
        }

        [Fact]
        public void Calculate_ShortData_ReturnsAllNulls()
        {
            var shortData = _testData.Take(5).ToList();
            var indicator = new CoreSSAIndicator { WindowSize = 10, EmbeddingDimension = 4, NumComponents = 2 };
            var result = indicator.Calculate(shortData);

            Assert.True(result.IsSuccessful);
            Assert.Equal(5, indicator.Values.Count);
            Assert.All(indicator.Values, val => Assert.Null(val));
        }
    }
}
