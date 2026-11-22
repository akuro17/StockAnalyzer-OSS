using System;
using StockAnalyzer.Core.Helpers;
using Xunit;

namespace StockAnalyzer.Core.Tests.Helpers;

public class TickerHelperTests
{
    [Theory]
    [InlineData("7203", true)]
    [InlineData("9984", true)]
    [InlineData("1000", true)]
    [InlineData("AAPL", false)]
    [InlineData("7203-T", false)]
    [InlineData("123", false)]
    [InlineData("12345", false)]
    [InlineData("", false)]
    public void IsFourDigitJapaneseCode_ValidatesCorrectly(string input, bool expected)
    {
        bool result = TickerHelper.IsFourDigitJapaneseCode(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("7203", "7203-T")]
    [InlineData("7203-t", "7203-T")]
    [InlineData("aapl", "AAPL")]
    [InlineData("brk.b", "BRK-B")]
    [InlineData(" 7203 ", "7203-T")]
    public void NormalizeTicker_ReturnsExpectedNormalizedSymbol(string input, string expected)
    {
        string normalized = TickerHelper.NormalizeTicker(input);
        Assert.Equal(expected, normalized);
    }

    // A Japanese stock code is four ASCII digits. Digits of other scripts (full-width, Arabic-Indic) are not codes: the dialog add path used to give them the "-T" suffix.
    // Built from code points so that no non-ASCII character appears in this file.
    [Theory]
    [InlineData(0xFF10)] // FULLWIDTH DIGIT ZERO
    [InlineData(0xFF17)] // FULLWIDTH DIGIT SEVEN
    [InlineData(0x0663)] // ARABIC-INDIC DIGIT THREE
    [InlineData(0x0967)] // DEVANAGARI DIGIT ONE
    public void NonAsciiDigits_AreNotAJapaneseCode_AndGetNoSuffix(int digitCodePoint)
    {
        string digits = new((char)digitCodePoint, 4);

        Assert.False(TickerHelper.IsFourDigitJapaneseCode(digits));
        Assert.False(TickerHelper.IsFourDigitJapaneseCode(digits.AsSpan()));
        Assert.Equal(digits, TickerHelper.NormalizeTicker(digits));
        Assert.False(TickerHelper.IsFourDigitJapaneseCode("72" + new string((char)digitCodePoint, 2)));
    }
}
