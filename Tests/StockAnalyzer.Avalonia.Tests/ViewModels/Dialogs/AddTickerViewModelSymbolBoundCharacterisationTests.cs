using System.Threading.Tasks;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels.Dialogs;

/// <summary>
/// Characterisation of the Add Ticker dialog's symbol check: it applies a LENGTH bound only (no character class), on the trimmed and upper-cased text.
/// Pre-refactoring baseline of the symbol-format contract work; the bound had no test before. Non-ASCII inputs are written as unicode escapes only.
/// </summary>
public class AddTickerViewModelSymbolBoundCharacterisationTests
{
    private const int DialogBound = StockAnalyzer.Core.Models.TickerSymbolLimits.DialogMaxLength;

    private static async Task<ValidationState> StateAfterTypingAsync(string typed)
    {
        using var viewModel = AddTickerViewModelVendorSuffixedSymbolTests.Create();
        viewModel.SearchText = typed;
        return await AddTickerViewModelVendorSuffixedSymbolTests.SettledStateAsync(viewModel);
    }

    [Theory]
    [InlineData(DialogBound - 1, ValidationState.NewTicker)]
    [InlineData(DialogBound, ValidationState.NewTicker)]
    [InlineData(DialogBound + 1, ValidationState.Invalid)]
    public async Task UnknownSymbol_FollowsTheDialogLengthBound(int length, ValidationState expected)
    {
        // "ZQ" + 'Z' padding is not in the mock master list, so the dialog offers it as a new ticker unless the length bound refuses it.
        string typed = "ZQ" + new string('Z', length - 2);

        Assert.Equal(expected, await StateAfterTypingAsync(typed));
    }

    [Fact]
    public async Task TheBoundIsAppliedAfterTrimming()
    {
        string typed = "  ZQZZZZZZZZ  "; // 10 characters plus surrounding blanks

        Assert.Equal(ValidationState.NewTicker, await StateAfterTypingAsync(typed));
    }

    // KNOWN GAP, recorded as-is (owner decision Q2 = A, backlog item "Add Ticker dialog format validation"): the dialog checks no character class, so a non-ASCII text of
    // acceptable length is offered as a new ticker although the chart and the import refuse it later. A dialog format check would flip these rows to Invalid.
    [Theory]
    [InlineData("\u212A")]
    [InlineData("ZQ\uFF21")]
    public async Task ShortNonAsciiText_IsOfferedAsANewTicker_KnownGap(string typed)
    {
        Assert.Equal(ValidationState.NewTicker, await StateAfterTypingAsync(typed));
    }
}
