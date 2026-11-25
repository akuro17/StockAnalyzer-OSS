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
/// Indicator wiring, dynamic R-9 <c>MinBars</c>, parameter hierarchy (defaults/ranges/JSON polymorphism),
/// and spec constants for the 18 V2-8 candlestick flags (<c>sa_pattern_literal_ledger_V2-8_CandlesPart1.md</c>, rows <c>CI_*</c>).
/// </summary>
public class CandlestickPart1IndicatorTests
{
    private static readonly IndicatorFactory Factory = new();
    private static readonly JsonSerializerOptions WorkspaceOptions = new() { TypeInfoResolver = WorkspacePolymorphicResolver.CreateResolver() };

    public static IEnumerable<object[]> Part1Types()
        => CandlePatternFlagTable.Entries
            .Where(e => e.Length <= 2 && CandlestickPatternEngine.Supports(e.Type))
            .Select(e => new object[] { e.Type });

    private static CoreCandleData[] ToCore(IEnumerable<CandleData> bars)
        => bars.Select(b => new CoreCandleData(b.Timestamp, b.Open, b.High, b.Low, b.Close, b.Volume)).ToArray();

    [Fact]
    public void CI_Part1Count_Is18_With9OneBarAnd9TwoBar()
    {
        var entries = CandlePatternFlagTable.Entries.Where(e => e.Length <= 2 && CandlestickPatternEngine.Supports(e.Type)).ToArray();
        Assert.Equal(18, entries.Length);
        Assert.Equal(9, entries.Count(e => e.Length == 1));
        Assert.Equal(9, entries.Count(e => e.Length == 2));
    }

    [Theory]
    [MemberData(nameof(Part1Types))]
    public void CI_Candle_MinBars_DefaultAndDynamic_R9(IndicatorType type)
    {
        PatternFlagSpec spec = PatternFlagIndicatorCatalog.Get(type);
        CandlePatternFlagEntry entry = CandlePatternFlagTable.Get(type);
        int expectedMin = 20 + entry.Length;
        Assert.Equal(expectedMin, spec.MinBars);
        Assert.Equal(PatternFlagEvaluationModel.FullSeries, spec.Model);

        CoreCandleData[] walk = ToCore(PatternEngines.PatternEngineTestBars.Walk(2, 40));
        IIndicatorResult defRes = Factory.Create(type)!.Calculate(walk);
        Assert.True(defRes.IsSuccessful, defRes.ErrorMessage);

        IReadOnlyList<decimal?> main = defRes.GetSeries(IndicatorResult.MainSeriesName);
        for (int t = 0; t < expectedMin - 1; t++)
        {
            Assert.Null(main[t]);
        }

        Assert.NotNull(main[expectedMin - 1]);

        // Plan R-9: increasing AtrPeriod beyond 19 widens MinBars dynamically (e.g., AtrPeriod=25 -> MinBars = 26 + k).
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
    public void CI_NoExcludePatternBarsProperty_ContextAlwaysBeforePattern()
    {
        Assert.Null(typeof(CoreCandlePatternParameter).GetProperty("ExcludePatternBarsFromContext"));
        Assert.Null(typeof(CoreUmbrellaPatternParameter).GetProperty("RequireTrendContext"));
    }

    [Fact]
    public void CI_ParameterClasses_DefaultsRangesJson()
    {
        CoreIndicatorParameterBase baseParam = new CoreCandlePatternParameter { AtrPeriod = 20, RequireVolumeConfirmation = true, VolumeMultiplier = 1.35m };
        CoreIndicatorParameterBase umbParam = new CoreUmbrellaPatternParameter { LongShadowBodyMultiplier = 2.5m, OppositeShadowBodyMaxRatio = 0.40m, StrictColorMatch = true };
        CoreIndicatorParameterBase gapParam = new CoreGapCandlePatternParameter { GapMinBodyRatio = 0.25m, RequireWickToWickGap = true };
        CoreIndicatorParameterBase multiParam = new CoreMultiBarCandlePatternParameter { NoiseBodyMinRatio = 0.60m, MaxUpperOrLowerShadowRatio = 0.25m };

        foreach (CoreIndicatorParameterBase p in new[] { baseParam, umbParam, gapParam, multiParam })
        {
            p.Validate();
            Assert.Equal(PatternFlagParameterLimits.WarmupBars, p.GetRequiredWarmupBars());

            string json = JsonSerializer.Serialize(p, WorkspaceOptions);
            Assert.Contains($"\"$type\":\"{p.GetType().Name}\"", json);
            CoreIndicatorParameterBase? back = JsonSerializer.Deserialize<CoreIndicatorParameterBase>(json, WorkspaceOptions);
            Assert.IsType(p.GetType(), back);
        }

        // Validation out-of-range throws.
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreCandlePatternParameter { AtrPeriod = 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreCandlePatternParameter { PriceZoneThreshold = 0.60m }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreUmbrellaPatternParameter { LongShadowBodyMultiplier = 0.9m }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreGapCandlePatternParameter { GapMinBodyRatio = 1.1m }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreMultiBarCandlePatternParameter { NoiseBodyMinRatio = 0.05m }.Validate());
    }

    [Fact]
    public void CI_Constants_MatchSpecifications()
    {
        Assert.Equal(20, PatternSpecConstants.CandleMinBaseBars);
        Assert.Equal(20, PatternSpecConstants.CandleContinuationEmaSlowPeriod);
        Assert.Equal(0.60m, PatternSpecConstants.CandleContinuationBullZoneMin);
        Assert.Equal(0.40m, PatternSpecConstants.CandleContinuationBearZoneMax);
        Assert.Equal(0.80m, PatternSpecConstants.CandleDojiHighZoneMin);
        Assert.Equal(0.20m, PatternSpecConstants.CandleDojiLowZoneMax);
        Assert.Equal(60, PatternSpecConstants.CandleLongZoneBars);
        Assert.Equal(0.25m, PatternSpecConstants.CandleLongZoneLowMax);
        Assert.Equal(0.80m, PatternSpecConstants.CandleMarubozuAtrRatio);
        Assert.Equal(0.90m, PatternSpecConstants.CandleMarubozuBodyRangeMin);
        Assert.Equal(0.50m, PatternSpecConstants.CandleDojiNormalMinRangeAtr);
        Assert.Equal(0.40m, PatternSpecConstants.CandleUmbrellaMaxBodyRangeRatio);
        Assert.Equal(0.80m, PatternSpecConstants.CandleUmbrellaMinRangeAtr);
        Assert.Equal(0.10m, PatternSpecConstants.CandleUmbrellaMaxOppShadowRangeRatio);
        Assert.Equal(0.15m, PatternSpecConstants.CandleCrossDojiMinShadowRatio);
        Assert.Equal(0.80m, PatternSpecConstants.CandleShadowDojiMinRangeAtr);
        Assert.Equal(0.70m, PatternSpecConstants.CandleShadowDojiMinLongShadowRatio);
        Assert.Equal(0.30m, PatternSpecConstants.CandleEngulfingFirstMinBodyAvgRatio);
        Assert.Equal(1.00m, PatternSpecConstants.CandleEngulfingSecondMinBodyAvgRatio);
        Assert.Equal(1.20m, PatternSpecConstants.CandleEngulfingVolumeRatio);
        Assert.Equal(1.10m, PatternSpecConstants.CandleBullishEngulfingAvgVolumeMultiplier);
        Assert.Equal(1.15m, PatternSpecConstants.CandleHammerPiercingAvgVolumeMultiplier);
        Assert.Equal(0.50m, PatternSpecConstants.CandleHaramiMaxInnerBodyRatio);
        Assert.Equal(0.20m, PatternSpecConstants.CandleKickingMinGapAvgRatio);
        Assert.Equal(0.50m, PatternSpecConstants.CandleKickingMinGapAtrRatio);
        Assert.Equal(1.30m, PatternSpecConstants.CandleKickingAvgVolumeMultiplier);
    }

    [Theory]
    [MemberData(nameof(Part1Types))]
    public void CI_IndicatorWiring_All18TypesCalculateViaFactory(IndicatorType type)
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
}
