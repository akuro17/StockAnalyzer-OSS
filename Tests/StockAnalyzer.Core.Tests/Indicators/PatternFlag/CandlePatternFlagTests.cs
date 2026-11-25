using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.PatternFlags;
using StockAnalyzer.Core.Models.Indicators.PatternFlags.Candles;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Serialization;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators.PatternFlag;

/// <summary>
/// The 43 candlestick flags (plan ChartPatterns56, section 5.1, run 2): the table, the registration, parity with the legacy detector (P-7), independence from its single-answer shadowing (P-8),
/// the two detector flags reaching the detector, the equality of the 100-bar window with the whole prefix, and the persistence of the two parameter classes.
/// </summary>
public class CandlePatternFlagTests
{
    private static readonly JsonSerializerOptions WorkspaceOptions = new() { TypeInfoResolver = WorkspacePolymorphicResolver.CreateResolver() };

    private static readonly IndicatorFactory Factory = new();

    private static IReadOnlyList<CandlePatternFlagEntry> Entries => CandlePatternFlagTable.Entries;

    public static IEnumerable<object[]> AllTypes() => Entries.Select(e => new object[] { e.Type });

    private static CoreCandlePatternIndicatorBase Create(IndicatorType type, CoreIndicatorParameterBase? parameters = null)
        => Assert.IsAssignableFrom<CoreCandlePatternIndicatorBase>(Factory.Create(type, parameters));

    private static CoreCandleData[] ToCore(IEnumerable<CandleData> bars)
        => bars.Select(b => new CoreCandleData(b.Timestamp, b.Open, b.High, b.Low, b.Close, b.Volume)).ToArray();

    private static decimal? LastMain(IndicatorType type, IReadOnlyList<CandleData> bars, CoreIndicatorParameterBase? parameters = null)
    {
        var result = Create(type, parameters).Calculate(ToCore(bars));
        Assert.True(result.IsSuccessful, result.ErrorMessage);
        return result.GetSeries(IndicatorResult.MainSeriesName)[^1];
    }

    // ---------------------------------------------------------------- the table

    [Fact]
    public void TheTable_HasThe43Types_WithTheDocumentedBarCounts()
    {
        Assert.Equal(43, Entries.Count);
        Assert.Equal(43, Entries.Select(e => e.Type).Distinct().Count());
        Assert.Equal(43, Entries.Select(e => e.Pattern).Distinct().Count());
        Assert.DoesNotContain(Entries, e => e.Pattern == CandlePatternType.None);

        var byLength = Entries.GroupBy(e => e.Length).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(new Dictionary<int, int> { [1] = 9, [2] = 9, [3] = 16, [4] = 3, [5] = 6 }, byLength);

        Assert.Equal(
            new[] { IndicatorType.CandleHammer, IndicatorType.CandleInvertedHammer, IndicatorType.CandleHangingMan, IndicatorType.CandleShootingStar }.OrderBy(t => t),
            Entries.Where(e => e.IsUmbrella).Select(e => e.Type).OrderBy(t => t));
    }

    [Fact]
    public void EveryCandleIndicatorType_IsInTheTable_AndTheCatalogHoldsExactlyThoseRows()
    {
        var candleMembers = Enum.GetValues<IndicatorType>().Where(t => t.ToString().StartsWith("Candle", StringComparison.Ordinal)).ToHashSet();

        Assert.Equal(candleMembers, Entries.Select(e => e.Type).ToHashSet());
        Assert.Equal(Entries.Select(e => e.Type), PatternFlagIndicatorCatalog.Rows.Where(r => r.Category == CoreIndicatorCategory.CandlestickPatterns).Select(r => r.Type));
    }

    [Fact]
    public void TheDescriptions_AreUnique_AndContinueTheScreenerCriteriaShortNames()
    {
        var descriptions = Entries.Select(e => e.Type.GetDescription()).ToList();

        Assert.Equal(43, descriptions.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var criteriaNames = new ScreenerCatalogProvider().GetCatalogItems()
            .Where(i => i.CategoryType == ScreenerItemCategoryType.Criteria && i.GroupName == ScreenerGroupNames.CandlestickPatterns)
            .Select(i => i.ShortName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(criteriaNames.OrderBy(n => n, StringComparer.Ordinal), descriptions.OrderBy(n => n, StringComparer.Ordinal));
    }

    // ---------------------------------------------------------------- registration

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void EveryType_IsRegistered_WithItsParameterClassAndColourGroup(IndicatorType type)
    {
        CandlePatternFlagEntry entry = CandlePatternFlagTable.Get(type);
        CoreCandlePatternIndicatorBase indicator = Create(type);

        Assert.Equal(type, indicator.Spec.Type);
        Assert.Equal(entry, indicator.Entry);
        Assert.Equal(CoreCandlePatternParameter.CreateFor(type).ComputeMinBars(entry.Length), indicator.Spec.MinBars);
        Assert.Equal(PatternFlagEvaluationModel.FullSeries, indicator.Spec.Model);
        Assert.False(indicator.Spec.HasDirection);
        Assert.Equal(CoreIndicatorCategory.CandlestickPatterns, indicator.Spec.Category);

        CoreIndicatorSettings settings = indicator.GetDefaultSettings();
        Assert.IsType(CoreCandlePatternParameter.CreateFor(type).GetType(), settings.ParameterObject);
        Assert.Equal(
            entry.Bias switch
            {
                CandleBias.Bullish => IndicatorDefaultConstants.PatternFlagBullishColor,
                CandleBias.Bearish => IndicatorDefaultConstants.PatternFlagBearishColor,
                _ => IndicatorDefaultConstants.PatternFlagNeutralColor,
            },
            settings.Color);
    }

    // All 43 candlestick types are evaluated by CandlestickPatternEngine since Run V2-10
    // (tests: PatternEngines/CandlestickPatternEnginePart1/2/3Tests, CandlestickPart1/2/3*IndicatorTests);
    // the legacy window-detector parity tests of runs 1-3 were retired with CandleWindowView.

    [Fact]
    public void EveryPattern_HasAFixtureWhereTheLegacySingleAnswerIsThatPattern()
    {
        Assert.Equal(43, CandlePatternFixtures.Exact.Count);

        foreach (CandlePatternFlagEntry entry in Entries)
        {
            List<CandleData> window = CandlePatternFixtures.Build(CandlePatternFixtures.Exact[entry.Pattern]);

            Assert.Equal(entry.Pattern, CandlePatternDetector.DetectPattern(window));
        }
    }

    // ---------------------------------------------------------------- the first bars (MinBars)

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void TheFlag_IsNullBeforeThePatternLength_AndNeverNullAfter(IndicatorType type)
    {
        PatternFlagSpec spec = PatternFlagIndicatorCatalog.Get(type);
        var candles = ToCore(PatternEngines.PatternEngineTestBars.Walk(2, Math.Max(40, spec.MinBars + 5)));

        var main = Create(type).Calculate(candles).GetSeries(IndicatorResult.MainSeriesName);

        Assert.Equal(candles.Length, main.Count);
        for (int t = 0; t < main.Count; t++)
        {
            if (t + 1 < spec.MinBars)
            {
                Assert.Null(main[t]);
            }
            else
            {
                Assert.Contains(main[t], new decimal?[] { 0m, 1m });
            }
        }
    }

    // ---------------------------------------------------------------- persistence of the parameter classes (they are permanent names, SAR section 7)

    [Fact]
    public void TheTwoParameterClasses_RoundTripThroughTheWorkspaceJson_WithTheirTypeName()
    {
        CoreIndicatorParameterBase candle = new CoreCandlePatternParameter { RequirePriorTrend = false, RequireVolumeConfirmation = true };
        CoreIndicatorParameterBase umbrella = new CoreUmbrellaPatternParameter { LongShadowBodyMultiplier = 3.0m, StrictColorMatch = true };

        string candleJson = JsonSerializer.Serialize(candle, WorkspaceOptions);
        string umbrellaJson = JsonSerializer.Serialize(umbrella, WorkspaceOptions);

        Assert.Contains("\"$type\":\"CoreCandlePatternParameter\"", candleJson);
        Assert.Contains("\"$type\":\"CoreUmbrellaPatternParameter\"", umbrellaJson);

        var candleBack = Assert.IsType<CoreCandlePatternParameter>(JsonSerializer.Deserialize<CoreIndicatorParameterBase>(candleJson, WorkspaceOptions));
        var umbrellaBack = Assert.IsType<CoreUmbrellaPatternParameter>(JsonSerializer.Deserialize<CoreIndicatorParameterBase>(umbrellaJson, WorkspaceOptions));

        Assert.False(candleBack.RequirePriorTrend);
        Assert.True(candleBack.RequireVolumeConfirmation);
        Assert.Equal(3.0m, umbrellaBack.LongShadowBodyMultiplier);
        Assert.True(umbrellaBack.StrictColorMatch);
    }

    [Fact]
    public void TheDefaults_MatchIntegrationSpec()
    {
        var candle = new CoreCandlePatternParameter();
        var umbrella = new CoreUmbrellaPatternParameter();

        Assert.Equal(14, candle.AtrPeriod);
        Assert.True(candle.RequirePriorTrend);
        Assert.True(candle.RequirePriceZone);
        Assert.False(candle.RequireVolumeConfirmation);
        Assert.Equal(1.20m, candle.VolumeMultiplier);
        Assert.Equal(2.0m, umbrella.LongShadowBodyMultiplier);
        Assert.Equal(0.50m, umbrella.OppositeShadowBodyMaxRatio);
        Assert.False(umbrella.StrictColorMatch);
        Assert.Equal(PatternFlagParameterLimits.WarmupBars, candle.GetRequiredWarmupBars());
        Assert.Equal("Bullish Engulfing", candle.GetDisplayName("Bullish Engulfing"));
        candle.Validate();
        umbrella.Validate();
    }
}
