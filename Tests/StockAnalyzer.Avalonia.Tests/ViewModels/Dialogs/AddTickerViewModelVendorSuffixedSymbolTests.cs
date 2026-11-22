using System;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels.Dialogs;

/// <summary>
/// The Add Ticker dialog's own state machine for vendor-suffixed symbols (<c>CL=F</c>, <c>GC=F</c>, <c>HG=F</c>, <c>JPY=X</c>, <c>EURUSD=X</c>): typing one reaches
/// <see cref="ValidationState.NewTicker"/>, the Add command becomes executable, and the dialog hands back the canonical symbol.
/// </summary>
public class AddTickerViewModelVendorSuffixedSymbolTests
{
    private static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(10);

    internal static AddTickerViewModel Create() => new(
        new MockMarketDataProvider(),
        new TickerImportService(),
        new WatchlistManager(),
        new SynchronousDispatcherService());

    internal static async Task<ValidationState> SettledStateAsync(AddTickerViewModel viewModel)
    {
        var deadline = DateTime.UtcNow + StateTimeout;
        while (viewModel.State == ValidationState.Searching && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        return viewModel.State;
    }

    [Theory]
    [InlineData("CL=F", "CL=F")]
    [InlineData("gc=f", "GC=F")]
    [InlineData("HG=F", "HG=F")]
    [InlineData("jpy=x", "JPY=X")]
    [InlineData("EURUSD=X", "EURUSD=X")]
    public async Task TypingAVendorSuffixedSymbol_ReachesNewTickerAndReturnsTheCanonicalSymbol(string typed, string expected)
    {
        using var viewModel = Create();

        viewModel.SearchText = typed;
        var state = await SettledStateAsync(viewModel);

        Assert.Equal(ValidationState.NewTicker, state);
        Assert.True(viewModel.AddCommand.CanExecute(null));

        viewModel.AddCommand.Execute(null);
        Assert.Equal(expected, viewModel.ResultSymbol);
    }
}
