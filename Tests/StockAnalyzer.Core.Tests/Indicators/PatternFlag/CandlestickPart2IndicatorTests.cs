using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.PatternFlags;
using StockAnalyzer.Core.Models.Indicators.PatternFlags.Candles;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators.PatternFlag;

/// <summary>
/// Indicator wiring, dynamic R-9 <c>MinBars = 23</c>, parameter defaults (<c>CandleBullishAbandonedBaby Vol=1.25m</c>),
/// and spec constants for the 16 V2-9 three-bar candlestick flags (<c>sa_pattern_literal_ledger_V2-9_CandlesPart2.md</c>, rows <c>CI2_*</c>).
/// </summary>
public class CandlestickPart2IndicatorTests
{
    private static readonly IndicatorFactory Factory = new();

    public static IEnumerable<object[]> Part2Types()
        => CandlePatternFlagTable.Entries
            .Where(e => e.Length == 3)
            .Select(e => new object[] { e.Type });

    private static CoreCandleData[] ToCore(IEnumerable<CandleData> bars)
        => bars.Select(b => new CoreCandleData(b.Timestamp, b.Open, b.High, b.Low, b.Close, b.Volume)).ToArray();

    [Fact]
    public void CI2_Part2Count_Is16_AndTotalThroughPart2Is34()
    {
        var throughPart2 = CandlePatternFlagTable.Entries.Where(e => e.Length <= 3 && CandlestickPatternEngine.Supports(e.Type)).ToArray();
        Assert.Equal(34, throughPart2.Length);
        var threeBar = CandlePatternFlagTable.Entries.Where(e => e.Length == 3).ToArray();
        Assert.Equal(16, threeBar.Length);
        Assert.All(threeBar, e => Assert.True(CandlestickPatternEngine.Supports(e.Type)));
    }

    [Theory]
    [MemberData(nameof(Part2Types))]
    public void CI2_Candle_MinBars_23_And_Dynamic_R9(IndicatorType type)
    {
        PatternFlagSpec spec = PatternFlagIndicatorCatalog.Get(type);
        CoreCandlePatternParameter defParam = CoreCandlePatternParameter.CreateFor(type);
        int expectedMin = defParam.ComputeMinBars(3);
        Assert.Equal(type == IndicatorType.CandleThreeWhiteSoldiers ? 63 : 23, expectedMin);
        Assert.Equal(expectedMin, spec.MinBars);
        Assert.Equal(PatternFlagEvaluationModel.FullSeries, spec.Model);

        CoreCandleData[] walk = ToCore(PatternEngines.PatternEngineTestBars.Walk(2, expectedMin + 15));
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
        int wideMin = wide.ComputeMinBars(3); // 29 for standard 3-bar patterns; 63 for ThreeWhiteSoldiers (PriceZoneLookbackBars=60 dominates)
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
    public void CI2_Constants_MatchPart2Specifications()
    {
        Assert.Equal(1.20m, PatternSpecConstants.CandleStarThirdMinBodyAvgRatio);
        Assert.Equal(0.70m, PatternSpecConstants.CandleThreeSoldiersMinBodyAvgRatio);
        Assert.Equal(0.55m, PatternSpecConstants.CandleThreeSoldiersMinBodyRange);
        Assert.Equal(0.80m, PatternSpecConstants.CandleGapPatternFirstMinBodyAvgRatio);
        Assert.Equal(0.30m, PatternSpecConstants.CandleSideBySideOpenTolRatio);
        Assert.Equal(0.30m, PatternSpecConstants.CandleSideBySideBodyTolRatio);
        Assert.Equal(1.25m, PatternSpecConstants.CandleAbandonedBabyAvgVolumeMultiplier);
        Assert.Equal(0.85m, PatternSpecConstants.CandleAdvanceBlockMaxThirdBodyRatio);
        Assert.Equal(0.50m, PatternSpecConstants.CandleAdvanceBlockMinUpperShadowBodyRatio);
        Assert.Equal(0.40m, PatternSpecConstants.CandleDeliberationMaxThirdBodyRatio);
        Assert.Equal(0.15m, PatternSpecConstants.CandleDeliberationThirdOpenSlackAvgRatio);
        Assert.Equal(0.10m, PatternSpecConstants.CandleStickSandwichThirdOpenSlackAvgRatio);
        Assert.Equal(0.05m, PatternSpecConstants.CandleEqualPriceAvgBodyRatio);
        Assert.Equal(0.002m, PatternSpecConstants.CandleEqualPriceFraction);
        Assert.Equal(0.20m, PatternSpecConstants.CandleGapReentryOpenSlackAvgRatio);
        Assert.Equal(0.15m, PatternSpecConstants.CandleIdenticalCrowsMaxLowerShadowBodyRatio);

        var bullBaby = (CoreGapCandlePatternParameter)CoreCandlePatternParameter.CreateFor(IndicatorType.CandleBullishAbandonedBaby);
        Assert.True(bullBaby.RequireWickToWickGap);
        Assert.Equal(1.25m, bullBaby.VolumeMultiplier);
        Assert.False(bullBaby.RequireVolumeConfirmation);

        var bearBaby = (CoreGapCandlePatternParameter)CoreCandlePatternParameter.CreateFor(IndicatorType.CandleBearishAbandonedBaby);
        Assert.True(bearBaby.RequireWickToWickGap);
        Assert.Equal(1.25m, bearBaby.VolumeMultiplier);
    }

    [Theory]
    [MemberData(nameof(Part2Types))]
    public void CI2_IndicatorWiring_All16TypesCalculateViaFactory(IndicatorType type)
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
        int minBars = expectedParam.ComputeMinBars(3);
        for (int t = minBars - 1; t < walk.Length; t++)
        {
            Assert.Equal(engine[t].BullishFires ? 1m : 0m, main[t]);
        }
    }
}
