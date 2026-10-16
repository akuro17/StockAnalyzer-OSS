using System;
using System.Collections.Generic;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class ScreenerHistoryRequirementsTests
{
    [Fact]
    public void GetRequiredCandleCount_ForStandardIndicator_ReturnsOptimizedDefault()
    {
        var entry = new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig
            {
                IndicatorType = IndicatorType.SMA,
                Parameters = new Dictionary<string, object> { { "Period", 20 } }
            }
        };

        int count = ScreenerHistoryRequirements.GetRequiredCandleCount(entry);

        Assert.Equal(500, count);
        Assert.False(ScreenerHistoryRequirements.RequiresFullHistory(entry));
    }

    [Fact]
    public void GetRequiredCandleCount_ForAnchoredVWAP_ReturnsZeroForFullHistory()
    {
        var entry = new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig
            {
                IndicatorType = IndicatorType.AnchoredVWAP,
                Parameters = new Dictionary<string, object> { { "AnchorIndex", 0 } }
            }
        };

        int count = ScreenerHistoryRequirements.GetRequiredCandleCount(entry);

        Assert.Equal(0, count);
        Assert.True(ScreenerHistoryRequirements.RequiresFullHistory(entry));
    }

    [Fact]
    public void GetRequiredCandleCount_ForLargePeriodIndicator_ReturnsCalculatedWarmupCount()
    {
        var entry = new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig
            {
                IndicatorType = IndicatorType.SMA,
                Parameters = new Dictionary<string, object> { { "Period", 250 } }
            }
        };

        int count = ScreenerHistoryRequirements.GetRequiredCandleCount(entry);

        // 250 * 5 + 0 + 100 = 1350 (the warm-up multiplier is IndicatorDefaultConstants.EmaConvergenceMultiplier = 5)
        Assert.Equal(1350, count);
        Assert.False(ScreenerHistoryRequirements.RequiresFullHistory(entry));
    }

    // Parameters of a template loaded from disk are System.Text.Json JsonElement values, not int/long.
    private static ScreenerIndicatorEntry EntryWithJsonParameters(string json, int offset = 0)
    {
        return new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig
            {
                IndicatorType = IndicatorType.SMA,
                Parameters = JsonSerializer.Deserialize<Dictionary<string, object>>(json)!,
                Offset = offset
            }
        };
    }

    [Fact]
    public void GetRequiredCandleCount_JsonElementPeriod_IsRecognised()
    {
        var entry = EntryWithJsonParameters("{\"Period\":200}", offset: 350);

        int count = ScreenerHistoryRequirements.GetRequiredCandleCount(entry);

        // 200 * 5 + 350 + 100 = 1450 (an unrecognised JsonElement period fell back to 20 => 510)
        Assert.Equal(1450, count);
    }

    // The warm-up of a recursive indicator is Period * EmaConvergenceMultiplier bars: the history size uses the same constant as the indicators (5 * 80 + 100 = 500).
    [Theory]
    [InlineData(80, 500)]
    [InlineData(81, 505)]
    public void GetRequiredCandleCount_UsesTheSharedConvergenceMultiplier(int period, int expected)
    {
        var entry = EntryWithJsonParameters("{\"Period\":" + period + "}");

        Assert.Equal(expected, ScreenerHistoryRequirements.GetRequiredCandleCount(entry));
        Assert.Equal(5, IndicatorDefaultConstants.EmaConvergenceMultiplier);
    }

    [Fact]
    public void GetMaxConditionOffset_IsTheLargestOffsetOfEverySide_AndZeroForNoCondition()
    {
        var entry = new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.SMA, Offset = 7 },
            TargetMode = RightHandTargetMode.NumericValue,
            RightHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.EMA, Offset = 40 }
        };

        Assert.Equal(40, ScreenerHistoryRequirements.GetMaxConditionOffset(entry)); // the right side counts in every mode, as in the history sizing
        Assert.Equal(0, ScreenerHistoryRequirements.GetMaxConditionOffset(null));
    }

    // ---- Criteria windows: the history that is loaded must hold the window the detector needs behind the evaluated bar, at every Offset ----

    private const string GranvilleBuy = "Granville's Law Buy Signal";

    private static ScreenerIndicatorSideConfig CriteriaSide(string name, int offset) => new()
    {
        CategoryType = ScreenerItemCategoryType.Criteria,
        CustomDisplayName = name,
        Offset = offset
    };

    private static ScreenerIndicatorEntry CriteriaEntry(ScreenerIndicatorSideConfig left) => new()
    {
        LeftHand = left,
        Operator = ComparisonOperator.GreaterThan,
        TargetMode = RightHandTargetMode.NumericValue,
        RightNumericValue = 0m
    };

    // 499 flat bars at 100, the breakout bar at 110 (a Granville buy on that bar), then `trailingBars` more bars at 110.
    private static List<CoreCandleData> BreakoutFollowedBy(int trailingBars)
    {
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 499 + 1 + trailingBars; i++)
        {
            decimal price = i < 499 ? 100m : 110m;
            candles.Add(new CoreCandleData(new DateTime(2024, 1, 1).AddDays(i), price, price, price, price, 1000));
        }
        return candles;
    }

    // The loader hands the scan the last GetRequiredCandleCount bars; the Granville buy bar is Offset bars before the end of the full history.
    // With only 100 + 5 * 20 bars behind an Offset above 294 the 206-bar Granville window was cut short and the answer was a silent 0.
    [Theory]
    [InlineData(0)]
    [InlineData(294)]
    [InlineData(295)]
    [InlineData(300)]
    [InlineData(301)]
    [InlineData(400)]
    [InlineData(500)]
    public void GetRequiredCandleCount_GranvilleCriteria_HoldsTheDetectorWindowAtEveryOffset(int offset)
    {
        var side = CriteriaSide(GranvilleBuy, offset);
        var full = BreakoutFollowedBy(offset);
        int required = ScreenerHistoryRequirements.GetRequiredCandleCount(CriteriaEntry(side));
        var loaded = full.GetRange(full.Count - Math.Min(required, full.Count), Math.Min(required, full.Count));

        Assert.Equal(1m, new ScreenerValueExtractor().ExtractValueNullable(side, loaded, default(TickerMetadata)));
    }

    [Theory]
    [InlineData("Granville's Law Buy Signal", 206)]
    [InlineData("granville sell", 206)]
    [InlineData("Bullish Engulfing", ScreenerHistoryRequirements.CriteriaWindowBars)]
    [InlineData("", ScreenerHistoryRequirements.CriteriaWindowBars)]
    [InlineData(null, ScreenerHistoryRequirements.CriteriaWindowBars)]
    public void GetCriteriaWindowBars_IsTheDetectorWindow(string? name, int expected)
    {
        Assert.Equal(expected, ScreenerHistoryRequirements.GetCriteriaWindowBars(name));
    }

    [Fact]
    public void GetRequiredCandleCount_CriteriaReach_CountsEachEvaluatedSideAndIgnoresAnUnusedRightSide()
    {
        var granvilleFar = CriteriaSide(GranvilleBuy, 400);

        // Left side: 400 + 206.
        Assert.Equal(606, ScreenerHistoryRequirements.GetRequiredCandleCount(CriteriaEntry(granvilleFar)));

        // Right side of an indicator comparison is evaluated, so it counts ...
        var indicatorMode = new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.SMA },
            TargetMode = RightHandTargetMode.Indicator,
            RightHand = granvilleFar
        };
        Assert.Equal(606, ScreenerHistoryRequirements.GetRequiredCandleCount(indicatorMode));

        // ... and in a numeric comparison it is not read at all, so its window is not added; only the Offset counts, as it does for every side in every
        // mode (5 * 20 + 400 + 100 = 600, not 400 + 206).
        var numericMode = new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.SMA },
            TargetMode = RightHandTargetMode.NumericValue,
            RightHand = granvilleFar
        };
        Assert.Equal(600, ScreenerHistoryRequirements.GetRequiredCandleCount(numericMode));
    }

    // The named constants replaced literals: the values are the behaviour that saved scans and the Criteria window already rely on.
    [Fact]
    public void NamedConstants_KeepTheValuesTheLiteralsHad()
    {
        Assert.Equal(100, ScreenerHistoryRequirements.CriteriaWindowBars);
        Assert.Equal(ScreenerHistoryRequirements.CriteriaWindowBars, ScreenerHistoryRequirements.HistoryMarginBars);
        Assert.Equal(20, ScreenerHistoryRequirements.DefaultIndicatorPeriod);
        Assert.Equal(200, ScreenerHistoryRequirements.UnknownConditionPeriod);
        Assert.Equal(10000, ScreenerHistoryRequirements.PeriodParameterExclusiveUpperBound);
    }

    [Fact]
    public void GetRequiredCandleCount_ConditionWithoutDeclaredPeriods_AssumesTheUnknownConditionPeriod()
    {
        var condition = new StockAnalyzer.Core.Models.ScreeningConditions.RsiOversoldCondition(14, 30m);

        // 200 * 5 + 0 + 100 = 1100
        Assert.Equal(
            (ScreenerHistoryRequirements.UnknownConditionPeriod * IndicatorDefaultConstants.EmaConvergenceMultiplier) + ScreenerHistoryRequirements.HistoryMarginBars,
            ScreenerHistoryRequirements.GetRequiredCandleCount(condition));
        Assert.Equal(1100, ScreenerHistoryRequirements.GetRequiredCandleCount(condition));
    }

    [Theory]
    [InlineData("{\"Period\":\"200\"}")]
    [InlineData("{\"Period\":10000}")]
    [InlineData("{\"Period\":200.5}")]
    [InlineData("{\"Period\":true}")]
    public void GetRequiredCandleCount_JsonElementNotAValidPeriod_IsIgnored(string json)
    {
        var entry = EntryWithJsonParameters(json);

        Assert.Equal(ScreenerHistoryRequirements.DefaultStandardCandleCount, ScreenerHistoryRequirements.GetRequiredCandleCount(entry));
    }
}
