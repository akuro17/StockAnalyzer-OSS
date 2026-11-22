using System;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// A vendor-suffixed symbol (a continuous future such as <c>CL=F</c> or a currency pair such as <c>JPY=X</c>) that Add Ticker has just registered is selected in the chart right after the add
/// (<c>TickerListViewModel.AddTickerToMaster</c> sets the selected ticker). The chart must keep it; a symbol that fails the chart's
/// format check is reverted to the previous symbol, which looks like "the ticker could not be registered".
/// </summary>
public class ChartViewModelVendorSuffixedSymbolTests : IDisposable
{
    // Must differ from the symbol the view model starts with (MockStockAnalyzerSettings.DefaultSymbol is "AAPL"): assigning the
    // starting symbol is a no-op, so the "previous symbol" a rejected input reverts to would never be recorded.
    private const string OrdinarySymbol = "MSFT";
    private readonly ChartViewModel _sut;

    public ChartViewModelVendorSuffixedSymbolTests()
    {
        _sut = CreateChartViewModel();
    }

    /// <summary>The chart view model every symbol-format test class of this folder uses (mock services, synchronous dispatcher).</summary>
    internal static ChartViewModel CreateChartViewModel()
    {
        var settingsManager = new StockAnalyzer.Avalonia.Services.MockChartSettingsManager();
        settingsManager.UpdatePreview(new StockAnalyzer.Core.Models.Settings.GlobalChartSettings { IsSubWindowVisible = false });

        return new ChartViewModel(
            new StockAnalyzer.Avalonia.Services.MockDataService(),
            new StockAnalyzer.Avalonia.Services.DialogService(),
            null!, // strategyFactory
            new StockAnalyzer.Avalonia.Services.MockStockAnalyzerSettings(),
            new StockAnalyzer.Core.Services.TimeFrameManager(new StockAnalyzer.Avalonia.Services.MockDataService()),
            null!, // marketStructureService
            new StockAnalyzer.Core.Theme.ThemeManager(),
            settingsManager,
            new SynchronousDispatcherService(),
            null,  // predictionService
            null!, // analysisPipelineService
            null,  // marketDataProvider
            null,  // pythonService
            null,  // comparisonDataAligner
            messenger: new CommunityToolkit.Mvvm.Messaging.StrongReferenceMessenger(),
            drawingRepository: new ChartDrawingRepository());
    }

    public void Dispose() => _sut.Dispose();

    [Theory]
    [InlineData("CL=F", "CL=F")]
    [InlineData("GC=F", "GC=F")]
    [InlineData("HG=F", "HG=F")]
    [InlineData("cl=f", "CL=F")]
    [InlineData("JPY=X", "JPY=X")]
    [InlineData("EURUSD=X", "EURUSD=X")]
    [InlineData("eurusd=x", "EURUSD=X")]
    public async Task Symbol_VendorSuffixedSymbol_IsKeptAndUpperCased(string typed, string expected)
    {
        _sut.Symbol = OrdinarySymbol;
        await ChartViewModelTestWaits.AwaitLoadsAsync(_sut);

        _sut.Symbol = typed;
        await ChartViewModelTestWaits.AwaitLoadsAsync(_sut);

        Assert.Equal(expected, _sut.Symbol);
    }

    [Theory]
    [InlineData("A=B=F")]
    [InlineData("=F")]
    [InlineData("CL=")]
    [InlineData("CL==F")]
    [InlineData("CL=Y")]
    [InlineData("6E=X")]
    [InlineData("USD1=X")]
    [InlineData("USD-JPY=X")]
    [InlineData("=X")]
    public async Task Symbol_MalformedEqualsSymbol_RevertsToThePreviousSymbol(string typed)
    {
        _sut.Symbol = OrdinarySymbol;
        await ChartViewModelTestWaits.AwaitLoadsAsync(_sut);

        _sut.Symbol = typed;
        await ChartViewModelTestWaits.AwaitLoadsAsync(_sut);

        Assert.Equal(OrdinarySymbol, _sut.Symbol);
    }

    // The chart's length bound (TickerSymbolLimits.ChartMaxLength = 15) also applies to the vendor-suffixed forms; these rows pin the wiring of that constant.
    [Theory]
    [InlineData("AAAAAAAAAAAAA=F", true)]    // 13 + 2 = 15 = the bound
    [InlineData("AAAAAAAAAAAAAA=F", false)]  // 14 + 2 = 16
    [InlineData("AAAAAAAAAAAAA=X", true)]
    [InlineData("AAAAAAAAAAAAAA=X", false)]
    public async Task Symbol_VendorSuffixedSymbol_FollowsTheChartLengthBound(string typed, bool accepted)
    {
        _sut.Symbol = OrdinarySymbol;
        await ChartViewModelTestWaits.AwaitLoadsAsync(_sut);

        _sut.Symbol = typed;
        await ChartViewModelTestWaits.AwaitLoadsAsync(_sut);

        Assert.Equal(accepted ? typed : OrdinarySymbol, _sut.Symbol);
    }

    [Theory]
    [InlineData("AAPL")]
    [InlineData("BRK-B")]
    [InlineData("7203-T")]
    public async Task Symbol_OrdinarySymbols_StayAccepted(string typed)
    {
        _sut.Symbol = OrdinarySymbol;
        await ChartViewModelTestWaits.AwaitLoadsAsync(_sut);

        _sut.Symbol = typed;
        await ChartViewModelTestWaits.AwaitLoadsAsync(_sut);

        Assert.Equal(typed, _sut.Symbol);
    }
}
