using System.Linq;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Avalonia.Views.Backtest;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Avalonia.Tests.Views.Backtest;

/// <summary>
/// Mirrors BacktestWindow's real structure (TabControl whose Results tab hosts BacktestResultsView with
/// DataContext="{Binding Results}"): a Run completes while another tab is selected, so the Results view is
/// detached from the visual tree when the presentation is published.
/// </summary>
public class EquityCurveTabSwitchTests
{
    private readonly ITestOutputHelper _output;

    public EquityCurveTabSwitchTests(ITestOutputHelper output) => _output = output;

    private sealed class Holder
    {
        public BacktestResultsViewModel Results { get; init; } = null!;
    }

    private static void Render()
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void RunPublishedWhileResultsTabIsHidden_CurveMatchesLatestRunAfterTabIsShown()
    {
        var viewModel = new BacktestResultsViewModel(
            NullLocalizationService.Instance,
            new Mock<IBacktestReportExporter>().Object,
            new Mock<IDialogService>().Object);
        var view = new BacktestResultsView();
        view.Bind(Control.DataContextProperty, new Binding("Results"));
        var configTab = new TabItem { Header = "Config", Content = new TextBlock { Text = "config" } };
        var resultsTab = new TabItem { Header = "Results", Content = view };
        var tabs = new TabControl { Items = { configTab, resultsTab } };
        var window = new Window { Width = 1100, Height = 900, Content = tabs, DataContext = new Holder { Results = viewModel } };

        try
        {
            window.Show();
            Render();

            // Run 1 finishes while the user is looking at the Config tab (Results tab never shown yet).
            (var firstResult, var firstReport) = BacktestResultsViewInteractionTests.MakeRun(110m);
            viewModel.Update(firstResult, firstReport, 0);
            Render();
            tabs.SelectedItem = resultsTab;
            Render();
            EquityCurveControl curve = Assert.Single(view.GetVisualDescendants().OfType<EquityCurveControl>());
            _output.WriteLine($"after run1 shown: builds={curve.SnapshotBuildCount} rev={curve.ResultRevision} pts={curve.EquityPoints.Length}");
            Assert.Equal(firstResult.EquityPoints, curve.EquityPoints);

            // User goes back to edit signals, Run 2 completes while Results tab is hidden.
            tabs.SelectedItem = configTab;
            Render();
            (var secondResult, var secondReport) = BacktestResultsViewInteractionTests.MakeRun(90m);
            viewModel.Update(secondResult, secondReport, 0);
            Render();
            tabs.SelectedItem = resultsTab;
            Render();

            _output.WriteLine($"after run2 shown: builds={curve.SnapshotBuildCount} rev={curve.ResultRevision} pts={curve.EquityPoints.Length}");
            Assert.Equal(viewModel.ResultRevision, curve.ResultRevision);
            Assert.Equal(secondResult.EquityPoints, curve.EquityPoints);
            Assert.Equal(secondResult.EquityPoints, curve.RenderedEquityPoints);
        }
        finally
        {
            window.Close();
        }
    }
}
