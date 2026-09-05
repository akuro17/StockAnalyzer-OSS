using StockAnalyzer.Core.Common;
using Xunit;

namespace StockAnalyzer.Core.Tests.Common;

public class SymbolHelperTests
{
    [Theory]
    [InlineData("AAPL", "AAPL", true)]
    [InlineData("aapl", "AAPL", true)]
    [InlineData("^GSPC", "GSPC", true)]
    [InlineData("GSPC", "^GSPC", true)]
    [InlineData("^gspc", "GSPC", true)]
    [InlineData("7203.T", "7203-T", true)]
    [InlineData("7203-t", "7203.T", true)]
    [InlineData("^N225", "N225", true)]
    [InlineData("AAPL", "MSFT", false)]
    [InlineData("", "AAPL", false)]
    [InlineData(null, "AAPL", false)]
    [InlineData("AAPL", null, false)]
    [InlineData("  ", "  ", false)]
    public void IsSameSymbol_EvaluatesAliasesCorrectly(string? a, string? b, bool expected)
    {
        var result = SymbolHelper.IsSameSymbol(a, b);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("aapl", "AAPL")]
    [InlineData("  ^GSPC  ", "GSPC")]
    [InlineData("7203.t", "7203-T")]
    [InlineData("^7203.T", "7203-T")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizeSymbol_StandardizesFormat(string? input, string expected)
    {
        var result = SymbolHelper.NormalizeSymbol(input!);
        Assert.Equal(expected, result);
    }
}
