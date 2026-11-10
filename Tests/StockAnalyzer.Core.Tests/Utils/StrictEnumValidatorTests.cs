using System;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.UI;
using StockAnalyzer.Core.Utils;

namespace StockAnalyzer.Core.Tests.Utils;

public class StrictEnumValidatorTests
{
    [Theory]
    [InlineData("SMA", IndicatorType.SMA)]
    [InlineData("RSI", IndicatorType.RSI)]
    [InlineData("BB", IndicatorType.BB)]
    [InlineData("BollingerBandsRatio", IndicatorType.BollingerBandsRatio)]
    [InlineData("alma", IndicatorType.ALMA)]
    [InlineData("macd", IndicatorType.MACD)]
    public void TryParseStrict_ValidNames_ReturnsTrueAndCorrectEnum(string input, IndicatorType expected)
    {
        bool success = StrictEnumValidator.TryParseStrict<IndicatorType>(input, out var result, ignoreCase: true);
        Assert.True(success);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("14")]
    [InlineData("-1")]
    [InlineData("+2")]
    [InlineData("999")]
    public void TryParseStrict_NumericStrings_AlwaysRejected(string input)
    {
        bool success = StrictEnumValidator.TryParseStrict<IndicatorType>(input, out var result, ignoreCase: true);
        Assert.False(success);
        Assert.Equal(default, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NonExistentIndicatorTypeXYZ")]
    [InlineData("14SMA")]
    public void TryParseStrict_InvalidOrWhitespace_ReturnsFalse(string? input)
    {
        bool success = StrictEnumValidator.TryParseStrict<IndicatorType>(input, out var result, ignoreCase: true);
        Assert.False(success);
        Assert.Equal(default, result);
    }

    [Fact]
    public void TryParseStrict_CaseSensitive_HonorsFlag()
    {
        bool upperSuccess = StrictEnumValidator.TryParseStrict<IndicatorType>("SMA", out var upperResult, ignoreCase: false);
        Assert.True(upperSuccess);
        Assert.Equal(IndicatorType.SMA, upperResult);

        bool lowerSuccess = StrictEnumValidator.TryParseStrict<IndicatorType>("sma", out var lowerResult, ignoreCase: false);
        Assert.False(lowerSuccess);
        Assert.Equal(default, lowerResult);
    }

    [Theory]
    [InlineData("Left", PanelRegion.Left)]
    [InlineData("right", PanelRegion.Right)]
    [InlineData("Top", PanelRegion.Top)]
    [InlineData("0", null)]
    [InlineData("-5", null)]
    public void TryParseStrict_OtherEnumType_BehavesConsistently(string input, PanelRegion? expected)
    {
        bool success = StrictEnumValidator.TryParseStrict<PanelRegion>(input, out var result, ignoreCase: true);
        if (expected.HasValue)
        {
            Assert.True(success);
            Assert.Equal(expected.Value, result);
        }
        else
        {
            Assert.False(success);
            Assert.Equal(default, result);
        }
    }
}
