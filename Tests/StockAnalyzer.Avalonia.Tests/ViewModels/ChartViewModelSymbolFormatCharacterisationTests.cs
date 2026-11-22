using System;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.ViewModels;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// Contract of the chart's symbol check for plain (non vendor-suffixed) symbols: ASCII letters, digits, '-' and '.' up to the chart bound, upper-cased and kept as typed
/// otherwise; a symbol that fails the check reverts to the previous symbol. The rows were first recorded on the pre-consolidation code, then the non-ASCII rows were
/// flipped to refusals by the ASCII-only fix. Non-ASCII inputs are written as unicode escapes only.
/// </summary>
public class ChartViewModelSymbolFormatCharacterisationTests : IDisposable
{
    // Must differ from the symbol the view model starts with ("AAPL"); see ChartViewModelVendorSuffixedSymbolTests.
    private const string BaseSymbol = "MSFT";
    private const int ChartBound = StockAnalyzer.Core.Models.TickerSymbolLimits.ChartMaxLength;

    private readonly ChartViewModel _sut = ChartViewModelVendorSuffixedSymbolTests.CreateChartViewModel();

    public void Dispose() => _sut.Dispose();

    private async Task<string> SymbolAfterTypingAsync(string typed)
    {
        _sut.Symbol = BaseSymbol;
        await ChartViewModelTestWaits.AwaitLoadsAsync(_sut);

        _sut.Symbol = typed;
        await ChartViewModelTestWaits.AwaitLoadsAsync(_sut);

        return _sut.Symbol;
    }

    [Theory]
    [InlineData("AAPL", "AAPL")]
    [InlineData("aapl", "AAPL")]
    [InlineData("brk.b", "BRK.B")]   // the chart upper-cases only: a dot stays a dot
    [InlineData("BRK-B", "BRK-B")]
    [InlineData("7203", "7203")]     // no Japanese suffix is added here
    [InlineData("7203-T", "7203-T")]
    [InlineData("A", "A")]
    [InlineData("  AAPL  ", "AAPL")] // trimmed
    public async Task PlainSymbol_IsAcceptedAndUpperCased(string typed, string expected)
    {
        Assert.Equal(expected, await SymbolAfterTypingAsync(typed));
    }

    [Theory]
    [InlineData(ChartBound - 1, true)]
    [InlineData(ChartBound, true)]
    [InlineData(ChartBound + 1, false)]
    public async Task PlainSymbol_FollowsTheChartLengthBound(int length, bool accepted)
    {
        string typed = new('A', length);

        Assert.Equal(accepted ? typed : BaseSymbol, await SymbolAfterTypingAsync(typed));
    }

    [Theory]
    [InlineData("A_B")]
    [InlineData("A B")]
    [InlineData("A@B")]
    [InlineData("^GSPC")]
    [InlineData("\uFF21\uFF22")]   // full-width letters
    [InlineData("\u0131")]         // LATIN SMALL LETTER DOTLESS I
    [InlineData("\u017F")]         // LATIN SMALL LETTER LONG S
    [InlineData("A\u017F")]
    [InlineData("\u212A")]         // KELVIN SIGN (a case-insensitive pattern would match it as K)
    [InlineData("A\u212A")]
    [InlineData("\u0130")]         // LATIN CAPITAL LETTER I WITH DOT ABOVE
    [InlineData("A\u0130")]
    public async Task OtherCharacters_RevertToThePreviousSymbol(string typed)
    {
        Assert.Equal(BaseSymbol, await SymbolAfterTypingAsync(typed));
    }
}
