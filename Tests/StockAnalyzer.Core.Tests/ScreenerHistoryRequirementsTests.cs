using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
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

    [Fact]
    public void GetRequiredCandleCount_RsiOversoldConditionWithLongPeriod_ScalesBeyondUnknownConditionFloor()
    {
        var condition = new StockAnalyzer.Core.Models.ScreeningConditions.RsiOversoldCondition(250, 30m);

        int expected = (250 * IndicatorDefaultConstants.EmaConvergenceMultiplier) + ScreenerHistoryRequirements.HistoryMarginBars;
        Assert.Equal(expected, ScreenerHistoryRequirements.GetRequiredCandleCount(condition));
        Assert.Equal(1350, ScreenerHistoryRequirements.GetRequiredCandleCount(condition));
    }

    // ===== G-1 / G1-T2: a Ratiocator in NormalizedRatio mode takes its base (R_0) from the FIRST bar it is given, so it needs the whole stored history =====

    private static ScreenerIndicatorEntry RatiocatorEntry(string key, object? mode, RightHandTargetMode targetMode = RightHandTargetMode.NumericValue)
    {
        var parameters = new Dictionary<string, object> { { "ComparisonSymbol", "SPY" }, { "LookbackPeriod", 20 } };
        if (mode is not null)
        {
            parameters[key] = mode;
        }

        return new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.Ratiocator, Parameters = parameters },
            TargetMode = targetMode,
        };
    }

    private static object JsonValue(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static IEnumerable<object?[]> NormalizedRatioForms()
    {
        yield return new object?[] { "CalculationMode", RatiocatorCalculationMode.NormalizedRatio };
        yield return new object?[] { "CalculationMode", "NormalizedRatio" };
        yield return new object?[] { "CalculationMode", "normalizedratio" };
        yield return new object?[] { "calculationmode", "NORMALIZEDRATIO" };
        yield return new object?[] { "CalculationMode", " NormalizedRatio " };
        yield return new object?[] { "CalculationMode", JsonValue("\"NormalizedRatio\"") };
        yield return new object?[] { "CalculationMode", 1 };
        yield return new object?[] { "CalculationMode", 1L };
        yield return new object?[] { "CalculationMode", JsonValue("1") };
        yield return new object?[] { "CalculationMode", "1" };
    }

    public static IEnumerable<object?[]> OtherModes()
    {
        yield return new object?[] { "CalculationMode", RatiocatorCalculationMode.RollingRatio };
        yield return new object?[] { "CalculationMode", RatiocatorCalculationMode.RawRatio };
        yield return new object?[] { "CalculationMode", "RollingRatio" };
        yield return new object?[] { "CalculationMode", "RawRatio" };
        yield return new object?[] { "CalculationMode", JsonValue("\"RollingRatio\"") };
        yield return new object?[] { "CalculationMode", 0 };
        yield return new object?[] { "CalculationMode", 2 };
        yield return new object?[] { "CalculationMode", JsonValue("2") };
        yield return new object?[] { "CalculationMode", "not a mode" };
        yield return new object?[] { "CalculationMode", true };
        yield return new object?[] { "LookbackPeriod", 1 };
        yield return new object?[] { "OtherKey", "NormalizedRatio" };
    }

    [Theory]
    [MemberData(nameof(NormalizedRatioForms))]
    public void GetRequiredCandleCount_RatiocatorInNormalizedRatioMode_NeedsTheWholeHistory_InEverySavedForm(string key, object mode)
    {
        ScreenerIndicatorEntry entry = RatiocatorEntry(key, mode);

        Assert.Equal(0, ScreenerHistoryRequirements.GetRequiredCandleCount(entry));
        Assert.True(ScreenerHistoryRequirements.RequiresFullHistory(entry));
    }

    [Theory]
    [MemberData(nameof(OtherModes))]
    public void GetRequiredCandleCount_RatiocatorInAnotherModeOrWithoutAMode_KeepsTheOrdinaryCount(string key, object mode)
    {
        ScreenerIndicatorEntry entry = RatiocatorEntry(key, mode);

        Assert.True(ScreenerHistoryRequirements.GetRequiredCandleCount(entry) > 0);
        Assert.False(ScreenerHistoryRequirements.RequiresFullHistory(entry));
    }

    [Fact]
    public void GetRequiredCandleCount_RatiocatorWithoutAnyModeParameter_KeepsTheOrdinaryCount()
    {
        ScreenerIndicatorEntry entry = RatiocatorEntry("CalculationMode", null);

        Assert.Equal(ScreenerHistoryRequirements.DefaultStandardCandleCount, ScreenerHistoryRequirements.GetRequiredCandleCount(entry));
        Assert.False(ScreenerHistoryRequirements.RequiresFullHistory(entry));
    }

    [Fact]
    public void GetRequiredCandleCount_ANormalizedRatioOnTheRightSideOrInATree_NeedsTheWholeHistory()
    {
        ScreenerIndicatorEntry right = new()
        {
            LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.SMA, Parameters = new Dictionary<string, object> { { "Period", 20 } } },
            TargetMode = RightHandTargetMode.Indicator,
            RightHand = RatiocatorEntry("CalculationMode", "NormalizedRatio").LeftHand,
        };
        Assert.Equal(0, ScreenerHistoryRequirements.GetRequiredCandleCount(right));

        // The right side is only read when the entry compares against an indicator.
        right.TargetMode = RightHandTargetMode.NumericValue;
        Assert.Equal(ScreenerHistoryRequirements.DefaultStandardCandleCount, ScreenerHistoryRequirements.GetRequiredCandleCount(right));

        var tree = new ScreenerConditionTreeCondition(new ScreenerConditionGroup(
            LogicalOperator.And,
            ImmutableArray.Create<IScreenerConditionNode>(
                new ScreenerConditionLeaf(RatiocatorEntry("CalculationMode", RatiocatorCalculationMode.RollingRatio)),
                new ScreenerConditionLeaf(RatiocatorEntry("CalculationMode", RatiocatorCalculationMode.NormalizedRatio)))));
        Assert.Equal(0, ScreenerHistoryRequirements.GetRequiredCandleCount(tree));
    }

    [Fact]
    public void GetRequiredCandleCount_OnlyRatiocatorIsAffected_AnotherTypeWithTheSameParameterKeepsItsCount()
    {
        var entry = new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig
            {
                IndicatorType = IndicatorType.PairRatio,
                Parameters = new Dictionary<string, object> { { "CalculationMode", "NormalizedRatio" } },
            },
        };

        Assert.Equal(ScreenerHistoryRequirements.DefaultStandardCandleCount, ScreenerHistoryRequirements.GetRequiredCandleCount(entry));
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

    // ===== WebAiExpansion plan 12 T1: a VWAP that never restarts (ResetPeriod Cumulative, the default) sums from the FIRST bar it is given, so it needs the whole stored history =====

    private static ScreenerIndicatorEntry VwapEntry(Dictionary<string, object>? parameters, RightHandTargetMode targetMode = RightHandTargetMode.NumericValue)
        => new()
        {
            LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.VWAP, Parameters = parameters },
            TargetMode = targetMode,
        };

    private static ScreenerIndicatorEntry VwapEntry(string key, object value)
        => VwapEntry(new Dictionary<string, object> { { key, value } });

    public static IEnumerable<object?[]> CumulativeVwapForms()
    {
        yield return new object?[] { "ResetPeriod", VwapResetPeriod.Cumulative };
        yield return new object?[] { "ResetPeriod", "Cumulative" };
        yield return new object?[] { "ResetPeriod", " cumulative " };
        yield return new object?[] { "resetperiod", "CUMULATIVE" };
        yield return new object?[] { "ResetPeriod", JsonValue("\"Cumulative\"") };
        yield return new object?[] { "ResetPeriod", 0 };
        yield return new object?[] { "ResetPeriod", 0L };
        yield return new object?[] { "ResetPeriod", JsonValue("0") };
        yield return new object?[] { "ResetPeriod", "0" };
        // Not a defined member, or not a number of the enum at all: not trusted to restart.
        yield return new object?[] { "ResetPeriod", "Foo" };
        yield return new object?[] { "ResetPeriod", true };
        yield return new object?[] { "ResetPeriod", 99 };
        yield return new object?[] { "ResetPeriod", 1.5 };
        yield return new object?[] { "ResetPeriod", JsonValue("\"Foo\"") };
        yield return new object?[] { "ResetPeriod", JsonValue("99") };
        // Another key: the ResetPeriod is absent, i.e. the default Cumulative.
        yield return new object?[] { "OtherKey", "Weekly" };
    }

    public static IEnumerable<object?[]> RestartingVwapForms()
    {
        foreach (VwapResetPeriod period in new[] { VwapResetPeriod.Weekly, VwapResetPeriod.Monthly, VwapResetPeriod.Quarterly, VwapResetPeriod.Yearly })
        {
            yield return new object?[] { "ResetPeriod", period };
            yield return new object?[] { "ResetPeriod", period.ToString() };
            yield return new object?[] { "ResetPeriod", $" {period.ToString().ToUpperInvariant()} " };
            yield return new object?[] { "ResetPeriod", JsonValue($"\"{period}\"") };
            yield return new object?[] { "ResetPeriod", (int)period };
            yield return new object?[] { "ResetPeriod", (long)period };
            yield return new object?[] { "ResetPeriod", JsonValue(((int)period).ToString()) };
            yield return new object?[] { "ResetPeriod", ((int)period).ToString() };
        }
    }

    [Fact]
    public void GetRequiredCandleCount_VwapWithoutParameters_NeedsTheWholeHistory()
    {
        foreach (Dictionary<string, object>? parameters in new Dictionary<string, object>?[] { null, new Dictionary<string, object>() })
        {
            ScreenerIndicatorEntry entry = VwapEntry(parameters);

            Assert.Equal(0, ScreenerHistoryRequirements.GetRequiredCandleCount(entry));
            Assert.True(ScreenerHistoryRequirements.RequiresFullHistory(entry));
        }
    }

    [Theory]
    [MemberData(nameof(CumulativeVwapForms))]
    public void GetRequiredCandleCount_VwapThatNeverRestarts_NeedsTheWholeHistory_InEverySavedForm(string key, object value)
    {
        ScreenerIndicatorEntry entry = VwapEntry(key, value);

        Assert.Equal(0, ScreenerHistoryRequirements.GetRequiredCandleCount(entry));
        Assert.True(ScreenerHistoryRequirements.RequiresFullHistory(entry));
    }

    [Theory]
    [MemberData(nameof(RestartingVwapForms))]
    public void GetRequiredCandleCount_VwapThatRestarts_KeepsTheOrdinaryCount_InEverySavedForm(string key, object value)
    {
        ScreenerIndicatorEntry entry = VwapEntry(key, value);

        Assert.Equal(ScreenerHistoryRequirements.DefaultStandardCandleCount, ScreenerHistoryRequirements.GetRequiredCandleCount(entry));
        Assert.False(ScreenerHistoryRequirements.RequiresFullHistory(entry));
    }

    [Fact]
    public void GetRequiredCandleCount_ACumulativeVwapOnTheRightSideOrInATreeOrInABundle_NeedsTheWholeHistory()
    {
        ScreenerIndicatorEntry right = new()
        {
            LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.SMA, Parameters = new Dictionary<string, object> { { "Period", 20 } } },
            TargetMode = RightHandTargetMode.Indicator,
            RightHand = VwapEntry(null).LeftHand,
        };
        Assert.Equal(0, ScreenerHistoryRequirements.GetRequiredCandleCount(right));

        // The right side is only read when the entry compares against an indicator.
        right.TargetMode = RightHandTargetMode.NumericValue;
        Assert.Equal(ScreenerHistoryRequirements.DefaultStandardCandleCount, ScreenerHistoryRequirements.GetRequiredCandleCount(right));

        ScreenerIndicatorEntry yearly = VwapEntry("ResetPeriod", VwapResetPeriod.Yearly);
        var tree = new ScreenerConditionTreeCondition(new ScreenerConditionGroup(
            LogicalOperator.And,
            ImmutableArray.Create<IScreenerConditionNode>(
                new ScreenerConditionLeaf(yearly),
                new ScreenerConditionLeaf(VwapEntry(null)))));
        Assert.Equal(0, ScreenerHistoryRequirements.GetRequiredCandleCount(tree));

        var yearlyOnly = new ScreenerConditionTreeCondition(new ScreenerConditionGroup(
            LogicalOperator.And,
            ImmutableArray.Create<IScreenerConditionNode>(new ScreenerConditionLeaf(yearly))));
        Assert.Equal(ScreenerHistoryRequirements.DefaultStandardCandleCount, ScreenerHistoryRequirements.GetRequiredCandleCount(yearlyOnly));
    }

    [Fact]
    public void GetRequiredCandleCount_ACumulativeVwapNextToALongPeriodIndicator_GivesTheSameWholeHistoryAsAloneSoItsValueDoesNotDependOnTheOtherCondition()
    {
        ScreenerIndicatorEntry alone = VwapEntry(null);
        ScreenerIndicatorEntry sma = new()
        {
            LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.SMA, Parameters = new Dictionary<string, object> { { "Period", 200 } } },
        };
        var together = new ScreenerConditionTreeCondition(new ScreenerConditionGroup(
            LogicalOperator.And,
            ImmutableArray.Create<IScreenerConditionNode>(new ScreenerConditionLeaf(alone), new ScreenerConditionLeaf(sma))));

        Assert.Equal(0, ScreenerHistoryRequirements.GetRequiredCandleCount(alone));
        Assert.Equal(0, ScreenerHistoryRequirements.GetRequiredCandleCount(together));
    }

    // ===== WebAiExpansion plan 12 F-3 (decision U-1 B): the extractor applies a parameter only under its EXACT name, so only the exact key is trusted here =====
    // Three situations: the exact key exists (its value alone decides), only a key that differs in case exists (not applied by the extractor, value not trusted: whole history),
    // no key at all (the indicator's own default: VWAP Cumulative = whole history, Ratiocator RollingRatio = ordinary count).

    public static IEnumerable<object?[]> CaseVariantKeysWithAnyValue()
    {
        yield return new object?[] { "resetperiod", "Weekly" };
        yield return new object?[] { "RESETPERIOD", VwapResetPeriod.Yearly };
        yield return new object?[] { "Resetperiod", 1 };
        yield return new object?[] { "resetperiod", JsonValue("\"Monthly\"") };
        yield return new object?[] { "resetperiod", "Cumulative" };
    }

    [Theory]
    [MemberData(nameof(CaseVariantKeysWithAnyValue))]
    public void GetRequiredCandleCount_VwapKeyDifferingOnlyInCase_IsNotTrusted_NeedsTheWholeHistory(string key, object value)
    {
        ScreenerIndicatorEntry entry = VwapEntry(key, value);

        Assert.Equal(0, ScreenerHistoryRequirements.GetRequiredCandleCount(entry));
        Assert.True(ScreenerHistoryRequirements.RequiresFullHistory(entry));
    }

    [Fact]
    public void GetRequiredCandleCount_VwapWithTheExactKeyAndACaseVariant_FollowsTheExactKeyOnly()
    {
        // The extractor applies "ResetPeriod" and ignores "resetperiod": the exact key decides in both directions.
        ScreenerIndicatorEntry exactWeekly = VwapEntry(new Dictionary<string, object> { { "ResetPeriod", "Weekly" }, { "resetperiod", "Cumulative" } });
        Assert.Equal(ScreenerHistoryRequirements.DefaultStandardCandleCount, ScreenerHistoryRequirements.GetRequiredCandleCount(exactWeekly));
        Assert.False(ScreenerHistoryRequirements.RequiresFullHistory(exactWeekly));

        ScreenerIndicatorEntry exactCumulative = VwapEntry(new Dictionary<string, object> { { "resetperiod", "Weekly" }, { "ResetPeriod", "Cumulative" } });
        Assert.Equal(0, ScreenerHistoryRequirements.GetRequiredCandleCount(exactCumulative));
        Assert.True(ScreenerHistoryRequirements.RequiresFullHistory(exactCumulative));
    }

    public static IEnumerable<object?[]> CaseVariantRatiocatorKeysWithAnyValue()
    {
        yield return new object?[] { "calculationmode", "NormalizedRatio" };
        yield return new object?[] { "CALCULATIONMODE", RatiocatorCalculationMode.RawRatio };
        yield return new object?[] { "calculationmode", "RollingRatio" };
        yield return new object?[] { "Calculationmode", 0 };
    }

    [Theory]
    [MemberData(nameof(CaseVariantRatiocatorKeysWithAnyValue))]
    public void GetRequiredCandleCount_RatiocatorKeyDifferingOnlyInCase_IsNotTrusted_NeedsTheWholeHistory(string key, object value)
    {
        ScreenerIndicatorEntry entry = RatiocatorEntry(key, value);

        Assert.Equal(0, ScreenerHistoryRequirements.GetRequiredCandleCount(entry));
        Assert.True(ScreenerHistoryRequirements.RequiresFullHistory(entry));
    }

    [Fact]
    public void GetRequiredCandleCount_RatiocatorWithTheExactKeyAndACaseVariant_FollowsTheExactKeyOnly()
    {
        ScreenerIndicatorEntry exactRolling = RatiocatorEntry("CalculationMode", RatiocatorCalculationMode.RollingRatio);
        exactRolling.LeftHand.Parameters["calculationmode"] = "NormalizedRatio";
        Assert.Equal(ScreenerHistoryRequirements.DefaultStandardCandleCount, ScreenerHistoryRequirements.GetRequiredCandleCount(exactRolling));
        Assert.False(ScreenerHistoryRequirements.RequiresFullHistory(exactRolling));

        ScreenerIndicatorEntry exactNormalized = RatiocatorEntry("CalculationMode", RatiocatorCalculationMode.NormalizedRatio);
        exactNormalized.LeftHand.Parameters["calculationmode"] = "RollingRatio";
        Assert.Equal(0, ScreenerHistoryRequirements.GetRequiredCandleCount(exactNormalized));
        Assert.True(ScreenerHistoryRequirements.RequiresFullHistory(exactNormalized));
    }
}
