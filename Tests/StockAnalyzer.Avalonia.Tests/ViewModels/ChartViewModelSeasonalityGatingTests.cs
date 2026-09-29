using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// SAで修正: the Seasonality Chart tab must stay idle until the user actually picks a symbol, so a
/// freshly opened / workspace-restored (incl. detached-window) Seasonality Chart does not auto-run a
/// data-less analysis for the startup default symbol. ChartViewModel now pushes the active symbol to
/// <see cref="ISeasonalityChartDataSource"/> from <c>OnSymbolChanged</c> (a real symbol change) and
/// never from <c>OnCandlesChanged</c> (which also fires for the startup default-symbol auto-load).
/// </summary>
public sealed class ChartViewModelSeasonalityGatingTests
{
    private static ChartViewModel BuildChart(RecordingSeasonalityDataSource seasonality)
    {
        var settingsManager = new MockChartSettingsManager();
        return new ChartViewModel(
            new MockDataService(),
            new DialogService(),
            null!,
            new MockStockAnalyzerSettings(),
            new TimeFrameManager(new MockDataService()),
            null!,
            new StockAnalyzer.Core.Theme.ThemeManager(),
            settingsManager,
            new SynchronousDispatcherService(),
            null,
            null!,
            null,
            null,
            null,
            messenger: new StrongReferenceMessenger(),
            seasonalityChartDataSource: seasonality);
    }

    [Fact]
    public void CandleLoadAlone_DoesNotTouchTheSeasonalitySource()
    {
        var seasonality = new RecordingSeasonalityDataSource();
        var chart = BuildChart(seasonality);

        chart.Candles = new[]
        {
            new CoreCandleData(new DateTime(2024, 1, 2), 10m, 11m, 9m, 10.5m, 100),
            new CoreCandleData(new DateTime(2024, 1, 3), 10.5m, 12m, 10m, 11.5m, 120),
        };
        chart.Candles = Array.Empty<CoreCandleData>();

        Assert.Empty(seasonality.SetSymbolCalls);
        Assert.Null(seasonality.ActiveSymbol);
    }

    [Fact]
    public void ExplicitSymbolChange_PropagatesToTheSeasonalitySource()
    {
        var seasonality = new RecordingSeasonalityDataSource();
        var chart = BuildChart(seasonality);

        chart.Symbol = "GOOG"; // differs from MockStockAnalyzerSettings.DefaultSymbol ("AAPL")

        Assert.Equal("GOOG", seasonality.ActiveSymbol);
        Assert.Contains("GOOG", seasonality.SetSymbolCalls);
    }

    private sealed class RecordingSeasonalityDataSource : ISeasonalityChartDataSource
    {
        public List<string?> SetSymbolCalls { get; } = new();

        public SeasonalityChartResult? Current { get; private set; }

        public string? ActiveSymbol { get; private set; }

        public bool HasActiveSymbol => !string.IsNullOrWhiteSpace(ActiveSymbol);

        public IReadOnlyDictionary<int, SeasonalityRadiusMode> RadiusModeOverrides { get; } =
            new Dictionary<int, SeasonalityRadiusMode>();

        public event EventHandler? Changed;

        public void SetActiveSymbol(string? symbol)
        {
            SetSymbolCalls.Add(symbol);
            ActiveSymbol = string.IsNullOrWhiteSpace(symbol) ? null : symbol;
            Current = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void SetRadiusModeOverrides(IReadOnlyDictionary<int, SeasonalityRadiusMode>? overridesBySeriesId) { }

        public Task AnalyzeAsync(SeasonalityChartParameters parameters, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
