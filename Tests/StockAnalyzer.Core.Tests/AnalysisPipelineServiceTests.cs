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
/// DAG dependency sorting, indicator-on-indicator chaining, and dynamic adaptive modulation.
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
        var mockFactory = new Mock<IIndicatorFactory>();

        var realFactory = IndicatorFactory.Default;
        var realSma = realFactory.Create(IndicatorType.SMA, new CoreSmaParameter { Period = 5 });
        mockFactory.Setup(f => f.Create(IndicatorType.SMA, It.IsAny<CoreIndicatorParameterBase>()))
            .Returns(realSma);

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

        Assert.True(result.ContainsKey("GoodSMA"));
        Assert.True(result["GoodSMA"].IsSuccessful);

        Assert.True(result.ContainsKey("CrashEMA"));
        Assert.False(result["CrashEMA"].IsSuccessful);
        Assert.Contains("Crash", result["CrashEMA"].ErrorMessage);
    }

    // =====================================================================
    // 6. DAG Topological Sorting & Dependency Chaining
    // =====================================================================

    [Fact]
    public void SortSettingsByDependency_OrdersDependenciesCorrectly()
    {
        var settingA = CreateSetting("IndA", IndicatorType.SMA);
        var settingB = CreateSetting("IndB", IndicatorType.RSI);
        settingB.SourceIndicatorId = "IndA";

        var settingC = CreateSetting("IndC", IndicatorType.EMA);
        settingC.DynamicPeriodIndicatorId = "IndB";

        // Pass in reverse order [C, B, A]
        var input = new List<CoreIndicatorSettings> { settingC, settingB, settingA };
        var sorted = AnalysisPipelineService.SortSettingsByDependency(input);

        Assert.Equal(3, sorted.Count);
        Assert.Equal("IndA", sorted[0].Id);
        Assert.Equal("IndB", sorted[1].Id);
        Assert.Equal("IndC", sorted[2].Id);
    }

    [Fact]
    public void SortSettingsByDependency_WithCircularDependency_RecoversGracefully()
    {
        var settingA = CreateSetting("IndA", IndicatorType.SMA);
        var settingB = CreateSetting("IndB", IndicatorType.EMA);
        settingA.SourceIndicatorId = "IndB";
        settingB.SourceIndicatorId = "IndA";

        var input = new List<CoreIndicatorSettings> { settingA, settingB };
        var sorted = AnalysisPipelineService.SortSettingsByDependency(input);

        // All items should be returned without throwing
        Assert.Equal(2, sorted.Count);
        Assert.Contains(sorted, s => s.Id == "IndA");
        Assert.Contains(sorted, s => s.Id == "IndB");
    }

    [Fact]
    public void CalculateIndicators_IndicatorChaining_FeedsUpstreamOutputIntoDownstream()
    {
        var service = new AnalysisPipelineService(
            pythonService: null,
            indicatorFactory: IndicatorFactory.Default);

        var candles = CreateTestCandles(30);

        var settingSma = CreateSetting("Sma1", IndicatorType.SMA);
        settingSma.ParameterObject = new CoreSmaParameter { Period = 3 };

        var settingRsi = CreateSetting("RsiOnSma", IndicatorType.RSI);
        settingRsi.ParameterObject = new CoreSmaParameter { Period = 3 };
        settingRsi.SourceIndicatorId = "Sma1"; // RSI of SMA

        var settings = new List<CoreIndicatorSettings> { settingRsi, settingSma }; // Input in arbitrary order

        var result = service.CalculateIndicators(candles, settings);

        Assert.True(result.ContainsKey("Sma1"));
        Assert.True(result["Sma1"].IsSuccessful);

        Assert.True(result.ContainsKey("RsiOnSma"));
        Assert.True(result["RsiOnSma"].IsSuccessful);

        // Verify that RsiOnSma calculated from SMA series (which has null warmup at indices 0, 1)
        Assert.Equal(30, result["RsiOnSma"].MainValues.Count);
    }

    [Fact]
    public void CalculateIndicators_DynamicPeriodDriver_DrivesAdaptiveIndicator()
    {
        var service = new AnalysisPipelineService(
            pythonService: null,
            indicatorFactory: IndicatorFactory.Default);

        var candles = CreateTestCandles(40);

        var htSetting = new CoreIndicatorSettings
        {
            Id = "HT1",
            IsEnabled = true,
            TypeEnum = IndicatorType.HilbertTransform,
            ParameterObject = new CoreHilbertTransformParameter { MinPeriod = 2, MaxPeriod = 50 }
        };

        var emaSetting = new CoreIndicatorSettings
        {
            Id = "AdaptiveEMA",
            IsEnabled = true,
            TypeEnum = IndicatorType.EMA,
            ParameterObject = new CoreSmaParameter { Period = 10 },
            DynamicPeriodIndicatorId = "HT1"
        };

        var settings = new List<CoreIndicatorSettings> { emaSetting, htSetting };

        var result = service.CalculateIndicators(candles, settings);

        Assert.True(result.ContainsKey("HT1"));
        Assert.True(result["HT1"].IsSuccessful);
        Assert.True(result.ContainsKey("AdaptiveEMA"));
        Assert.True(result["AdaptiveEMA"].IsSuccessful);
        Assert.Equal(40, result["AdaptiveEMA"].MainValues.Count);
    }

    [Fact]
    public void CalculateIndicators_PriceScaleDynamicPeriodDriver_RespondsToDriverPeriodChange()
    {
        // Regression test: a non-period-native driver (e.g. a plain SMA of price) must be normalized into a
        // usable period range. Before the fix, its raw price-scale output (always > the hardcoded 200-bar clamp
        // for this candle set) saturated to the same fixed period regardless of the driver's own settings, so
        // changing the driver's period had zero effect on the driven indicator.
        var service = new AnalysisPipelineService(
            pythonService: null,
            indicatorFactory: IndicatorFactory.Default);

        var candles = CreateTestCandles(300); // close prices rise from 100 to ~399, comfortably above the old 200-bar clamp

        var driverShort = new CoreIndicatorSettings
        {
            Id = "DriverShort",
            IsEnabled = true,
            TypeEnum = IndicatorType.SMA,
            ParameterObject = new CoreSmaParameter { Period = 25 }
        };

        var driverLong = new CoreIndicatorSettings
        {
            Id = "DriverLong",
            IsEnabled = true,
            TypeEnum = IndicatorType.SMA,
            ParameterObject = new CoreSmaParameter { Period = 250 }
        };

        var drivenViaShort = new CoreIndicatorSettings
        {
            Id = "DrivenViaShort",
            IsEnabled = true,
            TypeEnum = IndicatorType.SMA,
            ParameterObject = new CoreSmaParameter { Period = 5 },
            DynamicPeriodIndicatorId = "DriverShort"
        };

        var drivenViaLong = new CoreIndicatorSettings
        {
            Id = "DrivenViaLong",
            IsEnabled = true,
            TypeEnum = IndicatorType.SMA,
            ParameterObject = new CoreSmaParameter { Period = 5 },
            DynamicPeriodIndicatorId = "DriverLong"
        };

        var settings = new List<CoreIndicatorSettings> { driverShort, driverLong, drivenViaShort, drivenViaLong };
        var result = service.CalculateIndicators(candles, settings);

        Assert.True(result["DrivenViaShort"].IsSuccessful);
        Assert.True(result["DrivenViaLong"].IsSuccessful);

        bool anyDifference = false;
        for (int i = 0; i < candles.Count; i++)
        {
            var a = result["DrivenViaShort"].MainValues[i];
            var b = result["DrivenViaLong"].MainValues[i];
            if (a.HasValue && b.HasValue && a.Value != b.Value)
            {
                anyDifference = true;
                break;
            }
        }

        Assert.True(anyDifference, "Changing the dynamic period driver's own period must change the driven indicator's output.");
    }

    [Fact]
    public void CalculateIndicators_DynamicPeriodDriver_PeriodNativeHilbertUsedDirectly()
    {
        // Regression guard: period-native drivers (Hilbert Transform / FFT Cycle) already output bar counts and
        // must keep receiving their raw values unmodified (Direct mapping), not the min-max normalization applied
        // to non-period-native driver types.
        var service = new AnalysisPipelineService(
            pythonService: null,
            indicatorFactory: IndicatorFactory.Default);

        var candles = CreateTestCandles(60);

        var htSetting = new CoreIndicatorSettings
        {
            Id = "HT1",
            IsEnabled = true,
            TypeEnum = IndicatorType.HilbertTransform,
            ParameterObject = new CoreHilbertTransformParameter { MinPeriod = 2, MaxPeriod = 50 }
        };

        var smaSetting = new CoreIndicatorSettings
        {
            Id = "AdaptiveSMA",
            IsEnabled = true,
            TypeEnum = IndicatorType.SMA,
            ParameterObject = new CoreSmaParameter { Period = 5 },
            DynamicPeriodIndicatorId = "HT1"
        };

        var settings = new List<CoreIndicatorSettings> { smaSetting, htSetting };
        var result = service.CalculateIndicators(candles, settings);

        Assert.True(result["AdaptiveSMA"].IsSuccessful);
        Assert.Equal(60, result["AdaptiveSMA"].MainValues.Count);
    }

    [Fact]
    public void CalculateIndicators_DynamicPeriodDriver_SharedByMultipleConsumers_BothSucceed()
    {
        // Regression test: a single dynamic-period-driver result (read via result[DynamicPeriodIndicatorId])
        // must be usable by more than one downstream indicator at once without interference, since the
        // AnalysisPipelineService result dictionary is a shared, read-only lookup for all consumers.
        var service = new AnalysisPipelineService(
            pythonService: null,
            indicatorFactory: IndicatorFactory.Default);

        var candles = CreateTestCandles(60);

        var htSetting = new CoreIndicatorSettings
        {
            Id = "HT1",
            IsEnabled = true,
            TypeEnum = IndicatorType.HilbertTransform,
            ParameterObject = new CoreHilbertTransformParameter { MinPeriod = 2, MaxPeriod = 50 }
        };

        var consumerA = new CoreIndicatorSettings
        {
            Id = "ConsumerA",
            IsEnabled = true,
            TypeEnum = IndicatorType.SMA,
            ParameterObject = new CoreSmaParameter { Period = 5 },
            DynamicPeriodIndicatorId = "HT1"
        };

        var consumerB = new CoreIndicatorSettings
        {
            Id = "ConsumerB",
            IsEnabled = true,
            TypeEnum = IndicatorType.EMA,
            ParameterObject = new CoreSmaParameter { Period = 10 },
            DynamicPeriodIndicatorId = "HT1"
        };

        var settings = new List<CoreIndicatorSettings> { consumerA, consumerB, htSetting };

        var result = service.CalculateIndicators(candles, settings);

        Assert.True(result["HT1"].IsSuccessful);
        Assert.True(result["ConsumerA"].IsSuccessful);
        Assert.True(result["ConsumerB"].IsSuccessful);
        Assert.Equal(60, result["ConsumerA"].MainValues.Count);
        Assert.Equal(60, result["ConsumerB"].MainValues.Count);
    }

    [Fact]
    public void CalculateIndicators_DuplicateSettingsId_DoesNotThrow()
    {
        // Regression test: the dynamic-period-driver lookup dictionary built inside CalculateIndicators
        // must tolerate duplicate CoreIndicatorSettings.Id values the same way SortSettingsByDependency's
        // own id-keyed map does (last one wins), instead of throwing ArgumentException like a raw
        // Enumerable.ToDictionary() call would.
        var service = new AnalysisPipelineService(
            pythonService: null,
            indicatorFactory: IndicatorFactory.Default);

        var candles = CreateTestCandles(40);

        var duplicateA = CreateSetting("DUPLICATE_ID", IndicatorType.SMA);
        var duplicateB = CreateSetting("DUPLICATE_ID", IndicatorType.SMA);

        var driven = new CoreIndicatorSettings
        {
            Id = "Driven",
            IsEnabled = true,
            TypeEnum = IndicatorType.SMA,
            ParameterObject = new CoreSmaParameter { Period = 5 },
            DynamicPeriodIndicatorId = "DUPLICATE_ID"
        };

        var settings = new List<CoreIndicatorSettings> { duplicateA, duplicateB, driven };

        var exception = Record.Exception(() => service.CalculateIndicators(candles, settings));

        Assert.Null(exception);
    }
}
