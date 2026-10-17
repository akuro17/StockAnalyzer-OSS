using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.GeometricPattern;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Core.Tests;

public class ScreenerCriteriaRoundTripTests
{
    private readonly ITestOutputHelper _output;

    public ScreenerCriteriaRoundTripTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static readonly Dictionary<string, CandlePatternType> ExpectedCandlePatterns = new()
    {
        ["Bullish Marubozu"] = CandlePatternType.BullishMarubozu,
        ["Bearish Marubozu"] = CandlePatternType.BearishMarubozu,
        ["Bullish Engulfing"] = CandlePatternType.BullishEngulfing,
        ["Bearish Engulfing"] = CandlePatternType.BearishEngulfing,
        ["Bullish Harami"] = CandlePatternType.BullishHarami,
        ["Bearish Harami"] = CandlePatternType.BearishHarami,
        ["Morning Star"] = CandlePatternType.MorningStar,
        ["Evening Star"] = CandlePatternType.EveningStar,
        ["Hammer / Bullish Umbrella"] = CandlePatternType.BullishUmbrella,
        ["Inverted Hammer"] = CandlePatternType.BullishInvertedUmbrella,
        ["Hanging Man / Bearish Umbrella"] = CandlePatternType.BearishUmbrella,
        ["Shooting Star"] = CandlePatternType.BearishInvertedUmbrella,
        ["Doji / Cross Doji"] = CandlePatternType.CrossDoji,
        ["Dragonfly Doji"] = CandlePatternType.DragonflyDoji,
        ["Gravestone Doji"] = CandlePatternType.GravestoneDoji,
        ["Piercing Line"] = CandlePatternType.PiercingLine,
        ["Dark Cloud Cover"] = CandlePatternType.DarkCloudCover,
        ["Three White Soldiers"] = CandlePatternType.ThreeWhiteSoldiers,
        ["Three Black Crows"] = CandlePatternType.ThreeBlackCrows,
        ["Rising Three Methods"] = CandlePatternType.RisingThreeMethods,
        ["Falling Three Methods"] = CandlePatternType.FallingThreeMethods,
        ["Mat Hold"] = CandlePatternType.MatHold,
        ["Bullish Side-by-Side White Lines"] = CandlePatternType.BullishSideBySideWhiteLines,
        ["Bearish Side-by-Side White Lines"] = CandlePatternType.BearishSideBySideWhiteLines,
        ["Bullish Three-Line Strike"] = CandlePatternType.BullishThreeLineStrike,
        ["Bearish Three-Line Strike"] = CandlePatternType.BearishThreeLineStrike,
        ["Bullish Abandoned Baby"] = CandlePatternType.BullishAbandonedBaby,
        ["Bearish Abandoned Baby"] = CandlePatternType.BearishAbandonedBaby,
        ["Advance Block"] = CandlePatternType.AdvanceBlock,
        ["Deliberation"] = CandlePatternType.Deliberation,
        ["Stick Sandwich"] = CandlePatternType.StickSandwich,
        ["Ladder Bottom"] = CandlePatternType.LadderBottom,
        ["Homing Pigeon"] = CandlePatternType.HomingPigeon,
        ["Bullish Tasuki Gap"] = CandlePatternType.BullishTasukiGap,
        ["Bearish Tasuki Gap"] = CandlePatternType.BearishTasukiGap,
        ["Bullish Gap Three Methods"] = CandlePatternType.BullishGapThreeMethods,
        ["Bearish Gap Three Methods"] = CandlePatternType.BearishGapThreeMethods,
        ["Bullish Breakaway"] = CandlePatternType.BullishBreakaway,
        ["Bearish Breakaway"] = CandlePatternType.BearishBreakaway,
        ["Bullish Kicking"] = CandlePatternType.BullishKicking,
        ["Bearish Kicking"] = CandlePatternType.BearishKicking,
        ["Concealing Baby Swallow"] = CandlePatternType.ConcealingBabySwallow,
        ["Identical Three Crows"] = CandlePatternType.IdenticalThreeCrows,
    };

    private static List<CoreCandleData> FlatCandles(int count, decimal price)
    {
        var candles = new List<CoreCandleData>(count);
        for (int i = 0; i < count; i++)
        {
            candles.Add(new CoreCandleData(new DateTime(2024, 1, 1).AddDays(i), price, price, price, price, 1000));
        }
        return candles;
    }

    private static List<CoreCandleData> FlatThenBreakout(int flatBars)
    {
        var candles = FlatCandles(flatBars, 100m);
        candles.Add(new CoreCandleData(new DateTime(2024, 1, 1).AddDays(flatBars), 110m, 110m, 110m, 110m, 1000));
        return candles;
    }

    [Fact]
    public void Probe_MeasureRoutingFailures_WithResolveLookupName()
    {
        var provider = new ScreenerCatalogProvider();
        var criteriaItems = provider.GetCatalogItems()
            .Where(i => i.CategoryType == ScreenerItemCategoryType.Criteria)
            .ToList();

        Assert.Equal(56, criteriaItems.Count);

        int failureCount = 0;
        var failureDetails = new List<string>();

        foreach (var item in criteriaItems)
        {
            var config = new ScreenerIndicatorSideConfig
            {
                CategoryType = ScreenerItemCategoryType.Criteria,
                CustomDisplayName = item.DisplayName,
                OutputName = item.ShortName,
            };

            // Today's routing uses ResolveLookupName()
            string lookupName = config.ResolveLookupName() ?? string.Empty;

            if (ExpectedCandlePatterns.TryGetValue(item.ShortName, out var expectedPattern))
            {
                var resolved = ScreenerValueExtractor.ResolveCandlePatternType(lookupName);
                if (resolved != expectedPattern)
                {
                    failureCount++;
                    failureDetails.Add($"Candle: '{item.ShortName}' (Desc: '{item.DisplayName}') resolved to '{resolved?.ToString() ?? "null"}' (expected {expectedPattern})");
                }
            }
            else if (item.ShortName == "Bullish / Bearish Flag")
            {
                var resolved = ScreenerValueExtractor.ResolveGeometricFormationType(lookupName);
                if (resolved != GeometricFormationType.BullishFlag)
                {
                    failureCount++;
                    failureDetails.Add($"Geometric: '{item.ShortName}' resolved to '{resolved?.ToString() ?? "null"}' (expected BullishFlag)");
                }
            }
            else
            {
                // Other 12 items: should resolve to item.ShortName
                if (lookupName != item.ShortName)
                {
                    failureCount++;
                    failureDetails.Add($"Other: '{item.ShortName}' resolved to '{lookupName}' (expected '{item.ShortName}')");
                }
            }
        }

        _output.WriteLine($"Total Criteria items: {criteriaItems.Count}, Routing failures before fix: {failureCount}");
        foreach (var d in failureDetails)
        {
            _output.WriteLine($"  {d}");
        }

        // We verify that the probe detects the defect (red before fix)
        Assert.True(failureCount > 0, $"Expected routing failures before fix, but found {failureCount}");
    }

    [Fact]
    public void Granville_RegisteredAsCriteriaDialogWay_ExtractsValueOne()
    {
        var config = new ScreenerIndicatorSideConfig
        {
            CategoryType = ScreenerItemCategoryType.Criteria,
            CustomDisplayName = "Granville's Moving Average Buy Signal 1-4",
            OutputName = "Granville's Law Buy Signal",
        };
        var extractor = new ScreenerValueExtractor();
        var candles = FlatThenBreakout(499);
        decimal? val = extractor.ExtractValueNullable(config, candles, default(TickerMetadata));
        Assert.Equal(1m, val);
    }

    [Fact]
    public void All56CriteriaItems_RoundTripProperly_WithResolveCriteriaName()
    {
        var provider = new ScreenerCatalogProvider();
        var criteriaItems = provider.GetCatalogItems()
            .Where(i => i.CategoryType == ScreenerItemCategoryType.Criteria)
            .ToList();

        Assert.Equal(56, criteriaItems.Count);

        foreach (var item in criteriaItems)
        {
            var config = new ScreenerIndicatorSideConfig
            {
                CategoryType = ScreenerItemCategoryType.Criteria,
                CustomDisplayName = item.DisplayName,
                OutputName = item.ShortName,
            };

            string? criteriaName = config.ResolveCriteriaName();
            Assert.NotNull(criteriaName);

            if (ExpectedCandlePatterns.TryGetValue(item.ShortName, out var expectedPattern))
            {
                var resolved = ScreenerValueExtractor.ResolveCandlePatternType(criteriaName);
                Assert.Equal(expectedPattern, resolved);
            }
            else if (item.ShortName == "Bullish / Bearish Flag")
            {
                var resolved = ScreenerValueExtractor.ResolveGeometricFormationType(criteriaName);
                Assert.Equal(GeometricFormationType.BullishFlag, resolved);
            }
            else
            {
                // Other 12 items: should resolve to item.ShortName
                Assert.Equal(item.ShortName, criteriaName);
            }
        }
    }
}
