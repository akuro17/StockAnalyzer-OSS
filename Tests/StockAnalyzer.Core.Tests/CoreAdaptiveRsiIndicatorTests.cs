using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class CoreAdaptiveRsiIndicatorTests
{
    private readonly List<CoreCandleData> _testData;

    public CoreAdaptiveRsiIndicatorTests()
    {
        _testData = new List<CoreCandleData>();
        var startDate = new DateTime(2023, 1, 1);
        decimal price = 100m;
        for (int i = 0; i < 40; i++)
        {
            decimal change = (i % 2 == 0) ? 3.0m : -2.0m;
            price += change;
            _testData.Add(new CoreCandleData(startDate.AddDays(i), price - 1, price + 2, price - 2, price, 1000));
        }
    }

    [Fact]
    public void CalculateCore_ThrowsNotSupportedException()
    {
        var indicator = new CoreAdaptiveRsiIndicator();
        Assert.Throws<NotSupportedException>(() => indicator.Calculate(_testData));
    }

    [Fact]
    public async Task CalculateAsync_WithEmptyData_ReturnsEmpty()
    {
        var indicator = new CoreAdaptiveRsiIndicator();
        var mockPythonService = new AdaptiveRsiMockPythonService();
        var result = await indicator.CalculateAsync(new List<CoreCandleData>(), new CoreExecutionContext(mockPythonService));

        Assert.True(result.IsSuccessful);
        Assert.Empty(indicator.Values);
    }

    [Fact]
    public async Task CalculateAsync_WithoutPythonService_FallsBackToDefaultRsi()
    {
        var indicator = new CoreAdaptiveRsiIndicator();
        var result = await indicator.CalculateAsync(_testData, new CoreExecutionContext(null));

        Assert.True(result.IsSuccessful);
        Assert.Equal(_testData.Count, indicator.Values.Count);
        // Valid RSI values computed using DefaultPeriod fallback
        for (int i = indicator.DefaultPeriod; i < indicator.Values.Count; i++)
        {
            Assert.NotNull(indicator.Values[i]);
            Assert.InRange(indicator.Values[i]!.Value, 0m, 100m);
        }
        // DominantPeriod list is populated with nulls
        Assert.Equal(_testData.Count, indicator.DominantPeriod.Count);
        Assert.All(indicator.DominantPeriod, Assert.Null);
    }

    [Fact]
    public async Task CalculateAsync_WithMockPythonService_ComputesAdaptiveRsiAndSetsDominantPeriod()
    {
        var indicator = new CoreAdaptiveRsiIndicator();
        var mockPythonService = new AdaptiveRsiMockPythonService();
        var result = await indicator.CalculateAsync(_testData, new CoreExecutionContext(mockPythonService));

        Assert.True(result.IsSuccessful);
        Assert.Equal(_testData.Count, indicator.Values.Count);
        Assert.Equal(_testData.Count, indicator.DominantPeriod.Count);

        // First bar is null in mock cycle, subsequent bars have value 16.0
        Assert.Null(indicator.DominantPeriod[0]);
        Assert.True(indicator.DominantPeriod.Skip(1).All(v => v.HasValue));

        // Result values beyond default period have valid RSI
        for (int i = indicator.DefaultPeriod; i < indicator.Values.Count; i++)
        {
            Assert.NotNull(indicator.Values[i]);
            Assert.InRange(indicator.Values[i]!.Value, 0m, 100m);
        }
    }

    [Fact]
    public async Task CalculateAsync_WithPythonException_GracefullyFallsBackToDefaultRsi()
    {
        var indicator = new CoreAdaptiveRsiIndicator();
        var failingPythonService = new FailingMockPythonService();
        var result = await indicator.CalculateAsync(_testData, new CoreExecutionContext(failingPythonService));

        Assert.True(result.IsSuccessful);
        Assert.Equal(_testData.Count, indicator.Values.Count);
        for (int i = indicator.DefaultPeriod; i < indicator.Values.Count; i++)
        {
            Assert.NotNull(indicator.Values[i]);
        }
    }

    [Fact]
    public void IndicatorFactory_ShouldCreateAdaptiveRsiIndicator()
    {
        var factory = new IndicatorFactory();
        Assert.True(factory.IsRegistered(IndicatorType.AdaptiveRSI));

        var indicator = factory.Create(IndicatorType.AdaptiveRSI);
        Assert.NotNull(indicator);
        Assert.IsType<CoreAdaptiveRsiIndicator>(indicator);
    }

    [Fact]
    public void Configure_SetsParametersCorrectly()
    {
        var indicator = new CoreAdaptiveRsiIndicator();
        var param = new CoreAdaptiveRsiParameter
        {
            WindowSize = 128,
            DefaultPeriod = 21,
            MinPeriod = 8,
            MaxPeriod = 64
        };

        indicator.Configure(param);

        Assert.Equal(128, indicator.WindowSize);
        Assert.Equal(21, indicator.DefaultPeriod);
        Assert.Equal(8, indicator.MinPeriod);
        Assert.Equal(64, indicator.MaxPeriod);
        Assert.Equal("Adaptive RSI (128, 21)", indicator.Name);
    }

    private class AdaptiveRsiMockPythonService : MockPythonServiceBase
    {
        public override Task<string> CalculateFftCycleAsync(int windowSize = ChartConstants.FftCycleDefaultWindowSize)
        {
            var values = new List<string> { "null" };
            values.AddRange(Enumerable.Repeat("16.0", 39));
            var cycle = string.Join(",", values);

            return Task.FromResult($"{{\"status\":\"ok\",\"result\":{{\"cycle\":[{cycle}]}}}}");
        }
    }

    private class FailingMockPythonService : MockPythonServiceBase
    {
        public override Task<string> CalculateFftCycleAsync(int windowSize = ChartConstants.FftCycleDefaultWindowSize)
        {
            throw new PythonUnavailableException("Circuit breaker open test");
        }
    }
}
