using System;
using System.Collections.Immutable;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Avalonia.Views.Backtest;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Backtest;

/// <summary>The Results tab feeds the equity chart its trades: the XAML binding, end to end through the real view model.</summary>
// Builds the real view, whose texts read the shared static LocalizationManager.Instance (see LocalizationSharedStateCollection.cs).
[Collection("LocalizationSharedState")]
public class EquityCurveResultsBindingTests
{
    public EquityCurveResultsBindingTests()
    {
        LocalizationManager.Instance.Initialize("en");
    }

    [AvaloniaFact]
    public void PublishedRun_GivesTheChartItsTradesAndPlacesMarkersOnRealEngineTimes()
    {
        var viewModel = new BacktestResultsViewModel(
            NullLocalizationService.Instance,
            Mock.Of<IBacktestReportExporter>(),
            Mock.Of<IDialogService>());
        var view = new BacktestResultsView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1100, Height = 900 };

        try
        {
            window.Show();
            (BacktestResult result, BacktestReport report) = MakeRunWithOneTrade();
            viewModel.Update(result, report, 0);
            for (int i = 0; i < 3; i++)
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
            }

            EquityCurveControl curve = Assert.Single(view.GetVisualDescendants().OfType<EquityCurveControl>());

            Assert.Equal(viewModel.Trades, curve.Trades);
            Assert.NotEmpty(viewModel.Trades);
            // Every trade event whose time is a time of the evaluated equity series is marked, entry and exit alike.
            int expected = viewModel.Trades.Sum(trade =>
                (viewModel.EquityPoints.Any(p => p.Timestamp == trade.EntryTime) ? 1 : 0)
                + (viewModel.EquityPoints.Any(p => p.Timestamp == trade.ExitTime) ? 1 : 0));
            Assert.True(expected > 0, "the fixture run should have at least one trade event on an equity point");
            Assert.Equal(expected, curve.Markers.Length);
            Assert.Equal(2, curve.Markers.Length);
        }
        finally
        {
            window.Close();
        }
    }

    private static readonly DateTime Start = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Three bars, one closed trade entered on bar 0 and exited on bar 2 (both are times of the equity series).</summary>
    private static (BacktestResult Result, BacktestReport Report) MakeRunWithOneTrade()
    {
        var configuration = new BacktestConfiguration
        {
            InitialCapital = 100m,
            SizingModel = PositionSizingModel.FixedQuantity,
            SizingParameter = 1m,
        };
        var points = ImmutableArray.Create(
            new EquityPoint(0, Start, 100m, 100m, 0m, 0m),
            new EquityPoint(1, Start.AddDays(1), 105m, 105m, 0m, 0m),
            new EquityPoint(2, Start.AddDays(2), 110m, 110m, 0m, 0m));
        var trade = new BacktestTrade(
            1, TradeSide.Long, 0, Start, 50m, 2, Start.AddDays(2), 60m, 1m, 10m, 10m, 0m, 0m, 2, false);
        var result = new BacktestResult(
            ImmutableArray<BacktestOrder>.Empty,
            ImmutableArray<BacktestFill>.Empty,
            ImmutableArray.Create(trade),
            points,
            ImmutableArray<BacktestSignal>.Empty,
            configuration,
            RunStatus.Completed,
            "EquityMarkerBinding",
            new byte[32],
            false);
        var options = new BacktestReportOptions(TimeFrame.D1, 0, Start, Start.AddDays(3))
        {
            AnnualPeriods = 252,
        };
        return (result, new BacktestReportGenerator().Generate(result, options));
    }
}
