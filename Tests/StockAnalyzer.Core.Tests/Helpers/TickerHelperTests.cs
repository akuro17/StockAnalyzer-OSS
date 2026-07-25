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
}
