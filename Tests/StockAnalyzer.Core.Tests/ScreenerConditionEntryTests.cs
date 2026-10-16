using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class ScreenerConditionEntryTests
{
    [Fact]
    public void DisplayName_ReturnsFormattedString_ForNumericComparison()
    {
        var entry = new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig
            {
                IndicatorType = IndicatorType.SMA,
                Parameters = new Dictionary<string, object> { { "Period", 20 } },
                TimeFrame = TimeFrame.D1,
                Offset = 0
            },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 50m
        };

        string displayName = entry.DisplayName;
        Assert.Contains("SMA", displayName);
        Assert.Contains(">", displayName);
        Assert.EndsWith("50", displayName);
    }

    [Fact]
    public void DisplayName_ReturnsFormattedString_ForIndicatorComparison()
    {
        var entry = new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig
            {
                IndicatorType = IndicatorType.SMA,
                Parameters = new Dictionary<string, object> { { "Period", 20 } },
                TimeFrame = TimeFrame.D1,
                Offset = 0
            },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.Indicator,
            RightHand = new ScreenerIndicatorSideConfig
            {
                IndicatorType = IndicatorType.EMA,
                Parameters = new Dictionary<string, object> { { "Period", 50 } },
                TimeFrame = TimeFrame.W1,
                Offset = 1
            }
        };

        string displayName = entry.DisplayName;
        Assert.Contains("SMA", displayName);
        Assert.Contains(">", displayName);
        Assert.Contains("EMA", displayName);
    }

    [Fact]
    public void IsMet_EvaluatesTechnicalIndicatorsCorrectly()
    {
        var candles = new List<CandleData>();
        DateTime now = DateTime.UtcNow;
        for (int i = 0; i < 30; i++)
        {
            candles.Add(new CandleData(now.AddDays(i), 100m + i, 105m + i, 95m + i, 100m + i, 1000));
        }

        // Test SMA(20) > 100
        var smaEntry = new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig
            {
                IndicatorType = IndicatorType.SMA,
                Parameters = new Dictionary<string, object> { { "Period", 20 } }
            },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 100m
        };

        Assert.True(smaEntry.IsMet(candles));

        // Test RSI(14) > 90
        var rsiEntry = new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig
            {
                IndicatorType = IndicatorType.RSI,
                Parameters = new Dictionary<string, object> { { "Period", 14 } }
            },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 90m
        };

        Assert.True(rsiEntry.IsMet(candles));

        // Test BollingerBands (BB) via IndicatorFactory
        var bbEntry = new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig
            {
                IndicatorType = IndicatorType.BB,
                OutputName = "Main"
            },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 50m
        };

        Assert.True(bbEntry.IsMet(candles));
    }

    // c_i = 100 + i, O = H = L = C.
    private static List<CandleData> RisingCandleData(int count)
    {
        var candles = new List<CandleData>();
        for (int i = 0; i < count; i++)
        {
            candles.Add(new CandleData(new DateTime(2024, 1, 1).AddDays(i), 100m + i, 100m + i, 100m + i, 100m + i, 1000));
        }
        return candles;
    }

    private static ScreenerIndicatorSideConfig PriceClose(int offset = 0) => new()
    {
        IndicatorType = IndicatorType.Price,
        OutputName = nameof(PriceType.Close),
        Offset = offset
    };

    // A condition whose right-hand indicator has no value (SMA 200 over 30 bars) must not match: before, the missing value became 0 and "Close > 0" was true.
    [Fact]
    public void Entry_RightIndicatorMissing_IsFalse()
    {
        var entry = new ScreenerIndicatorEntry
        {
            LeftHand = PriceClose(),
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.Indicator,
            RightHand = new ScreenerIndicatorSideConfig
            {
                IndicatorType = IndicatorType.SMA,
                Parameters = new Dictionary<string, object> { { "Period", 200 } }
            }
        };

        Assert.False(entry.IsMet(RisingCandleData(30)));
    }

    // RSI 14 needs more than 5 bars: before, the warm-up value became 0 and "RSI < 30" was true.
    [Fact]
    public void Entry_LeftIndicatorWarmup_IsFalse()
    {
        var entry = new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig
            {
                IndicatorType = IndicatorType.RSI,
                Parameters = new Dictionary<string, object> { { "Period", 14 } }
            },
            Operator = ComparisonOperator.LessThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 30m
        };

        Assert.False(entry.IsMet(RisingCandleData(5)));
    }

    // An Offset beyond the data used to fall back to the latest bar: "Close > 0" at Offset 150 over 100 bars was true.
    [Theory]
    [InlineData(ComparisonOperator.GreaterThan)]
    [InlineData(ComparisonOperator.LessThan)]
    [InlineData(ComparisonOperator.NotEqual)]
    public void Entry_OffsetBeyondData_IsFalseForEveryOperator(ComparisonOperator op)
    {
        var entry = new ScreenerIndicatorEntry
        {
            LeftHand = PriceClose(150),
            Operator = op,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = op == ComparisonOperator.LessThan ? 1000m : 0m
        };

        Assert.False(entry.IsMet(RisingCandleData(100)));
    }

    // Both sides read an Offset beyond the data: neither side may fall back to the latest bar.
    [Fact]
    public void Entry_RightOffsetBeyondData_IsFalse()
    {
        var entry = new ScreenerIndicatorEntry
        {
            LeftHand = PriceClose(),
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.Indicator,
            RightHand = PriceClose(150)
        };

        Assert.False(entry.IsMet(RisingCandleData(100)));
    }

    // Existing in-range behaviour: today's close 199 is above the close 99 bars ago (100).
    [Fact]
    public void Entry_InRangeOffset_Unchanged()
    {
        var entry = new ScreenerIndicatorEntry
        {
            LeftHand = PriceClose(),
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.Indicator,
            RightHand = PriceClose(99)
        };

        Assert.True(entry.IsMet(RisingCandleData(100)));
    }

    // ---- Signal flags (IsLong, IsTPLong, ...): a flag whose state is unknown is a missing value, not "no signal" (0) ----

    private static readonly TickerMetadata NoFlags = new("AAPL", "Apple Inc.", "US", "Technology", "Semiconductors", "USD");

    private static TickerMetadata WithFlag(string flag, bool? value) => flag switch
    {
        "IsLong" => NoFlags with { IsLong = value },
        "IsTPLong" => NoFlags with { IsTPLong = value },
        "IsSLLong" => NoFlags with { IsSLLong = value },
        "IsShort" => NoFlags with { IsShort = value },
        "IsTPShort" => NoFlags with { IsTPShort = value },
        "IsSLShort" => NoFlags with { IsSLShort = value },
        _ => throw new ArgumentOutOfRangeException(nameof(flag), flag, null)
    };

    private static ScreenerIndicatorSideConfig FlagColumn(string flag) => new()
    {
        CategoryType = ScreenerItemCategoryType.Column,
        CustomDisplayName = flag
    };

    private static ScreenerIndicatorEntry FlagEntry(string flag, decimal expected) => new()
    {
        LeftHand = FlagColumn(flag),
        Operator = ComparisonOperator.Equal,
        TargetMode = RightHandTargetMode.NumericValue,
        RightNumericValue = expected
    };

    [Theory]
    [InlineData("IsLong")]
    [InlineData("IsTPLong")]
    [InlineData("IsSLLong")]
    [InlineData("IsShort")]
    [InlineData("IsTPShort")]
    [InlineData("IsSLShort")]
    public void SignalFlag_ValueIsOneZeroOrMissing(string flag)
    {
        var extractor = ScreenerValueExtractor.Default;
        var candles = RisingCandleData(3);

        Assert.Equal(1m, extractor.ExtractValueNullable(FlagColumn(flag), candles, WithFlag(flag, true)));
        Assert.Equal(0m, extractor.ExtractValueNullable(FlagColumn(flag), candles, WithFlag(flag, false)));
        Assert.Null(extractor.ExtractValueNullable(FlagColumn(flag), candles, WithFlag(flag, null)));
    }

    // Before: an unknown flag read as 0, so "IsLong == 0" matched every symbol whose signal state was simply not loaded.
    [Theory]
    [InlineData("IsLong")]
    [InlineData("IsTPLong")]
    [InlineData("IsSLLong")]
    [InlineData("IsShort")]
    [InlineData("IsTPShort")]
    [InlineData("IsSLShort")]
    public void SignalFlag_Unknown_NeverMatchesAnyComparison(string flag)
    {
        var candles = RisingCandleData(3);
        var unknown = WithFlag(flag, null);

        Assert.False(FlagEntry(flag, 0m).IsMet(candles, unknown));
        Assert.False(FlagEntry(flag, 1m).IsMet(candles, unknown));
        Assert.False(new ScreenerIndicatorEntry
        {
            LeftHand = FlagColumn(flag),
            Operator = ComparisonOperator.NotEqual,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 1m
        }.IsMet(candles, unknown));
    }

    [Fact]
    public void SignalFlag_Known_StillMatches()
    {
        var candles = RisingCandleData(3);

        Assert.True(FlagEntry("IsLong", 1m).IsMet(candles, WithFlag("IsLong", true)));
        Assert.True(FlagEntry("IsLong", 0m).IsMet(candles, WithFlag("IsLong", false)));
        Assert.False(FlagEntry("IsLong", 1m).IsMet(candles, WithFlag("IsLong", false)));
    }

    // ---- Text conditions (spec S8): Sector / Industry / Region / Name (the long name) / Tag against the ticker metadata ----

    private static readonly TickerMetadata Apple = new("AAPL", "Apple Inc.", "US", "Technology", "Semiconductors", "USD") { Tag = "Growth, Tech;Value" };

    private static ScreenerIndicatorEntry TextEntry(string column, ComparisonOperator op, string text) => new()
    {
        // The registration dialog stores the short name in OutputName and the catalog description in CustomDisplayName.
        LeftHand = new ScreenerIndicatorSideConfig { CategoryType = ScreenerItemCategoryType.Column, OutputName = column, CustomDisplayName = "Description of " + column },
        Operator = op,
        TargetMode = RightHandTargetMode.StringValue,
        RightStringValue = text
    };

    public static IEnumerable<object[]> TextCases() => new[]
    {
        new object[] { "Sector", ComparisonOperator.Contains, "tech", true },
        new object[] { "Sector", ComparisonOperator.Contains, "Health", false },
        new object[] { "Sector", ComparisonOperator.DoesNotContain, "Health", true },
        new object[] { "Sector", ComparisonOperator.DoesNotContain, "tech", false },
        new object[] { "Sector", ComparisonOperator.Equal, " technology ", true },
        new object[] { "Sector", ComparisonOperator.Equal, "Tech", false },
        new object[] { "Sector", ComparisonOperator.NotEqual, "Energy", true },
        new object[] { "Sector", ComparisonOperator.NotEqual, "TECHNOLOGY", false },
        new object[] { "Industry", ComparisonOperator.Contains, "semi", true },
        new object[] { "Industry", ComparisonOperator.Equal, "Semiconductors", true },
        new object[] { "Region", ComparisonOperator.Equal, "us", true },
        new object[] { "Region", ComparisonOperator.NotEqual, "US", false },
        new object[] { "Name", ComparisonOperator.Contains, "apple", true },
        new object[] { "Name", ComparisonOperator.Equal, "Apple Inc.", true },
        new object[] { "Name", ComparisonOperator.Equal, "AAPL", false },
        // Tag: comma / semicolon separated tokens, whole-token match for Contains and DoesNotContain.
        new object[] { "Tag", ComparisonOperator.Contains, "tech", true },
        new object[] { "Tag", ComparisonOperator.Contains, "Value", true },
        new object[] { "Tag", ComparisonOperator.Contains, "ech", false },
        new object[] { "Tag", ComparisonOperator.DoesNotContain, "Growth", false },
        new object[] { "Tag", ComparisonOperator.DoesNotContain, "Income", true },
        new object[] { "Tag", ComparisonOperator.Equal, "Growth, Tech;Value", true },
        new object[] { "Tag", ComparisonOperator.Equal, "Growth", false },
        new object[] { "Tag", ComparisonOperator.NotEqual, "Growth", true },
    };

    [Theory]
    [MemberData(nameof(TextCases))]
    public void Text_Comparison_FollowsTheSpecification(string column, ComparisonOperator op, string text, bool expected)
    {
        Assert.Equal(expected, TextEntry(column, op, text).IsMet(RisingCandleData(3), Apple));
    }

    // A field with no value never matches, whatever the operator (even NotEqual / DoesNotContain).
    [Theory]
    [InlineData(ComparisonOperator.Contains)]
    [InlineData(ComparisonOperator.DoesNotContain)]
    [InlineData(ComparisonOperator.Equal)]
    [InlineData(ComparisonOperator.NotEqual)]
    public void Text_MissingField_IsFalseForEveryOperator(ComparisonOperator op)
    {
        foreach (string column in new[] { "Sector", "Industry", "Region", "Name", "Tag" })
        {
            Assert.False(TextEntry(column, op, "Energy").IsMet(RisingCandleData(3), default), column);
            var whitespace = new TickerMetadata("X", " ", " ", " ", " ", "USD") { Tag = "  " };
            Assert.False(TextEntry(column, op, "Energy").IsMet(RisingCandleData(3), whitespace), column);
        }
    }

    [Theory]
    [InlineData(ComparisonOperator.Contains)]
    [InlineData(ComparisonOperator.DoesNotContain)]
    [InlineData(ComparisonOperator.Equal)]
    [InlineData(ComparisonOperator.NotEqual)]
    public void Text_EmptyTargetText_IsFalse(ComparisonOperator op)
    {
        Assert.False(TextEntry("Sector", op, "  ").IsMet(RisingCandleData(3), Apple));
    }

    [Fact]
    public void Text_UnsupportedOperator_IsFalse()
    {
        Assert.False(TextEntry("Sector", ComparisonOperator.GreaterThan, "Technology").IsMet(RisingCandleData(3), Apple));
    }

    // A text operator in a numeric comparison has no meaning: it must not fall back to ">".
    [Fact]
    public void Numeric_TextOperator_IsFalse()
    {
        var entry = new ScreenerIndicatorEntry
        {
            LeftHand = PriceClose(),
            Operator = ComparisonOperator.Contains,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 1m
        };

        Assert.False(entry.IsMet(RisingCandleData(3)));
    }

    [Fact]
    public void FormattedParameters_OmitsBooleanFlags()
    {
        var config = new ScreenerIndicatorSideConfig
        {
            IndicatorType = IndicatorType.RSI,
            Parameters = new Dictionary<string, object>
            {
                { "Period", 14 },
                { "IsSmoothed", false },
                { "UseFilter", true }
            }
        };

        Assert.Equal("(14)", config.FormattedParameters);
        Assert.Equal("RSI(14)", config.GetColumnHeaderTitle());
    }
}

