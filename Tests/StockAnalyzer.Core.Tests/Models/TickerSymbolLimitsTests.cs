using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

/// <summary>The one symbol-format contract: the per-layer bounds (values unchanged by the consolidation) and the shared character rules.</summary>
public class TickerSymbolLimitsTests
{
    // Non-ASCII look-alikes are built from code points so that no non-ASCII character appears in this file.
    private static string Chr(int codePoint) => ((char)codePoint).ToString();

    [Fact]
    public void LayerBounds_KeepTheirHistoricalValues()
    {
        Assert.Equal(10, TickerSymbolLimits.DialogMaxLength);
        Assert.Equal(15, TickerSymbolLimits.ChartMaxLength);
        Assert.Equal(16, TickerSymbolLimits.ImportMaxLength);
        Assert.Equal(32, TickerSymbolLimits.FileSafetyMaxLength);
    }

    [Fact]
    public void TheFileSafetyBoundNameForwardsToTheContract()
    {
        Assert.Equal(TickerSymbolLimits.FileSafetyMaxLength, TickerSymbol.MaxLength);
    }

    [Theory]
    [InlineData("A", true)]
    [InlineData("aapl", true)]
    [InlineData("AAPL", true)]
    [InlineData("BRK-B", true)]
    [InlineData("BRK.B", true)]
    [InlineData("7203-T", true)]
    [InlineData("12345", true)]
    [InlineData("", false)]
    [InlineData("A_B", false)]
    [InlineData("A B", false)]
    [InlineData("A/B", false)]
    [InlineData("A=B", false)]
    [InlineData("^GSPC", false)]
    [InlineData("CL=F", false)]       // vendor-suffixed forms belong to IsVendorSuffixedSymbolFormat, not to the plain form
    public void IsPlainSymbolFormat_AcceptsAsciiLettersDigitsHyphenAndDot(string value, bool expected)
    {
        Assert.Equal(expected, TickerSymbol.IsPlainSymbolFormat(value, TickerSymbolLimits.ImportMaxLength));
    }

    [Theory]
    [InlineData(0x212A)]   // KELVIN SIGN
    [InlineData(0x0130)]   // LATIN CAPITAL LETTER I WITH DOT ABOVE
    [InlineData(0x0131)]   // LATIN SMALL LETTER DOTLESS I
    [InlineData(0x017F)]   // LATIN SMALL LETTER LONG S
    [InlineData(0xFF21)]   // FULLWIDTH LATIN CAPITAL LETTER A
    [InlineData(0xFF17)]   // FULLWIDTH DIGIT SEVEN
    [InlineData(0x0663)]   // ARABIC-INDIC DIGIT THREE
    public void IsPlainSymbolFormat_RefusesNonAsciiCharacters(int codePoint)
    {
        Assert.False(TickerSymbol.IsPlainSymbolFormat(Chr(codePoint), TickerSymbolLimits.FileSafetyMaxLength));
        Assert.False(TickerSymbol.IsPlainSymbolFormat("A" + Chr(codePoint), TickerSymbolLimits.FileSafetyMaxLength));
        Assert.False(TickerSymbol.IsAcceptedSymbolFormat("A" + Chr(codePoint), TickerSymbolLimits.FileSafetyMaxLength));
    }

    [Theory]
    [InlineData(TickerSymbolLimits.DialogMaxLength)]
    [InlineData(TickerSymbolLimits.ChartMaxLength)]
    [InlineData(TickerSymbolLimits.ImportMaxLength)]
    [InlineData(TickerSymbolLimits.FileSafetyMaxLength)]
    public void IsPlainSymbolFormat_FollowsTheGivenBoundOnBothSides(int bound)
    {
        Assert.True(TickerSymbol.IsPlainSymbolFormat(new string('A', bound), bound));
        Assert.False(TickerSymbol.IsPlainSymbolFormat(new string('A', bound + 1), bound));
        Assert.False(TickerSymbol.IsPlainSymbolFormat("A", 0));
    }

    [Theory]
    [InlineData("AAPL", true)]
    [InlineData("BRK-B", true)]
    [InlineData("CL=F", true)]
    [InlineData("cl=f", true)]
    [InlineData("JPY=X", true)]
    [InlineData("EURUSD=X", true)]
    [InlineData("CL=Y", false)]
    [InlineData("6E=X", false)]
    [InlineData("A=B=F", false)]
    [InlineData("=F", false)]
    [InlineData("A_B", false)]
    public void IsAcceptedSymbolFormat_IsPlainOrVendorSuffixed(string value, bool expected)
    {
        Assert.Equal(expected, TickerSymbol.IsAcceptedSymbolFormat(value, TickerSymbolLimits.ImportMaxLength));
    }
}
