using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views;
using StockAnalyzer.Core.Analysis;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views;

public sealed class SeasonalityChartTabResolutionTests
{
    [AvaloniaFact]
    public void ViewLocator_ResolvesSeasonalityChartViewModel_ToSeasonalityChartView()
    {
        var locator = new ViewLocator();

        var view = locator.Build(new SeasonalityChartViewModel(new StubDataSource()));

        Assert.IsType<SeasonalityChartView>(view);
    }

    private sealed class StubDataSource : ISeasonalityChartDataSource
    {
        public SeasonalityChartResult? Current => null;

        public string? ActiveSymbol => null;

        public bool HasActiveSymbol => false;

        public System.Collections.Generic.IReadOnlyDictionary<int, SeasonalityRadiusMode> RadiusModeOverrides
            => new System.Collections.Generic.Dictionary<int, SeasonalityRadiusMode>();

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public void SetActiveSymbol(string? symbol)
        {
        }

        public void SetRadiusModeOverrides(System.Collections.Generic.IReadOnlyDictionary<int, SeasonalityRadiusMode>? overridesBySeriesId)
        {
        }

        public Task AnalyzeAsync(SeasonalityChartParameters parameters, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
