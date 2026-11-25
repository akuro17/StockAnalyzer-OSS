using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.PatternFlags;
using StockAnalyzer.Core.Models.Indicators.PatternFlags.Candles;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Serialization;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators.PatternFlag;

/// <summary>
/// Indicator wiring, R-9 dynamic <c>MinBars</c>, full-56 <c>FullSeries</c> retirement of <c>CandleWindowView</c>,
/// <see cref="CoreStructureTrendParameter"/> validation/JSON round-trip, and Part 3 spec constants
/// (<c>sa_pattern_literal_ledger_V2-10_CandlesPart3.md</c>, rows <c>CI3_*</c>).
/// </summary>
public class CandlestickPart3AndHhHlIndicatorTests
{
    private static readonly IndicatorFactory Factory = new();
    private static readonly JsonSerializerOptions WorkspaceOptions = new() { TypeInfoResolver = WorkspacePolymorphicResolver.CreateResolver() };

    public static IEnumerable<object[]> Part3CandleTypes()
        => CandlePatternFlagTable.Entries
            .Where(e => e.Length >= 4)
            .Select(e => new object[] { e.Type });

    private static CoreCandleData[] ToCore(IEnumerable<CandleData> bars)
        => bars.Select(b => new CoreCandleData(b.Timestamp, b.Open, b.High, b.Low, b.Close, b.Volume)).ToArray();

    [Fact]
    public void CI3_All56FlagIndicators_AreFullSeries_AndAll43CandlesSupported()
    {
        Assert.Equal(57, PatternFlagIndicatorCatalog.Rows.Count);
        Assert.All(PatternFlagIndicatorCatalog.Rows, r => Assert.Equal(PatternFlagEvaluationModel.FullSeries, r.Model));

        Assert.Equal(43, CandlePatternFlagTable.Entries.Count);
        Assert.Equal(9, CandlePatternFlagTable.Entries.Count(e => e.Length >= 4));
        Assert.All(CandlePatternFlagTable.Entries, e => Assert.True(CandlestickPatternEngine.Supports(e.Type)));

        PatternFlagSpec hhSpec = PatternFlagIndicatorCatalog.Get(IndicatorType.MarketStructureHhHl);
        Assert.Equal(20, hhSpec.MinBars);
        Assert.False(hhSpec.HasDirection);
        Assert.Equal(typeof(CoreStructureTrendParameter), hhSpec.ParameterClass);

        PatternFlagSpec llSpec = PatternFlagIndicatorCatalog.Get(IndicatorType.MarketStructureLlLh);
        Assert.Equal(20, llSpec.MinBars);
        Assert.False(llSpec.HasDirection);
        Assert.Equal(typeof(CoreStructureTrendParameter), llSpec.ParameterClass);
    }

    [Theory]
    [MemberData(nameof(Part3CandleTypes))]
    public void CI3_Part3Candle_MinBars_24Or25_And_Dynamic_R9(IndicatorType type)
    {
        CandlePatternFlagEntry entry = CandlePatternFlagTable.Get(type);
        PatternFlagSpec spec = PatternFlagIndicatorCatalog.Get(type);
        int expectedMin = 20 + entry.Length;
        Assert.Equal(expectedMin, spec.MinBars);

        CoreCandleData[] walk = ToCore(PatternEngines.PatternEngineTestBars.Walk(2, 40));
        IIndicatorResult defRes = Factory.Create(type)!.Calculate(walk);
        Assert.True(defRes.IsSuccessful, defRes.ErrorMessage);
        IReadOnlyList<decimal?> main = defRes.GetSeries(IndicatorResult.MainSeriesName);
        for (int t = 0; t < expectedMin - 1; t++)
        {
            Assert.Null(main[t]);
        }

        Assert.NotNull(main[expectedMin - 1]);

        CoreCandlePatternParameter wide = CoreCandlePatternParameter.CreateFor(type);
        wide.AtrPeriod = 25;
        int wideMin = 26 + entry.Length;
        IIndicatorResult wideRes = Factory.Create(type, wide)!.Calculate(walk);
        Assert.True(wideRes.IsSuccessful, wideRes.ErrorMessage);
        IReadOnlyList<decimal?> wideMain = wideRes.GetSeries(IndicatorResult.MainSeriesName);
        for (int t = 0; t < wideMin - 1; t++)
        {
            Assert.Null(wideMain[t]);
        }

        Assert.NotNull(wideMain[wideMin - 1]);
    }

    [Fact]
    public void CI3_Constants_MatchPart3AndStructureTrendSpecs()
    {
        Assert.Equal(1.00m, PatternSpecConstants.CandleThreeMethodsFifthMinBodyAvgRatio);
        Assert.Equal(0.10m, PatternSpecConstants.CandleMatHoldSecondOpenGapAvgRatio);
        Assert.Equal(0.10m, PatternSpecConstants.CandleThreeLineStrikeOpenSlackAvgRatio);
        Assert.Equal(1.50m, PatternSpecConstants.CandleThreeLineStrikeFourthMinBodyAvgRatio);
        Assert.Equal(1.20m, PatternSpecConstants.CandleBreakawayFifthMinBodyAvgRatio);
        Assert.Equal(0.20m, PatternSpecConstants.CandleConcealingBabyThirdMinBodyAvgRatio);
        Assert.Equal(0.10m, PatternSpecConstants.CandleConcealingBabyThirdMaxLowerShadowRangeRatio);
        Assert.Equal(0.80m, PatternSpecConstants.CandleConcealingBabyThirdMinUpperShadowBodyRatio);

        Assert.Equal(20, PatternSpecConstants.StructureTrendMinBars);
        Assert.Equal(0, PatternSpecConstants.StructureTrendFractalBars);
        Assert.Equal(10, PatternSpecConstants.StructureTrendMinSpanBars);
        Assert.Equal(120, PatternSpecConstants.StructureTrendMaxSpanBars);
        Assert.Equal(2, PatternSpecConstants.StructureTrendSpanIntervalMultiplier);
        Assert.Equal(2, PatternSpecConstants.StructureTrendMinSpanOffsetBars);
        Assert.Equal(20, PatternSpecConstants.StructureTrendWarmupMarginBars);
        Assert.Equal(0.50m, PatternSpecConstants.StructureTrendStepAtrMultiplier);
    }

    [Fact]
    public void CI3_StructureTrend_ParameterHierarchyAndValidate()
    {
        var def = new CoreStructureTrendParameter();
        Assert.Equal(3.0m, def.ZigZagThresholdPercent);
        Assert.Equal(1.5m, def.MinSwingAtrMultiplier);
        Assert.Equal(0.5m, def.MinStepUpPercent);
        Assert.Equal(4, def.MinPivotIntervalBars);
        Assert.Equal(60, def.MaxPivotIntervalBars);
        Assert.Equal(40, def.MaxLastPivotAgeBars);
        Assert.True(def.RequireCloseAboveLastLow);
        Assert.Equal(500, def.GetRequiredWarmupBars());
        Assert.Equal(620, new CoreStructureTrendParameter { MaxPivotIntervalBars = 200, MaxLastPivotAgeBars = 200 }.GetRequiredWarmupBars());
        Assert.Equal(630, StockAnalyzer.Core.Services.ScreenerHistoryRequirements.GetMinimumBars(
            IndicatorType.MarketStructureHhHl,
            new Dictionary<string, object> { ["MaxPivotIntervalBars"] = 200, ["MaxLastPivotAgeBars"] = 200 },
            offset: 10));
        def.Validate();

        var custom = new CoreStructureTrendParameter
        {
            ZigZagThresholdPercent = 4.5m,
            MinSwingAtrMultiplier = 2.0m,
            MinStepUpPercent = 1.0m,
            MinPivotIntervalBars = 6,
            MaxPivotIntervalBars = 80,
            MaxLastPivotAgeBars = 55,
            RequireCloseAboveLastLow = false,
        };
        custom.Validate();
        string json = JsonSerializer.Serialize<CoreIndicatorParameterBase>(custom, WorkspaceOptions);
        Assert.Contains("\"$type\":\"CoreStructureTrendParameter\"", json);
        var roundTripped = Assert.IsType<CoreStructureTrendParameter>(JsonSerializer.Deserialize<CoreIndicatorParameterBase>(json, WorkspaceOptions));
        Assert.Equal(JsonSerializer.Serialize<CoreIndicatorParameterBase>(custom, WorkspaceOptions), JsonSerializer.Serialize<CoreIndicatorParameterBase>(roundTripped, WorkspaceOptions));

        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreStructureTrendParameter { MinSwingAtrMultiplier = -0.1m }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreStructureTrendParameter { MinStepUpPercent = 10.1m }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreStructureTrendParameter { MinPivotIntervalBars = 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreStructureTrendParameter { MaxPivotIntervalBars = 201 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreStructureTrendParameter { MaxLastPivotAgeBars = 4 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreStructureTrendParameter { MinPivotIntervalBars = 16, MaxPivotIntervalBars = 15 }.Validate());
    }

    [Theory]
    [MemberData(nameof(Part3CandleTypes))]
    public void CI3_IndicatorWiring_All9Part3TypesCalculateViaFactory(IndicatorType type)
    {
        Assert.Equal(ChannelNormalization.None, FeatureChannelNormalizationDefaults.ForIndicator(type));
        CandleData[] walk = PatternEngines.PatternEngineTestBars.Walk(3, 80);
        CoreIndicatorSettings defSettings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == type);
        CoreCandlePatternParameter expectedParam = CoreCandlePatternParameter.CreateFor(type);
        Assert.IsType(expectedParam.GetType(), defSettings.ParameterObject);

        IIndicatorResult res = Factory.Create(type)!.Calculate(ToCore(walk));
        Assert.True(res.IsSuccessful, res.ErrorMessage);
        Assert.Equal(new[] { IndicatorResult.MainSeriesName }, res.SeriesNamesList);

        PatternBarResult[] engine = CandlestickPatternEngine.Run(PatternEngines.PatternEngineTestBars.Context(walk), type, expectedParam);
        IReadOnlyList<decimal?> main = res.GetSeries(IndicatorResult.MainSeriesName);
        int minBars = expectedParam.ComputeMinBars(CandlePatternFlagTable.Get(type).Length);
        for (int t = minBars - 1; t < walk.Length; t++)
        {
            Assert.Equal(engine[t].BullishFires ? 1m : 0m, main[t]);
        }
    }

    [Fact]
    public void CI3_IndicatorWiring_HhHlAndLlLhCalculateViaFactory_MatchEngine_AndAreMutuallyExclusive()
    {
        Assert.Equal(ChannelNormalization.None, FeatureChannelNormalizationDefaults.ForIndicator(IndicatorType.MarketStructureHhHl));
        Assert.Equal(ChannelNormalization.None, FeatureChannelNormalizationDefaults.ForIndicator(IndicatorType.MarketStructureLlLh));
        CandleData[] walk = PatternEngines.PatternEngineTestBars.Walk(12, 400);
        CoreCandleData[] coreWalk = ToCore(walk);
        var param = new CoreStructureTrendParameter { ZigZagThresholdPercent = 1.5m, MinSwingAtrMultiplier = 0m };
        IIndicatorResult hhRes = Factory.Create(IndicatorType.MarketStructureHhHl, param)!.Calculate(coreWalk);
        IIndicatorResult llRes = Factory.Create(IndicatorType.MarketStructureLlLh, param)!.Calculate(coreWalk);
        Assert.True(hhRes.IsSuccessful, hhRes.ErrorMessage);
        Assert.True(llRes.IsSuccessful, llRes.ErrorMessage);
        Assert.Equal(new[] { IndicatorResult.MainSeriesName }, hhRes.SeriesNamesList);
        Assert.Equal(new[] { IndicatorResult.MainSeriesName }, llRes.SeriesNamesList);

        PatternBarResult[] hhEngine = HigherHighHigherLowEngine.Run(PatternEngines.PatternEngineTestBars.Context(walk), param, side: 1);
        PatternBarResult[] llEngine = HigherHighHigherLowEngine.Run(PatternEngines.PatternEngineTestBars.Context(walk), param, side: -1);
        IReadOnlyList<decimal?> hhMain = hhRes.GetSeries(IndicatorResult.MainSeriesName);
        IReadOnlyList<decimal?> llMain = llRes.GetSeries(IndicatorResult.MainSeriesName);

        Assert.Contains(hhMain, v => v == 1m);
        Assert.Contains(llMain, v => v == 1m);
        for (int t = 19; t < walk.Length; t++)
        {
            Assert.Equal(hhEngine[t].Fires ? 1m : 0m, hhMain[t]);
            Assert.Equal(llEngine[t].Fires ? 1m : 0m, llMain[t]);
            Assert.False(hhMain[t] == 1m && llMain[t] == 1m, $"MarketStructureHhHl and MarketStructureLlLh must never both output 1m on bar {t}");
        }
    }
}
