using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.PatternFlags;
using StockAnalyzer.Core.Models.Indicators.PatternFlags.Rules;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.PatternEngines;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators.PatternFlag;

/// <summary>
/// Plan ChartPatterns56 V2, run V2-2: the output schema of the flag indicators (integration spec 4.2, plan R-30/R-31, CF-V2-13/21/22). A bidirectional type has the series Main, Direction and one visible series per side
/// (named in the catalog row, exposed as a public property of the same name); the others have Main only. The invariants hold for all 49 types the catalog lists today.
/// </summary>
public class PatternFlagSeriesSchemaTests
{
    private const int Bars = 400;
    private static readonly IndicatorFactory Factory = new();

    public static IEnumerable<object[]> AllTypes() => PatternFlagIndicatorCatalog.Rows.Select(r => new object[] { r.Type });

    public static IEnumerable<object[]> BidirectionalTypes() => PatternFlagIndicatorCatalog.Rows.Where(r => r.HasDirection).Select(r => new object[] { r.Type });

    public static IEnumerable<object[]> RuleTypes() => PatternFlagIndicatorCatalog.Rows.Where(r => r.HasRules).Select(r => new object[] { r.Type });

    private static CoreCandleData[] CoreBars(int seed)
        => PatternEngineTestBars.Walk(seed, Bars).Select(b => new CoreCandleData(b.Timestamp, b.Open, b.High, b.Low, b.Close, b.Volume)).ToArray();

    private static (ICoreIndicator Indicator, IIndicatorResult Result) Calculate(IndicatorType type, int seed = 3)
    {
        ICoreIndicator indicator = Factory.Create(type, FormationFixtures.RelaxedParameters(type))!;
        IIndicatorResult result = indicator.Calculate(CoreBars(seed));
        Assert.True(result.IsSuccessful, result.ErrorMessage);
        return (indicator, result);
    }

    // ---- the catalog row -----------------------------------------------------------------------------------------

    [Fact]
    public void TheBidirectionalRows_NameTwoSideSeries_TheOthersNone()
    {
        foreach (PatternFlagSpec row in PatternFlagIndicatorCatalog.Rows)
        {
            if (row.HasDirection)
            {
                Assert.False(string.IsNullOrWhiteSpace(row.BullishSeries));
                Assert.False(string.IsNullOrWhiteSpace(row.BearishSeries));
                Assert.NotEqual(row.BullishSeries, row.BearishSeries);
                Assert.Equal(-1.2m, row.YMin);
            }
            else
            {
                Assert.Null(row.BullishSeries);
                Assert.Null(row.BearishSeries);
                Assert.Equal(0m, row.YMin);
            }

            Assert.Equal(1.2m, row.YMax);
        }
    }

    [Fact]
    public void TheBidirectionalTypes_HaveTheNamesOfTheIntegrationSpec()
    {
        var expected = new Dictionary<IndicatorType, (string Bull, string Bear)>
        {
            [IndicatorType.PatternHeadAndShoulders] = ("InverseBottom", "Top"),
            [IndicatorType.PatternDoubleTopBottom] = ("DoubleBottom", "DoubleTop"),
            [IndicatorType.PatternFlag] = ("BullishFlag", "BearishFlag"),
            [IndicatorType.MarketStructureBos] = ("BullishBos", "BearishBos"),
            [IndicatorType.MarketStructureChoch] = ("BullishChoch", "BearishChoch"),
            [IndicatorType.HarmonicGartley] = ("BullishGartley", "BearishGartley"),
            [IndicatorType.HarmonicButterfly] = ("BullishButterfly", "BearishButterfly"),
            [IndicatorType.HarmonicBat] = ("BullishBat", "BearishBat"),
            [IndicatorType.ElliottWaveImpulse] = ("BullishImpulse", "BearishImpulse"),
            [IndicatorType.ElliottWaveCorrective] = ("BullishCorrective", "BearishCorrective"),
        };

        Assert.Equal(expected.Count, PatternFlagIndicatorCatalog.Rows.Count(r => r.HasDirection));
        foreach ((IndicatorType type, (string bull, string bear)) in expected)
        {
            PatternFlagSpec row = PatternFlagIndicatorCatalog.Get(type);
            Assert.Equal(bull, row.BullishSeries);
            Assert.Equal(bear, row.BearishSeries);
        }
    }

    // ---- the result ----------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void TheResult_HasExactlyTheSeriesOfTheRow_AndTheInvariantsHold(IndicatorType type)
    {
        PatternFlagSpec row = PatternFlagIndicatorCatalog.Get(type);
        (_, IIndicatorResult result) = Calculate(type);

        var expectedNames = new List<string> { IndicatorResult.MainSeriesName };
        if (row.HasDirection)
        {
            expectedNames.AddRange(new[] { IndicatorSeriesNames.PatternDirection, row.BullishSeries!, row.BearishSeries! });
        }
        else if (row.HasRules)
        {
            expectedNames.Add(CoreGranvilleIndicatorBase.RuleCodeSeriesName);
            expectedNames.AddRange(row.RuleSeries!);
        }

        Assert.Equal(expectedNames.OrderBy(n => n, StringComparer.Ordinal), result.SeriesNamesList.OrderBy(n => n, StringComparer.Ordinal));

        IReadOnlyList<decimal?> main = result.GetSeries(IndicatorResult.MainSeriesName);
        Assert.Equal(Bars, main.Count);
        Assert.All(main, v => Assert.True(v is null or 0m or 1m));

        if (row.HasRules)
        {
            IReadOnlyList<decimal?> ruleCode = result.GetSeries(CoreGranvilleIndicatorBase.RuleCodeSeriesName);
            IReadOnlyList<decimal?>[] rules = row.RuleSeries!.Select(result.GetSeries).ToArray();
            Assert.Equal(Bars, ruleCode.Count);
            Assert.All(rules, s => Assert.Equal(Bars, s.Count));

            for (int t = 0; t < Bars; t++)
            {
                if (main[t] is null)
                {
                    Assert.Null(ruleCode[t]);
                    Assert.All(rules, s => Assert.Null(s[t]));
                    continue;
                }

                Assert.All(rules, s => Assert.Contains(s[t], new decimal?[] { 0m, 1m }));
                bool anyRule = rules.Any(s => s[t] == 1m);
                Assert.Equal(main[t] == 1m, anyRule);
                Assert.Equal(anyRule, ruleCode[t] != 0m);
            }

            return;
        }

        if (!row.HasDirection)
        {
            return;
        }

        IReadOnlyList<decimal?> direction = result.GetSeries(IndicatorSeriesNames.PatternDirection);
        IReadOnlyList<decimal?> bull = result.GetSeries(row.BullishSeries!);
        IReadOnlyList<decimal?> bear = result.GetSeries(row.BearishSeries!);
        Assert.All(new[] { direction, bull, bear }, s => Assert.Equal(Bars, s.Count));

        for (int t = 0; t < Bars; t++)
        {
            if (main[t] is null)
            {
                Assert.Null(direction[t]);
                Assert.Null(bull[t]);
                Assert.Null(bear[t]);
                continue;
            }

            Assert.Contains(bull[t], new decimal?[] { 0m, 1m });
            Assert.Contains(bear[t], new decimal?[] { 0m, -1m });
            Assert.Equal(main[t] == 1m, bull[t] == 1m || bear[t] == -1m);
            Assert.Equal(main[t] == 1m, direction[t] != 0m);
            if (main[t] == 1m)
            {
                Assert.True((direction[t] == 1m && bull[t] == 1m) || (direction[t] == -1m && bear[t] == -1m));
            }
            else
            {
                Assert.Equal(0m, direction[t]);
            }
        }
    }

    [Theory]
    [MemberData(nameof(BidirectionalTypes))]
    public void TheVisibleSides_AreNotAllZero_OnAWalk(IndicatorType type)
    {
        PatternFlagSpec row = PatternFlagIndicatorCatalog.Get(type);
        var sidesSeen = new HashSet<string>();
        foreach (int seed in new[] { 1, 2, 3, 4, 5, 6 })
        {
            (_, IIndicatorResult result) = Calculate(type, seed);
            if (result.GetSeries(row.BullishSeries!).Any(v => v == 1m)) sidesSeen.Add("bull");
            if (result.GetSeries(row.BearishSeries!).Any(v => v == -1m)) sidesSeen.Add("bear");
        }

        Assert.NotEmpty(sidesSeen); // not vacuous: at least one of the two sides fires on these walks
    }

    // ---- the public properties (output-name discovery of the Screener and MCP) ---------------------------------------

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void ThePublicSeriesProperties_AreTheSeriesOfTheRow_AndEqualTheResultAfterACalculation(IndicatorType type)
    {
        PatternFlagSpec row = PatternFlagIndicatorCatalog.Get(type);
        (ICoreIndicator indicator, IIndicatorResult result) = Calculate(type);

        string[] propertyNames = indicator.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => typeof(IEnumerable<decimal?>).IsAssignableFrom(p.PropertyType) && p.Name != nameof(ICoreIndicator.Values))
            .Select(p => p.Name).ToArray();

        if (!row.HasDirection && !row.HasRules)
        {
            Assert.Empty(propertyNames); // one series: the discovery falls back to the type name
            return;
        }

        IEnumerable<string> expectedProps = row.HasDirection
            ? new[] { IndicatorResult.MainSeriesName, IndicatorSeriesNames.PatternDirection, row.BullishSeries!, row.BearishSeries! }
            : new[] { IndicatorResult.MainSeriesName, CoreGranvilleIndicatorBase.RuleCodeSeriesName }.Concat(row.RuleSeries!);

        Assert.Equal(
            expectedProps.OrderBy(n => n, StringComparer.Ordinal),
            propertyNames.OrderBy(n => n, StringComparer.Ordinal));

        foreach (string name in propertyNames)
        {
            var property = indicator.GetType().GetProperty(name)!;
            var values = ((IEnumerable<decimal?>)property.GetValue(indicator)!).ToArray();
            Assert.Equal(result.GetSeries(name).ToArray(), values);
        }
    }

    [Theory]
    [MemberData(nameof(BidirectionalTypes))]
    public void OutputNameDiscovery_ListsTheFourSeries_AndTheMainAliasResolvesToMain(IndicatorType type)
    {
        PatternFlagSpec row = PatternFlagIndicatorCatalog.Get(type);
        (_, IIndicatorResult result) = Calculate(type);

        IReadOnlyList<string> names = IndicatorOutputSeriesResolver.GetDisplayNames(type);
        Assert.Equal(
            new[] { IndicatorResult.MainSeriesName, IndicatorSeriesNames.PatternDirection, row.BullishSeries!, row.BearishSeries! }.OrderBy(n => n, StringComparer.Ordinal),
            names.OrderBy(n => n, StringComparer.Ordinal));

        // every listed output is emitted by a default-parameter calculation, and the Screener resolves each by name
        foreach (string name in names)
        {
            Assert.True(IndicatorOutputSeriesResolver.TryResolveSeries(result, type, name, (PriceType?)null, out IReadOnlyList<decimal?> series), name);
            Assert.Equal(Bars, series.Count);
        }

        Assert.Equal(IndicatorResult.MainSeriesName, IndicatorOutputSeriesResolver.Normalize(type, IndicatorResult.MainSeriesName));
    }

    [Theory]
    [MemberData(nameof(RuleTypes))]
    public void OutputNameDiscovery_ListsTheSixRuleSeries_AndTheMainAliasResolvesToMain(IndicatorType type)
    {
        PatternFlagSpec row = PatternFlagIndicatorCatalog.Get(type);
        (_, IIndicatorResult result) = Calculate(type);

        IReadOnlyList<string> names = IndicatorOutputSeriesResolver.GetDisplayNames(type);
        string[] expected = new[] { IndicatorResult.MainSeriesName, CoreGranvilleIndicatorBase.RuleCodeSeriesName }.Concat(row.RuleSeries!).ToArray();
        Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal), names.OrderBy(n => n, StringComparer.Ordinal));

        foreach (string name in names)
        {
            Assert.True(IndicatorOutputSeriesResolver.TryResolveSeries(result, type, name, (PriceType?)null, out IReadOnlyList<decimal?> series), name);
            Assert.Equal(Bars, series.Count);
        }

        Assert.Equal(IndicatorResult.MainSeriesName, IndicatorOutputSeriesResolver.Normalize(type, IndicatorResult.MainSeriesName));
    }

    // ---- hiding -------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void MainAndDirection_AreHiddenOnTheChartAndInTheDataWindow_OnlyForABidirectionalType(IndicatorType type)
    {
        PatternFlagSpec row = PatternFlagIndicatorCatalog.Get(type);

        Assert.Equal(row.HasDirection || row.HasRules, IndicatorChartSuppression.IsSuppressed(type, IndicatorResult.MainSeriesName));
        Assert.Equal(row.HasDirection, IndicatorChartSuppression.IsSuppressed(type, IndicatorSeriesNames.PatternDirection));
        Assert.Equal(row.HasDirection || row.HasRules, IndicatorChartSuppression.IsDataWindowSuppressed(type, IndicatorResult.MainSeriesName));
        Assert.Equal(row.HasDirection, IndicatorChartSuppression.IsDataWindowSuppressed(type, IndicatorSeriesNames.PatternDirection));

        if (row.HasDirection)
        {
            Assert.False(IndicatorChartSuppression.IsSuppressed(type, row.BullishSeries!));
            Assert.False(IndicatorChartSuppression.IsSuppressed(type, row.BearishSeries!));
            Assert.False(IndicatorChartSuppression.IsDataWindowSuppressed(type, row.BullishSeries!));
            Assert.False(IndicatorChartSuppression.IsDataWindowSuppressed(type, row.BearishSeries!));
            Assert.False(IndicatorSeriesNames.IsDirectionalSignalSeries(row.BullishSeries!));
            Assert.False(IndicatorSeriesNames.IsDirectionalSignalSeries(row.BearishSeries!));
        }

        if (row.HasRules)
        {
            Assert.True(IndicatorChartSuppression.IsSuppressed(type, CoreGranvilleIndicatorBase.RuleCodeSeriesName));
            Assert.True(IndicatorChartSuppression.IsDataWindowSuppressed(type, CoreGranvilleIndicatorBase.RuleCodeSeriesName));
            foreach (string ruleSeries in row.RuleSeries!)
            {
                Assert.False(IndicatorChartSuppression.IsSuppressed(type, ruleSeries));
                Assert.False(IndicatorChartSuppression.IsDataWindowSuppressed(type, ruleSeries));
            }
        }
    }

    // ---- default settings ---------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void TheDefaultSettings_HaveTheRangeAndTheSideColoursOfTheRow(IndicatorType type)
    {
        PatternFlagSpec row = PatternFlagIndicatorCatalog.Get(type);
        CoreIndicatorSettings settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == type);

        Assert.False(settings.IsEnabled);
        Assert.Equal(row.YMin, settings.MinValue);
        Assert.Equal(row.YMax, settings.MaxValue);

        if (row.HasRules)
        {
            Assert.Equal(row.RuleSeries!.Length, settings.SeriesColors.Count);
            for (int i = 0; i < row.RuleSeries.Length; i++)
            {
                Assert.Equal(row.RuleColors![i], settings.SeriesColors[i].Color);
                Assert.Equal(new[] { row.RuleSeries[i] }, settings.SeriesColors[i].TargetSeries);
            }

            return;
        }

        if (!row.HasDirection)
        {
            Assert.Empty(settings.SeriesColors);
            return;
        }

        Assert.Equal(2, settings.SeriesColors.Count);
        Assert.Equal(IndicatorDefaultConstants.PatternFlagBullishColor, settings.SeriesColors[0].Color);
        Assert.Equal(new[] { row.BullishSeries! }, settings.SeriesColors[0].TargetSeries);
        Assert.Equal(IndicatorDefaultConstants.PatternFlagBearishColor, settings.SeriesColors[1].Color);
        Assert.Equal(new[] { row.BearishSeries! }, settings.SeriesColors[1].TargetSeries);
    }

    // ---- AutoHeal (R-30) ------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(BidirectionalTypes))]
    public void AutoHeal_WidensExactlyTheOldDefaultRange_AndBackfillsTheSideColours(IndicatorType type)
    {
        CoreIndicatorSettings old = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == type).Clone();
        old.MinValue = IndicatorDefaultConstants.PatternFlagMinValue;   // the range of runs 1-3
        old.MaxValue = IndicatorDefaultConstants.PatternFlagMaxValue;
        old.SeriesColors = new();

        DefaultCoreIndicatorSettings.AutoHeal(old);

        Assert.Equal(-1.2m, old.MinValue);
        Assert.Equal(1.2m, old.MaxValue);
        Assert.Equal(2, old.SeriesColors.Count);
    }

    [Theory]
    [InlineData(-5.0, 3.0)]
    [InlineData(0.0, 2.0)]
    [InlineData(-0.5, 1.2)]
    public void AutoHeal_NeverOverwritesARangeTheUserSet(double min, double max)
    {
        CoreIndicatorSettings custom = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == IndicatorType.PatternDoubleTopBottom).Clone();
        custom.MinValue = (decimal)min;
        custom.MaxValue = (decimal)max;

        DefaultCoreIndicatorSettings.AutoHeal(custom);

        Assert.Equal((decimal)min, custom.MinValue);
        Assert.Equal((decimal)max, custom.MaxValue);
    }

    [Fact]
    public void AutoHeal_LeavesASingleSeriesFlagAtItsRange()
    {
        CoreIndicatorSettings candle = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == IndicatorType.CandleHammer).Clone();
        DefaultCoreIndicatorSettings.AutoHeal(candle);
        Assert.Equal(0m, candle.MinValue);
        Assert.Equal(1.2m, candle.MaxValue);
    }

    // ---- registration (R-31, R-28) --------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void EveryFlagType_IsUnitStable_ForTheFeaturePicker(IndicatorType type)
        => Assert.Equal(ChannelNormalization.None, FeatureChannelNormalizationDefaults.ForIndicator(type));

    [Fact]
    public void AnOrdinaryIndicator_KeepsItsFeatureDefault()
    {
        Assert.Equal(ChannelNormalization.WindowMinMax, FeatureChannelNormalizationDefaults.ForIndicator(IndicatorType.SMA));
        Assert.Equal(ChannelNormalization.None, FeatureChannelNormalizationDefaults.ForIndicator(IndicatorType.RSI));
    }

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void TheWarmupRequest_IsTheOneConstant_AndNeverBelowTheFirstValueBar(IndicatorType type)
    {
        PatternFlagSpec row = PatternFlagIndicatorCatalog.Get(type);
        CoreIndicatorParameterBase parameters = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == type).ParameterObject!;

        Assert.Equal(PatternFlagParameterLimits.WarmupBars, parameters.GetRequiredWarmupBars());
        Assert.True(row.MinBars <= PatternFlagParameterLimits.WarmupBars);
    }

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void AShortSeries_IsCalculatedFromMinBarsOn_NotRefusedBecauseOfTheWarmupRequest(IndicatorType type)
    {
        PatternFlagSpec row = PatternFlagIndicatorCatalog.Get(type);
        int bars = row.MinBars + 5; // far below the 500 bars of the warm-up request
        CoreCandleData[] core = CoreBars(2).Take(bars).ToArray();

        IIndicatorResult result = Factory.Create(type)!.Calculate(core);

        Assert.True(result.IsSuccessful, result.ErrorMessage);
        IReadOnlyList<decimal?> main = result.GetSeries(IndicatorResult.MainSeriesName);
        Assert.Equal(bars, main.Count);
        if (row.MinBars >= 2)
        {
            Assert.Null(main[row.MinBars - 2]);
        }

        Assert.NotNull(main[row.MinBars - 1]);
        Assert.NotNull(main[bars - 1]);
    }
}
