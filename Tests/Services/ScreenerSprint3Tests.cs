using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Tests.Services;

public class ScreenerSprint3Tests
{
    [Fact]
    public void DefaultCoreIndicatorSettings_ContainsExplicitEntriesForSprint3Indicators()
    {
        var defaults = DefaultCoreIndicatorSettings.GetDefault();

        var pivotAdv = defaults.FirstOrDefault(s => s.TypeEnum == IndicatorType.PivotPointsAdvanced);
        Assert.NotNull(pivotAdv);
        Assert.True(pivotAdv.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Chart, pivotAdv.Category);

        var stdDev = defaults.FirstOrDefault(s => s.TypeEnum == IndicatorType.StandardDeviation);
        Assert.NotNull(stdDev);
        Assert.False(stdDev.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Volatility, stdDev.Category);
        Assert.IsType<CoreStandardDeviationParameter>(stdDev.ParameterObject);

        var massIndex = defaults.FirstOrDefault(s => s.TypeEnum == IndicatorType.MassIndex);
        Assert.NotNull(massIndex);
        Assert.False(massIndex.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Volatility, massIndex.Category);
        Assert.IsType<CoreMassIndexParameter>(massIndex.ParameterObject);

        var tema = defaults.FirstOrDefault(s => s.TypeEnum == IndicatorType.TEMA);
        Assert.NotNull(tema);
        Assert.True(tema.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Trend, tema.Category);

        var roc = defaults.FirstOrDefault(s => s.TypeEnum == IndicatorType.ROC);
        Assert.NotNull(roc);
        Assert.False(roc.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Oscillator, roc.Category);
    }

    [Theory]
    [InlineData(IndicatorType.Ichimoku, 78)]
    [InlineData(IndicatorType.MassIndex, 43)]
    [InlineData(IndicatorType.SchaffTrendCycle, 70)]
    public void ScreenerHistoryRequirements_ResolveIndicatorWarmupBars_DefaultParameters(IndicatorType type, int expectedBars)
    {
        int warmup = ScreenerHistoryRequirements.ResolveIndicatorWarmupBars(type, null);
        Assert.Equal(expectedBars, warmup);
    }

    [Fact]
    public void ScreenerHistoryRequirements_ResolveIndicatorWarmupBars_CustomParameters()
    {
        // Custom Ichimoku: Kijun=30, SenkouB=60 => 60 + 30 = 90
        var ichimokuParams = new Dictionary<string, object>
        {
            { "KijunPeriod", 30 },
            { "SenkouSpanBPeriod", 60 }
        };
        int ichimokuWarmup = ScreenerHistoryRequirements.ResolveIndicatorWarmupBars(IndicatorType.Ichimoku, ichimokuParams);
        Assert.Equal(90, ichimokuWarmup);

        // Custom MassIndex: EmaPeriod=10, SumPeriod=30 => 2 * 10 + 30 = 50
        var massIndexParams = new Dictionary<string, object>
        {
            { "EmaPeriod", 10 },
            { "SumPeriod", 30 }
        };
        int massIndexWarmup = ScreenerHistoryRequirements.ResolveIndicatorWarmupBars(IndicatorType.MassIndex, massIndexParams);
        Assert.Equal(50, massIndexWarmup);

        // Custom SchaffTrendCycle: FastPeriod=20, SlowPeriod=40, CyclePeriod=15 => 40 + 2 * 15 = 70
        var stcParams = new Dictionary<string, object>
        {
            { "FastPeriod", 20 },
            { "SlowPeriod", 40 },
            { "CyclePeriod", 15 }
        };
        int stcWarmup = ScreenerHistoryRequirements.ResolveIndicatorWarmupBars(IndicatorType.SchaffTrendCycle, stcParams);
        Assert.Equal(70, stcWarmup);
    }

    [Fact]
    public void ScreenerHistoryRequirements_GetMinimumBars_ComputesCorrectHistoryLength()
    {
        // Ichimoku default warmup is 78.
        // GetMinimumBars = 78 * 5 + 0 + 100 = 390 + 100 = 490 bars.
        int minBars = ScreenerHistoryRequirements.GetMinimumBars(IndicatorType.Ichimoku, null, 0);
        Assert.Equal(490, minBars);

        // With offset = 10: 490 + 10 = 500 bars.
        int minBarsWithOffset = ScreenerHistoryRequirements.GetMinimumBars(IndicatorType.Ichimoku, null, 10);
        Assert.Equal(500, minBarsWithOffset);
    }

    [Fact]
    public void IndicatorRegistrationViewModel_BuildParameterDictionary_ExtractsCorrectly()
    {
        var param = new CoreMassIndexParameter { Period = 30, EmaPeriod = 12 };
        var dict = IndicatorRegistrationViewModel.BuildParameterDictionary(param);

        Assert.Equal(30, dict["Period"]);
        Assert.Equal(12, dict["EmaPeriod"]);

        var defaultSmaDict = IndicatorRegistrationViewModel.BuildParameterDictionary(IndicatorType.SMA);
        Assert.True(defaultSmaDict.ContainsKey("Period"));
        Assert.Equal(20, defaultSmaDict["Period"]);
    }

    [Fact]
    public void ScreenerHistoryRequirements_ResolveIndicatorWarmupBars_WithSerializedParameterObjects()
    {
        // 1. Ichimoku via CoreIchimokuParameter
        var ichimokuParam = new CoreIchimokuParameter { KijunSample = 30, SenkouBSample = 60, Offset = 25 };
        var ichimokuDict = IndicatorRegistrationViewModel.BuildParameterDictionary(ichimokuParam);
        int ichimokuWarmup = ScreenerHistoryRequirements.ResolveIndicatorWarmupBars(IndicatorType.Ichimoku, ichimokuDict);
        Assert.Equal(60 + 25, ichimokuWarmup); // Math.Max(60, 30) + 25 = 85

        // 2. MassIndex via CoreMassIndexParameter
        var massIndexParam = new CoreMassIndexParameter { EmaPeriod = 12, Period = 35 };
        var massIndexDict = IndicatorRegistrationViewModel.BuildParameterDictionary(massIndexParam);
        int massIndexWarmup = ScreenerHistoryRequirements.ResolveIndicatorWarmupBars(IndicatorType.MassIndex, massIndexDict);
        Assert.Equal(2 * 12 + 35, massIndexWarmup); // 59

        // 3. SchaffTrendCycle via CoreSchaffTrendCycleParameter
        var stcParam = new CoreSchaffTrendCycleParameter { CyclePeriod = 15, ShortPeriod = 20, LongPeriod = 60 };
        var stcDict = IndicatorRegistrationViewModel.BuildParameterDictionary(stcParam);
        int stcWarmup = ScreenerHistoryRequirements.ResolveIndicatorWarmupBars(IndicatorType.SchaffTrendCycle, stcDict);
        Assert.Equal(60 + 2 * 15, stcWarmup); // 90

        // 4. MACD via CoreMacdParameter
        var macdParam = new CoreMacdParameter { ShortPeriod = 10, LongPeriod = 30, SignalPeriod = 12 };
        var macdDict = IndicatorRegistrationViewModel.BuildParameterDictionary(macdParam);
        int macdWarmup = ScreenerHistoryRequirements.ResolveIndicatorWarmupBars(IndicatorType.MACD, macdDict);
        Assert.Equal(30 + 12, macdWarmup); // 42

        // 5. Stochastic via CoreStochasticParameter
        var stochParam = new CoreStochasticParameter { KPeriod = 21, Smooth = 5, DPeriod = 4 };
        var stochDict = IndicatorRegistrationViewModel.BuildParameterDictionary(stochParam);
        int stochWarmup = ScreenerHistoryRequirements.ResolveIndicatorWarmupBars(IndicatorType.Stoch, stochDict);
        Assert.Equal(21 + 5 + 4, stochWarmup); // 30
    }

    [Fact]
    public void ScreenerHistoryRequirements_DefaultBranch_FloorOfTwentyIsPreserved()
    {
        // When an indicator has Period = 10 (< 20), warmup bars must remain at floor (20) per API_SPECIFICATION.md
        var smallParams = new Dictionary<string, object> { { "Period", 10 } };
        int warmup = ScreenerHistoryRequirements.ResolveIndicatorWarmupBars(IndicatorType.SMA, smallParams);
        Assert.Equal(20, warmup);

        // When Period = 50 (> 20), warmup bars must reflect 50
        var largeParams = new Dictionary<string, object> { { "Period", 50 } };
        int warmupLarge = ScreenerHistoryRequirements.ResolveIndicatorWarmupBars(IndicatorType.SMA, largeParams);
        Assert.Equal(50, warmupLarge);
    }

    [Fact]
    public void PivotPointsAdvanced_RegisteredInFactory_AndCalculatesOverlay()
    {
        var indicator = IndicatorFactory.Default.Create(IndicatorType.PivotPointsAdvanced);
        Assert.NotNull(indicator);
        Assert.True(((CoreIndicatorBase)indicator).IsOverlay);

        var baseDate = System.DateTime.UtcNow.Date;
        var candles = new List<CoreCandleData>
        {
            new(baseDate, 100m, 105m, 95m, 102m, 1000),
            new(baseDate.AddDays(1), 102m, 108m, 101m, 107m, 1500),
            new(baseDate.AddDays(2), 107m, 110m, 104m, 105m, 1200)
        };

        var result = indicator.Calculate(candles);
        Assert.True(result.IsSuccessful);
        Assert.Equal(3, indicator.Values.Count);

        // Re-calculation on same instance must clear previous values
        result = indicator.Calculate(candles);
        Assert.True(result.IsSuccessful);
        Assert.Equal(3, indicator.Values.Count);
    }

    [Fact]
    public void SsaAnomalyAndSnr_ProduceConstantSeriesNames_BothSmallAndNormalCandles()
    {
        var ssa = IndicatorFactory.Default.Create(IndicatorType.SSAAnomaly);
        Assert.NotNull(ssa);

        var snr = IndicatorFactory.Default.Create(IndicatorType.SSASNR);
        Assert.NotNull(snr);

        var baseDate = System.DateTime.UtcNow.Date;
        var shortCandles = new List<CoreCandleData> { new(baseDate, 100m, 105m, 95m, 102m, 1000) };

        var ssaShort = ssa.Calculate(shortCandles);
        Assert.True(ssaShort.HasSeries("EnterThreshold"));
        Assert.True(ssaShort.HasSeries("ExitThreshold"));
        Assert.True(ssaShort.HasSeries("AnomalyState"));

        var snrShort = snr.Calculate(shortCandles);
        Assert.True(snrShort.HasSeries("ThresholdHigh"));
        Assert.True(snrShort.HasSeries("ThresholdLow"));
        Assert.True(snrShort.HasSeries("SignalPurity"));

        // Normal candles (50 bars)
        var normalCandles = new List<CoreCandleData>();
        for (int i = 0; i < 50; i++)
        {
            normalCandles.Add(new(baseDate.AddDays(i), 100m + i, 105m + i, 95m + i, 102m + i, 1000));
        }

        var ssaNormal = ssa.Calculate(normalCandles);
        Assert.True(ssaNormal.HasSeries("EnterThreshold"));
        Assert.True(ssaNormal.HasSeries("ExitThreshold"));
        Assert.True(ssaNormal.HasSeries("AnomalyState"));

        var snrNormal = snr.Calculate(normalCandles);
        Assert.True(snrNormal.HasSeries("ThresholdHigh"));
        Assert.True(snrNormal.HasSeries("ThresholdLow"));
        Assert.True(snrNormal.HasSeries("SignalPurity"));
    }

    [Fact]
    public void ThreeLineBreakSignal_FlatPrices_ProducesHistogramSeries()
    {
        var tlb = IndicatorFactory.Default.Create(IndicatorType.ThreeLineBreakSignal);
        Assert.NotNull(tlb);

        var baseDate = System.DateTime.UtcNow.Date;
        var flatCandles = new List<CoreCandleData>();
        for (int i = 0; i < 10; i++)
        {
            flatCandles.Add(new(baseDate.AddDays(i), 100m, 100m, 100m, 100m, 1000));
        }

        var result = tlb.Calculate(flatCandles);
        Assert.True(result.IsSuccessful);
        Assert.True(result.HasSeries("Histogram"));
        Assert.Equal(10, result.GetSeries("Histogram").Count);
    }
}
