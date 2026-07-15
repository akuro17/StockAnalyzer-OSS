using Moq;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Tests;

/// <summary>
/// Tests for AnalysisPipelineService verifying pipeline-level behavior:
/// indicator creation via IIndicatorFactory, error isolation between indicators,
/// and graceful degradation when individual indicators fail.
/// </summary>
public class AnalysisPipelineServiceTests
{
    private static List<CoreCandleData> CreateTestCandles(int count = 10)
    {
        var startDate = DateTime.Today;
        return Enumerable.Range(0, count).Select(i => new CoreCandleData(
            startDate.AddDays(i), 100 + i, 102 + i, 98 + i, 100 + i, 1000
        )).ToList();
    }

    private static CoreIndicatorSettings CreateSetting(
        string id, IndicatorType type, bool isEnabled = true)
    {
        return new CoreIndicatorSettings
        {
            Id = id,
            IsEnabled = isEnabled,
            TypeEnum = type,
            ParameterObject = new CoreSmaParameter { Period = 5 }
        };
    }

    // =====================================================================
    // 1. Valid indicator returns successful result
    // =====================================================================

    [Fact]
    public void CalculateIndicators_WithValidSmaIndicator_ReturnsSuccess()
    {
        // Use real IndicatorFactory — SMA is a pure C# indicator, no mocking needed
        var service = new AnalysisPipelineService(
            pythonService: null,
            indicatorFactory: IndicatorFactory.Default);

        var candles = CreateTestCandles(10);
        var settings = new List<CoreIndicatorSettings>
        {
            CreateSetting("TestSMA", IndicatorType.SMA)
        };

        var result = service.CalculateIndicators(candles, settings);

        Assert.NotNull(result);
        Assert.True(result.ContainsKey("TestSMA"));
        Assert.True(result["TestSMA"].IsSuccessful);
        Assert.NotEmpty(result["TestSMA"].MainValues);
    }

    // =====================================================================
    // 2. Null or empty candles returns empty dictionary
    // =====================================================================

    [Fact]
    public void CalculateIndicators_WithNullCandles_ReturnsEmptyDictionary()
    {
        var service = new AnalysisPipelineService();

        var result = service.CalculateIndicators(null!, new List<CoreIndicatorSettings>());

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void CalculateIndicators_WithEmptyCandles_ReturnsEmptyDictionary()
    {
        var service = new AnalysisPipelineService();

        var result = service.CalculateIndicators(
            new List<CoreCandleData>(),
            new List<CoreIndicatorSettings> { CreateSetting("SMA1", IndicatorType.SMA) });

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    // =====================================================================
    // 3. Disabled indicator is still calculated (for sidebar value display)
    //    Rendering layer skips disabled indicators, not the pipeline.
    // =====================================================================

    [Fact]
    public void CalculateIndicators_DisabledIndicator_IsStillCalculated()
    {
        var service = new AnalysisPipelineService(
            pythonService: null,
            indicatorFactory: IndicatorFactory.Default);

        var candles = CreateTestCandles();
        var settings = new List<CoreIndicatorSettings>
        {
            CreateSetting("DisabledSMA", IndicatorType.SMA, isEnabled: false)
        };

        var result = service.CalculateIndicators(candles, settings);

        Assert.NotNull(result);
        // Step 72: Disabled indicators are now always calculated for sidebar display
        Assert.Contains("DisabledSMA", result.Keys);
        Assert.True(result["DisabledSMA"].IsSuccessful);
    }

    // =====================================================================
    // 4. Async pipeline with mock Python service
    // =====================================================================

    [Fact]
    public async Task CalculateIndicatorsAsync_WithMockPythonService_ReturnsSuccess()
    {
        var mockPython = new Mock<IPythonService>();
        var service = new AnalysisPipelineService(
            pythonService: mockPython.Object,
            indicatorFactory: IndicatorFactory.Default);

        var candles = CreateTestCandles(20);
        var settings = new List<CoreIndicatorSettings>
        {
            CreateSetting("AsyncSMA", IndicatorType.SMA)
        };

        var result = await service.CalculateIndicatorsAsync(candles, settings);

        Assert.NotNull(result);
        Assert.True(result.ContainsKey("AsyncSMA"));
        Assert.True(result["AsyncSMA"].IsSuccessful);
    }

    // =====================================================================
    // 5. One indicator crash does not affect others (error isolation)
    // =====================================================================

    [Fact]
    public void CalculateIndicators_IndicatorCrash_ReturnsFailureResult_WithoutAffectingOthers()
    {
        // Create a mock factory that returns a crashing indicator for EMA
        // but the real SMA indicator for SMA
        var mockFactory = new Mock<IIndicatorFactory>();

        // SMA: return a real working indicator
        var realFactory = IndicatorFactory.Default;
        var realSma = realFactory.Create(IndicatorType.SMA, new CoreSmaParameter { Period = 5 });
        mockFactory.Setup(f => f.Create(IndicatorType.SMA, It.IsAny<CoreIndicatorParameterBase>()))
            .Returns(realSma);

        // EMA: return a mock indicator that throws during CalculateAsync
        var crashingIndicator = new Mock<ICoreIndicator>();
        crashingIndicator.Setup(i => i.CalculateAsync(
                It.IsAny<IReadOnlyList<CoreCandleData>>(),
                It.IsAny<IExecutionContext>()))
            .ThrowsAsync(new DivideByZeroException("Simulated crash"));
        mockFactory.Setup(f => f.Create(IndicatorType.EMA, It.IsAny<CoreIndicatorParameterBase>()))
            .Returns(crashingIndicator.Object);

        var service = new AnalysisPipelineService(
            pythonService: null,
            indicatorFactory: mockFactory.Object);

        var candles = CreateTestCandles();
        var settings = new List<CoreIndicatorSettings>
        {
            CreateSetting("GoodSMA", IndicatorType.SMA),
            CreateSetting("CrashEMA", IndicatorType.EMA),
        };

        var result = service.CalculateIndicators(candles, settings);

        // SMA should succeed
        Assert.True(result.ContainsKey("GoodSMA"));
        Assert.True(result["GoodSMA"].IsSuccessful);

        // EMA should fail gracefully, not crash the pipeline
        Assert.True(result.ContainsKey("CrashEMA"));
        Assert.False(result["CrashEMA"].IsSuccessful);
        Assert.Contains("Crash", result["CrashEMA"].ErrorMessage);
    }
}
