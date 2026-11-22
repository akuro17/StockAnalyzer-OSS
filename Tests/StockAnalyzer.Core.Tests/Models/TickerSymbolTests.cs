using System;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

public class TickerSymbolTests
{
    // The expression that ParquetMarketDataProvider used inline at five places before TickerSymbol.Canonicalize replaced it.
    private static string Original(string s) => s.Trim().ToUpperInvariant().Replace('.', '-');

    [Theory]
    [InlineData("msft")]
    [InlineData("  msft  ")]
    [InlineData("7203.t")]
    [InlineData("BRK.B")]
    [InlineData("brk-b")]
    [InlineData("^gspc")]
    [InlineData("a.b.c")]
    [InlineData("ı.i")]
    [InlineData("")]
    public void Canonicalize_EqualsTheExpressionItReplaced(string input)
    {
        Assert.Equal(Original(input), TickerSymbol.Canonicalize(input));
    }

    [Theory]
    [InlineData("MSFT")]
    [InlineData("7203.T")]
    [InlineData("^N225")]
    [InlineData("JPY=X")]
    [InlineData(" msft ")]
    public void IsWellFormed_AcceptsOrdinarySymbols(string input)
    {
        Assert.True(TickerSymbol.IsWellFormed(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../x")]
    [InlineData("..\\x")]
    [InlineData("a..b")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("C:\\x")]
    [InlineData("C:x")]
    [InlineData("A\0B")]
    [InlineData("A\nB")]
    public void IsWellFormed_RejectsPathLikeAndControlInput(string? input)
    {
        Assert.False(TickerSymbol.IsWellFormed(input));
    }

    [Fact]
    public void IsWellFormed_AppliesTheLengthBoundAfterTrimming()
    {
        Assert.True(TickerSymbol.IsWellFormed(new string('A', TickerSymbol.MaxLength)));
        Assert.True(TickerSymbol.IsWellFormed(" " + new string('A', TickerSymbol.MaxLength) + " "));
        Assert.False(TickerSymbol.IsWellFormed(new string('A', TickerSymbol.MaxLength + 1)));
    }

    private const int LayerBound = 15;

    // The boundary table of the plan (section 4.1), one row per line.
    [Theory]
    [InlineData("CL=F", LayerBound, true)]
    [InlineData("cl=f", LayerBound, true)]
    [InlineData("GC=F", LayerBound, true)]
    [InlineData("HG=F", LayerBound, true)]
    [InlineData("6E=F", LayerBound, true)]
    [InlineData("C=F", LayerBound, true)]
    [InlineData("", LayerBound, false)]
    [InlineData("=F", LayerBound, false)]
    [InlineData("CL=", LayerBound, false)]
    [InlineData("CL=X", LayerBound, false)]
    [InlineData("CL==F", LayerBound, false)]
    [InlineData("CL=F=F", LayerBound, false)]
    [InlineData("A=B=F", LayerBound, false)]
    [InlineData("CL-F", LayerBound, false)]
    [InlineData("CL=FX", LayerBound, false)]
    [InlineData("C L=F", LayerBound, false)]
    [InlineData("BRK-B=F", LayerBound, false)]
    [InlineData("CL.X=F", LayerBound, false)]
    [InlineData("^CL=F", LayerBound, false)]
    [InlineData("AAAAAAAAAAAAA=F", LayerBound, true)]   // 13 + 2 = 15 = the bound
    [InlineData("AAAAAAAAAAAAAA=F", LayerBound, false)] // 14 + 2 = 16 > the bound
    [InlineData("CL=F", 2, false)]                          // a bound below the shortest legal symbol
    [InlineData("CL=F", 3, false)]                          // the bound is shorter than the value
    [InlineData("CL=F", 4, true)]                           // the bound equals the value length
    [InlineData("\uFF23\uFF2C=F", LayerBound, false)]     // full-width letters are not ASCII
    [InlineData("CL=\uFF26", LayerBound, false)]          // full-width suffix
    [InlineData("\uFF11\uFF12=F", LayerBound, false)]     // full-width digits
    public void IsContinuousFuturesFormat_FollowsTheBoundaryTable(string value, int maxLength, bool expected)
    {
        Assert.Equal(expected, TickerSymbol.IsContinuousFuturesFormat(value, maxLength));
    }

    [Fact]
    public void IsContinuousFuturesFormat_IsFuturesOnly_AndDoesNotTouchTheOtherMembers()
    {
        // The currency-pair class is NOT accepted by this function (a separate function covers it when it is added).
        Assert.False(TickerSymbol.IsContinuousFuturesFormat("EURUSD=X", LayerBound));
        Assert.Equal('F', TickerSymbol.ContinuousFuturesSuffix);

        // Unchanged contracts of the existing members.
        Assert.Equal("CL=F", TickerSymbol.Canonicalize(" cl=f "));
        Assert.True(TickerSymbol.IsWellFormed("CL=F"));
    }

    // Currency pairs (owner decision D-1 of the currency-pair plan): ROOT = ASCII letters only, no length rule beyond the layer bound.
    [Theory]
    [InlineData("JPY=X", LayerBound, true)]
    [InlineData("EURUSD=X", LayerBound, true)]
    [InlineData("eurusd=x", LayerBound, true)]
    [InlineData("A=X", LayerBound, true)]
    [InlineData("CL=X", LayerBound, true)]                // a letters-only root is accepted whatever the vendor knows
    [InlineData("", LayerBound, false)]
    [InlineData("=X", LayerBound, false)]                 // empty root
    [InlineData("EURUSD=", LayerBound, false)]
    [InlineData("EURUSD==X", LayerBound, false)]
    [InlineData("EURUSD=X=X", LayerBound, false)]
    [InlineData("EURUSD=F", LayerBound, false)]           // the futures suffix is not a pair
    [InlineData("EURUSD=XX", LayerBound, false)]
    [InlineData("EURUSD-X", LayerBound, false)]
    [InlineData("6E=X", LayerBound, false)]               // a digit in the root
    [InlineData("USD1=X", LayerBound, false)]
    [InlineData("USD-JPY=X", LayerBound, false)]          // a hyphen in the root
    [InlineData("USD JPY=X", LayerBound, false)]
    [InlineData("^EURUSD=X", LayerBound, false)]
    [InlineData("\uFF2A\uFF30\uFF39=X", LayerBound, false)] // full-width letters
    [InlineData("JPY=\uFF38", LayerBound, false)]           // full-width suffix
    [InlineData("AAAAAAAAAAAAA=X", LayerBound, true)]   // 13 + 2 = 15 = the bound
    [InlineData("AAAAAAAAAAAAAA=X", LayerBound, false)] // 14 + 2 = 16 > the bound
    [InlineData("JPY=X", 2, false)]
    [InlineData("JPY=X", 4, false)]                         // the bound is shorter than the value
    [InlineData("JPY=X", 5, true)]                          // the bound equals the value length
    [InlineData("A=X", 3, true)]                            // the shortest legal pair symbol
    public void IsForexPairFormat_FollowsTheBoundaryTable(string value, int maxLength, bool expected)
    {
        Assert.Equal(expected, TickerSymbol.IsForexPairFormat(value, maxLength));
    }

    [Theory]
    [InlineData("CL=F", true)]
    [InlineData("6E=F", true)]
    [InlineData("JPY=X", true)]
    [InlineData("EURUSD=X", true)]
    [InlineData("CL=Y", false)]
    [InlineData("6E=X", false)]       // digits: futures root with the pair suffix
    [InlineData("=F", false)]
    [InlineData("=X", false)]
    [InlineData("AAPL", false)]       // plain symbols are the regexes' business, not the vendor-suffix function's
    [InlineData("BRK-B", false)]
    public void IsVendorSuffixedSymbolFormat_IsTheUnionOfTheTwoClasses(string value, bool expected)
    {
        Assert.Equal(expected, TickerSymbol.IsVendorSuffixedSymbolFormat(value, LayerBound));
    }

    [Fact]
    public void TheTwoClassFunctionsDoNotAcceptEachOthersSymbols()
    {
        Assert.False(TickerSymbol.IsContinuousFuturesFormat("JPY=X", LayerBound));
        Assert.False(TickerSymbol.IsContinuousFuturesFormat("EURUSD=X", LayerBound));
        Assert.False(TickerSymbol.IsForexPairFormat("CL=F", LayerBound));
        Assert.False(TickerSymbol.IsForexPairFormat("6E=F", LayerBound));
        Assert.Equal('X', TickerSymbol.ForexPairSuffix);
        Assert.Equal('F', TickerSymbol.ContinuousFuturesSuffix); // the futures function and its suffix stay futures-only
    }
}
