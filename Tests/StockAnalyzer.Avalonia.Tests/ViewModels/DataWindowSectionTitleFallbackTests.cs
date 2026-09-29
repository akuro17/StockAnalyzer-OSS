using System;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Interfaces;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// Guards the "Drawings" section-title fallback in <see cref="DataWindowViewModel"/>: when the
/// <c>DataWindow_Section_Drawings</c> key is absent from every locale, <c>ILocalizationService.GetString</c>
/// returns the bracketed debug sentinel <c>"[DataWindow_Section_Drawings]"</c> (see
/// <c>LocalizationManager.Get</c>) - NOT the bare key. The constructor guard must recognise that
/// sentinel as "unresolved" and fall back to the English label, instead of binding the raw
/// <c>[DataWindow_Section_Drawings]</c> text into the Data tab (the originally reported bug).
/// </summary>
public class DataWindowSectionTitleFallbackTests
{
    private sealed class StubLocalizationService : ILocalizationService
    {
        private readonly Func<string, string> _resolve;
        public StubLocalizationService(Func<string, string> resolve) => _resolve = resolve;
        public string GetString(string key) => _resolve(key);
        public string this[string key] => _resolve(key);
    }

    private static ChartViewModel NewChartViewModel(IMessenger messenger) => new(
        new StockAnalyzer.Avalonia.Services.MockDataService(),
        new StockAnalyzer.Avalonia.Services.DialogService(),
        null!, // strategyFactory
        new StockAnalyzer.Avalonia.Services.MockStockAnalyzerSettings(),
        new StockAnalyzer.Avalonia.Services.TimeFrameManager(new StockAnalyzer.Avalonia.Services.MockDataService()),
        null!, // marketStructureService
        new StockAnalyzer.Core.Theme.ThemeManager(),
        new StockAnalyzer.Avalonia.Services.MockChartSettingsManager(),
        new SynchronousDispatcherService(),
        null,  // predictionService
        null!, // analysisPipelineService
        null,  // marketDataProvider
        null,  // pythonService
        null,  // comparisonDataAligner
        messenger: messenger);

    private static DataWindowViewModel NewSut(Func<string, string> resolve)
    {
        var messenger = new StrongReferenceMessenger();
        return new DataWindowViewModel(
            NewChartViewModel(messenger),
            messenger,
            new SynchronousDispatcherService(),
            new StubLocalizationService(resolve));
    }

    [Fact]
    public void DrawingSectionTitle_FallsBackToEnglishLabel_WhenKeyResolvesToBracketSentinel()
    {
        var sut = NewSut(key => "[" + key + "]"); // every key missing from every locale

        Assert.Equal("Drawing Tools", sut.DrawingSectionTitle);
    }

    [Fact]
    public void DrawingSectionTitle_UsesLocalizedValue_WhenKeyResolves()
    {
        var sut = NewSut(key => key == "DataWindow_Section_Drawings" ? "描画ツール" : "[" + key + "]");

        Assert.Equal("描画ツール", sut.DrawingSectionTitle);
    }
}
