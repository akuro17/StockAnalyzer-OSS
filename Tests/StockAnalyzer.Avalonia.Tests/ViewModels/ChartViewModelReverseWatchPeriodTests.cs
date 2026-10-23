using StockAnalyzer.Core.Services;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// The Reverse Watch period of the settings page must reach the chart (it was never copied from the saved settings), and the
/// single default is the chart constant, so a missing or new setting starts at the same period as the chart.
/// </summary>
public sealed class ChartViewModelReverseWatchPeriodTests
{
    [Fact]
    public void TheSavedReverseWatchPeriodReachesTheChartWhenTheSettingsChange()
    {
        var settings = new FakeChartSettingsManager();
        ChartViewModel chart = CreateChart(settings);

        settings.UpdatePreview(settings.Current with { ReverseWatchPeriod = 30 });

        Assert.Equal(30, chart.ReverseWatchPeriod);
    }

    [Fact]
    public void TheChartStartsAtTheChartConstantAndANewSettingsRecordUsesTheSameValue()
    {
        var settings = new FakeChartSettingsManager();
        ChartViewModel chart = CreateChart(settings);

        Assert.Equal(ChartConstants.DefaultReverseWatchPeriod, chart.ReverseWatchPeriod);
        Assert.Equal(ChartConstants.DefaultReverseWatchPeriod, new GlobalChartSettings().ReverseWatchPeriod);
        Assert.Equal(25, ChartConstants.DefaultReverseWatchPeriod);
    }

    private static ChartViewModel CreateChart(FakeChartSettingsManager settings)
    {
        var data = new MockDataService();
        return new ChartViewModel(
            data,
            new DialogService(),
            null!,
            new MockStockAnalyzerSettings(),
            new TimeFrameManager(data),
            null!,
            new ThemeManager(),
            settings,
            new SynchronousDispatcherService(),
            null,
            null!,
            messenger: new StrongReferenceMessenger());
    }
}
