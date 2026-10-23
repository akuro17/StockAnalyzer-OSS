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
}
