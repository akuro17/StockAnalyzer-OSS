using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.PatternFlags;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;
using StockAnalyzer.Core.Services.Screener;
using StockAnalyzer.Core.Tests.PatternEngines;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services.Screener;

/// <summary>
/// Run V2-11 tests: 6 <see cref="ScreenerIndicatorGroup"/> Indicator groups backed by <see cref="PatternFlagIndicatorCatalog"/>,
/// <see cref="LegacyCriteriaNameMap"/> bijection, plan R-29 <see cref="ScreenerHistoryRequirements.GetMinimumBars"/> &amp;
/// <see cref="ScreenerHistoryRequirements.GetRequiredCandleCount"/>, and <see cref="ScreenerValueExtractor"/> Main + named series resolution
/// (ledger <c>docs/sa_pattern_literal_ledger_V2-11_ScreenerIntegration</c>).
/// </summary>
public class PatternFlagScreenerIntegrationTests
{
    private static readonly IndicatorFactory Factory = new();

    private static readonly (CoreIndicatorCategory Category, string GroupName, int ExpectedCount)[] ExpectedGroups =
    {
        (CoreIndicatorCategory.PatternRecognition, ScreenerGroupNames.PatternRecognition, 3),
        (CoreIndicatorCategory.MarketStructure, ScreenerGroupNames.MarketStructure, 4),
        (CoreIndicatorCategory.Harmonic, ScreenerGroupNames.Harmonic, 3),
        (CoreIndicatorCategory.TradingRules, ScreenerGroupNames.TradingRules, 2),
        (CoreIndicatorCategory.WaveTheory, ScreenerGroupNames.WaveTheory, 2),
        (CoreIndicatorCategory.CandlestickPatterns, ScreenerGroupNames.CandlestickPatterns, 43),
    };

    [Fact]
    public void V2_11_ScreenerIndicatorGroup_Contains6FlagIndicatorGroups_NoTrendFallback()
    {
        var indicatorGroups = ScreenerIndicatorGroup.GetDefaultGroups()
            .Where(g => g.CategoryType == "Indicator")
            .ToList();

        var seen = new HashSet<IndicatorType>();
        foreach (ScreenerIndicatorGroup g in indicatorGroups)
        {
            foreach (IndicatorType t in g.IndicatorTypes)
            {
                Assert.True(seen.Add(t), $"Duplicate IndicatorType '{t}' in Indicator groups.");
            }
        }

        foreach ((CoreIndicatorCategory cat, string groupName, int expectedCount) in ExpectedGroups)
        {
            ScreenerIndicatorGroup group = indicatorGroups.Single(g => g.Name == groupName);
            IndicatorType[] catalogTypes = PatternFlagIndicatorCatalog.Rows
                .Where(r => r.Category == cat)
                .Select(r => r.Type)
                .ToArray();

            Assert.Equal(expectedCount, catalogTypes.Length);
            Assert.Equal(catalogTypes, group.IndicatorTypes);
            Assert.DoesNotContain(IndicatorType.Price, group.IndicatorTypes);
        }

        // No flag type is left unassigned to fall back to "Trend".
        ScreenerIndicatorGroup trendGroup = indicatorGroups.Single(g => g.Name == "Trend");
        Assert.All(PatternFlagIndicatorCatalog.Rows, r => Assert.DoesNotContain(r.Type, trendGroup.IndicatorTypes));
    }

    [Fact]
    public void V2_11_ScreenerCatalogProvider_PlacesAll56FlagsInMatchingGroups()
    {
        var provider = new ScreenerCatalogProvider();
        IReadOnlyList<ScreenerCatalogItem>[] catalogs =
        {
            provider.GetCatalogItems(Factory),
            provider.GetCatalogItemsForScreener(Factory),
        };

        foreach (IReadOnlyList<ScreenerCatalogItem> catalog in catalogs)
        {
            var indicatorItems = catalog.Where(i => i.CategoryType == ScreenerItemCategoryType.Indicator).ToList();
            foreach ((CoreIndicatorCategory cat, string groupName, _) in ExpectedGroups)
            {
                foreach (PatternFlagSpec row in PatternFlagIndicatorCatalog.Rows.Where(r => r.Category == cat))
                {
                    ScreenerCatalogItem item = indicatorItems.Single(i => i.IndicatorType == row.Type);
                    Assert.Equal(groupName, item.GroupName);
                    Assert.Equal(row.Type.ToString(), item.ShortName);
                    Assert.Equal(row.Type.GetDescription(), item.DisplayName);
                }
            }
        }
    }

    [Fact]
    public void V2_11_LegacyCriteriaNameMap_CoversAll56CriteriaNamesAndTypes_Decoupled()
    {
        Assert.Equal(56, LegacyCriteriaNameMap.ByCriteriaName.Count);
        Assert.Equal(56, LegacyCriteriaNameMap.ByIndicatorType.Count);

        IReadOnlyList<ScreenerCatalogItem> criteriaItems = new ScreenerCatalogProvider()
            .GetCatalogItems(Factory)
            .Where(i => i.CategoryType == ScreenerItemCategoryType.Criteria)
            .ToList();
        Assert.Equal(56, criteriaItems.Count);

        var resolvedTypes = new HashSet<IndicatorType>();
        foreach (ScreenerCatalogItem item in criteriaItems)
        {
            Assert.True(LegacyCriteriaNameMap.TryResolve(item.ShortName, out IndicatorType resolved), $"Missing map entry for '{item.ShortName}'.");
            Assert.True(LegacyCriteriaNameMap.TryResolve(item.ShortName.ToLowerInvariant(), out IndicatorType lowerResolved));
            Assert.Equal(resolved, lowerResolved);
            Assert.True(resolvedTypes.Add(resolved), $"Duplicate target IndicatorType '{resolved}' from '{item.ShortName}'.");
            Assert.True(LegacyCriteriaNameMap.TryGetCriteriaName(resolved, out string roundTrip));
            Assert.Equal(item.ShortName, roundTrip);
        }

        Assert.Equal(
            PatternFlagIndicatorCatalog.Rows.Select(r => r.Type).Where(t => t != IndicatorType.MarketStructureLlLh).ToHashSet(),
            resolvedTypes);
        Assert.False(LegacyCriteriaNameMap.TryResolve(null, out _));
        Assert.False(LegacyCriteriaNameMap.TryResolve("NotARealCriteria", out _));
        Assert.False(LegacyCriteriaNameMap.TryGetCriteriaName(IndicatorType.SMA, out _));
        Assert.False(LegacyCriteriaNameMap.TryGetCriteriaName(IndicatorType.MarketStructureLlLh, out _));
    }

    [Fact]
    public void V2_11_GetMinimumBars_UsesWarmupPlusOffsetForFlags_AndUnchangedForAllNonFlags()
    {
        foreach (IndicatorType type in Enum.GetValues<IndicatorType>())
        {
            if (PatternFlagIndicatorCatalog.TryGet(type, out PatternFlagSpec? spec))
            {
                Assert.Equal(PatternFlagParameterLimits.WarmupBars, ScreenerHistoryRequirements.GetMinimumBars(type, null, 0));
                Assert.Equal(PatternFlagParameterLimits.WarmupBars + 37, ScreenerHistoryRequirements.GetMinimumBars(type, null, 37));
                Assert.True(ScreenerHistoryRequirements.GetMinimumBars(type, null, 0) >= spec.MinBars);
            }
            else
            {
                foreach (int offset in new[] { 0, 15 })
                {
                    int expected = (ScreenerHistoryRequirements.ResolveIndicatorWarmupBars(type, null) * IndicatorDefaultConstants.EmaConvergenceMultiplier)
                                   + offset
                                   + ScreenerHistoryRequirements.HistoryMarginBars;
                    Assert.Equal(expected, ScreenerHistoryRequirements.GetMinimumBars(type, null, offset));
                }
            }
        }

        // Custom parameters whose engine need exceeds 500 raise GetMinimumBars cleanly without 5x multiplier.
        var bigGranvilleParams = new Dictionary<string, object> { { "MaPeriod", 500 }, { "SlopePeriod", 50 } };
        Assert.Equal(551 + 12, ScreenerHistoryRequirements.GetMinimumBars(IndicatorType.GranvilleBuySignal, bigGranvilleParams, 12));
        Assert.Equal(551, ScreenerHistoryRequirements.GetMinimumBars(IndicatorType.GranvilleSellSignal, bigGranvilleParams, 0));
    }

    [Fact]
    public void V2_11_GetRequiredCandleCount_CoversAll56FlagsAcrossOffsetsAndCustomPeriods()
    {
        CandleData[] walk = PatternEngineTestBars.Walk(seed: 11, count: 1050);

        foreach (PatternFlagSpec spec in PatternFlagIndicatorCatalog.Rows)
        {
            foreach (int offset in new[] { 0, 100, 294, 500 })
            {
                var entry = new ScreenerIndicatorEntry
                {
                    LeftHand = new ScreenerIndicatorSideConfig
                    {
                        CategoryType = ScreenerItemCategoryType.Indicator,
                        IndicatorType = spec.Type,
                        Offset = offset,
                    },
                };

                int count = ScreenerHistoryRequirements.GetRequiredCandleCount(entry);
                int minExpected = Math.Max(spec.MinBars, PatternFlagParameterLimits.WarmupBars) + offset;
                Assert.True(count >= minExpected, $"{spec.Type} offset={offset}: got {count}, expected >= {minExpected}");
                Assert.False(ScreenerHistoryRequirements.RequiresFullHistory(entry));

                // Extracting on exactly `count` bars at `offset` produces a valid non-null flag value (0 or 1).
                decimal? extracted = ScreenerValueExtractor.Default.ExtractValueNullable(entry.LeftHand, walk.Take(count).ToArray());
                Assert.NotNull(extracted);
            }
        }

        var bigGranvilleEntry = new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig
            {
                CategoryType = ScreenerItemCategoryType.Indicator,
                IndicatorType = IndicatorType.GranvilleBuySignal,
                Parameters = new Dictionary<string, object> { { "MaPeriod", 500 }, { "SlopePeriod", 50 } },
                Offset = 10,
            },
        };
        int bigCount = ScreenerHistoryRequirements.GetRequiredCandleCount(bigGranvilleEntry);
        Assert.True(bigCount >= 561, $"Expected >= 561, got {bigCount}");
        Assert.NotNull(ScreenerValueExtractor.Default.ExtractValueNullable(bigGranvilleEntry.LeftHand, walk.Take(bigCount).ToArray()));
    }

    [Fact]
    public void V2_11_ScreenerValueExtractor_SelectsMainAndNamedSeriesForFlags()
    {
        CandleData[] walk = PatternEngineTestBars.Walk(seed: 12, count: 300);
        var extractor = ScreenerValueExtractor.Default;

        // Group A (bidirectional): PatternDoubleTopBottom -> Main, DoubleBottom, DoubleTop, Direction (plus Offset check).
        var doubleDefault = new ScreenerIndicatorSideConfig
        {
            CategoryType = ScreenerItemCategoryType.Indicator,
            IndicatorType = IndicatorType.PatternDoubleTopBottom,
        };
        var doubleBottomSide = new ScreenerIndicatorSideConfig
        {
            CategoryType = ScreenerItemCategoryType.Indicator,
            IndicatorType = IndicatorType.PatternDoubleTopBottom,
            OutputName = "DoubleBottom",
        };
        var doubleTopSide = new ScreenerIndicatorSideConfig
        {
            CategoryType = ScreenerItemCategoryType.Indicator,
            IndicatorType = IndicatorType.PatternDoubleTopBottom,
            OutputName = "DoubleTop",
        };
        var bosDirSide = new ScreenerIndicatorSideConfig
        {
            CategoryType = ScreenerItemCategoryType.Indicator,
            IndicatorType = IndicatorType.MarketStructureBos,
            OutputName = IndicatorSeriesNames.PatternDirection,
            Offset = 5,
        };

        CoreCandleData[] coreWalk = walk.Select(b => new CoreCandleData(b.Timestamp, b.Open, b.High, b.Low, b.Close, b.Volume)).ToArray();
        IIndicatorResult doubleDirect = Factory.Create(IndicatorType.PatternDoubleTopBottom)!.Calculate(coreWalk);
        Assert.Equal(doubleDirect.MainValues[^1], extractor.ExtractValueNullable(doubleDefault, walk));
        Assert.Equal(doubleDirect.GetSeries("DoubleBottom")[^1], extractor.ExtractValueNullable(doubleBottomSide, walk));
        Assert.Equal(doubleDirect.GetSeries("DoubleTop")[^1], extractor.ExtractValueNullable(doubleTopSide, walk));

        IIndicatorResult bosDirect = Factory.Create(IndicatorType.MarketStructureBos)!.Calculate(coreWalk);
        Assert.Equal(bosDirect.GetSeries(IndicatorSeriesNames.PatternDirection)[^6], extractor.ExtractValueNullable(bosDirSide, walk));

        // Group B (multi-rule): GranvilleBuySignal -> Main, Buy1, RuleCode.
        var granvilleMain = new ScreenerIndicatorSideConfig { CategoryType = ScreenerItemCategoryType.Indicator, IndicatorType = IndicatorType.GranvilleBuySignal };
        var granvilleBuy1 = new ScreenerIndicatorSideConfig { CategoryType = ScreenerItemCategoryType.Indicator, IndicatorType = IndicatorType.GranvilleBuySignal, OutputName = "Buy1" };
        var granvilleRuleCode = new ScreenerIndicatorSideConfig { CategoryType = ScreenerItemCategoryType.Indicator, IndicatorType = IndicatorType.GranvilleBuySignal, OutputName = "RuleCode" };
        IIndicatorResult granvilleDirect = Factory.Create(IndicatorType.GranvilleBuySignal)!.Calculate(coreWalk);
        Assert.Equal(granvilleDirect.MainValues[^1], extractor.ExtractValueNullable(granvilleMain, walk));
        Assert.Equal(granvilleDirect.GetSeries("Buy1")[^1], extractor.ExtractValueNullable(granvilleBuy1, walk));
        Assert.Equal(granvilleDirect.GetSeries("RuleCode")[^1], extractor.ExtractValueNullable(granvilleRuleCode, walk));

        // Group C (single series): MarketStructureHhHl + MarketStructureLlLh + CandleMorningStar -> Main.
        var hhhlSide = new ScreenerIndicatorSideConfig { CategoryType = ScreenerItemCategoryType.Indicator, IndicatorType = IndicatorType.MarketStructureHhHl };
        var lllhSide = new ScreenerIndicatorSideConfig { CategoryType = ScreenerItemCategoryType.Indicator, IndicatorType = IndicatorType.MarketStructureLlLh };
        var candleSide = new ScreenerIndicatorSideConfig { CategoryType = ScreenerItemCategoryType.Indicator, IndicatorType = IndicatorType.CandleMorningStar, OutputName = IndicatorResult.MainSeriesName };
        Assert.Equal(Factory.Create(IndicatorType.MarketStructureHhHl)!.Calculate(coreWalk).MainValues[^1], extractor.ExtractValueNullable(hhhlSide, walk));
        Assert.Equal(Factory.Create(IndicatorType.MarketStructureLlLh)!.Calculate(coreWalk).MainValues[^1], extractor.ExtractValueNullable(lllhSide, walk));
        Assert.Equal(Factory.Create(IndicatorType.CandleMorningStar)!.Calculate(coreWalk).MainValues[^1], extractor.ExtractValueNullable(candleSide, walk));
    }
}
