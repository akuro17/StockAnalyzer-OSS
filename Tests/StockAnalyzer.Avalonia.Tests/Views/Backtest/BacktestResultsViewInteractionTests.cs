using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Moq;
using StockAnalyzer.Avalonia.Converters;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views.Backtest;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Backtest;

// The group headings read the shared static LocalizationManager.Instance (see LocalizationSharedStateCollection.cs).
[Collection("LocalizationSharedState")]
public class BacktestResultsViewInteractionTests
{
    private static readonly DateTime Start = new(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);

    public BacktestResultsViewInteractionTests()
    {
        LocalizationManager.Instance.Initialize("en");
    }

    [AvaloniaFact]
    public async Task PublishedRun_UpdatesVisibleModeMetricsCurveAndExportsTheSelectedArtifact()
    {
        var fileChoice = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new Mock<IDialogService>();
        dialog.Setup(service => service.ShowSaveFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]?>(), It.IsAny<string?>()))
            .Returns(fileChoice.Task);
        IReadOnlyList<BacktestTrade>? exportedTrades = null;
        var exporter = new Mock<IBacktestReportExporter>();
        exporter.Setup(service => service.ExportTradesCsvAsync(
                It.IsAny<IReadOnlyList<BacktestTrade>>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback<IReadOnlyList<BacktestTrade>, string, string>((trades, _, _) => exportedTrades = trades)
            .Returns(Task.CompletedTask);
        var viewModel = new BacktestResultsViewModel(
            NullLocalizationService.Instance,
            exporter.Object,
            dialog.Object);
        var view = new BacktestResultsView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1100, Height = 900 };

        try
        {
            window.Show();
            Render();

            Assert.False(viewModel.HasResult);
            (BacktestResult firstResult, BacktestReport firstReport) = MakeRun(110m);
            viewModel.Update(firstResult, firstReport, 0);
            Render();

            EquityCurveControl curve = Assert.Single(view.GetVisualDescendants().OfType<EquityCurveControl>());
            ItemsControl metrics = Assert.Single(view.GetVisualDescendants().OfType<ItemsControl>()
                .Where(control => control.ItemsSource?.Cast<object>().FirstOrDefault() is BacktestMetricGroupPresentation));
            Button export = Assert.Single(view.GetVisualDescendants().OfType<Button>()
                .Where(button => ReferenceEquals(button.Command, viewModel.ExportReportCommand)));

            Assert.True(viewModel.HasResult);
            Assert.True(export.IsEnabled);
            Assert.Equal(ExpectedGroupedMetricRowCount, metrics.ItemsSource!.Cast<BacktestMetricGroupPresentation>().Sum(group => group.Rows.Length));
            Assert.Equal(firstResult.EquityPoints, curve.EquityPoints);
            Assert.Equal(viewModel.ResultRevision, curve.ResultRevision);

            export.Command!.Execute(null);
            Assert.False(viewModel.ExportReportCommand.ExecutionTask!.IsCompleted);

            (BacktestResult secondResult, BacktestReport secondReport) = MakeRun(90m);
            viewModel.Update(secondResult, secondReport, 0);
            Render();
            fileChoice.SetResult(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "test_trades.csv"));
            await viewModel.ExportReportCommand.ExecutionTask!;

            Assert.Equal(firstResult.Trades, exportedTrades);
            Assert.Same(secondResult, viewModel.Presentation!.Result);
            Assert.Same(secondReport, viewModel.Presentation.Report);
            Assert.Equal(secondResult.EquityPoints, curve.EquityPoints);
            Assert.Equal(viewModel.ResultRevision, curve.ResultRevision);
            Assert.Equal(ExpectedGroupedMetricRowCount, metrics.ItemsSource!.Cast<BacktestMetricGroupPresentation>().Sum(group => group.Rows.Length));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PublishedRun_ShowsSixMetricGroupsUnifiedWithoutCategoryHeadingsAndNoExpander()
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
            (BacktestResult result, BacktestReport report) = MakeRun(110m);
            viewModel.Update(result, report, 0);
            Render();

            ItemsControl groups = Assert.Single(view.GetVisualDescendants().OfType<ItemsControl>()
                .Where(control => control.ItemsSource?.Cast<object>().FirstOrDefault() is BacktestMetricGroupPresentation));

            Assert.Equal(6, groups.ItemsSource!.Cast<BacktestMetricGroupPresentation>().Count());
            // Every group is shown the same way: there is no collapsible list inside the metrics section.
            Assert.Empty(groups.GetVisualDescendants().OfType<Expander>());

            // Per user request, category header banners inside the metrics table are unified into one table (no separate group headings).
            Assert.Empty(groups.GetVisualDescendants().OfType<TextBlock>()
                .Where(text => text.Classes.Contains("ColumnHeader")));

            // All metric display rows across all 6 groups are rendered in the unified table.
            int totalRenderedRows = groups.GetVisualDescendants().OfType<Grid>()
                .Count(grid => grid.DataContext is BacktestMetricDisplayRow && grid.ColumnDefinitions.Count == 2);
            Assert.Equal(ExpectedGroupedMetricRowCount, totalRenderedRows);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PublishedRun_ShowsTheFiveItemSummaryAboveTheMetricsBothBeforeAndAfterAResult()
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
            Render();
            ItemsControl summary = Assert.Single(view.GetVisualDescendants().OfType<ItemsControl>()
                .Where(control => global::Avalonia.Automation.AutomationProperties.GetAutomationId(control) == "Backtest_Results_Summary"));
            Assert.True(summary.GetVisualAncestors().OfType<Border>().First().IsVisible);
            Assert.Equal(5, summary.ItemsSource!.Cast<BacktestMetricDisplayRow>().Count());
            Assert.All(summary.ItemsSource!.Cast<BacktestMetricDisplayRow>(), row => Assert.Equal("-", row.ValueText));

            ItemsControl initialGroups = Assert.Single(view.GetVisualDescendants().OfType<ItemsControl>()
                .Where(control => control.ItemsSource?.Cast<object>().FirstOrDefault() is BacktestMetricGroupPresentation));
            Assert.Equal(6, initialGroups.ItemsSource!.Cast<BacktestMetricGroupPresentation>().Count());

            (BacktestResult result, BacktestReport report) = MakeRun(110m);
            viewModel.Update(result, report, 0);
            Render();

            Assert.True(summary.GetVisualAncestors().OfType<Border>().First().IsVisible);
            Assert.Equal(5, summary.ItemsSource!.Cast<BacktestMetricDisplayRow>().Count());
            Assert.Equal(viewModel.Summary, summary.ItemsSource!.Cast<BacktestMetricDisplayRow>());
            ItemsControl groups = Assert.Single(view.GetVisualDescendants().OfType<ItemsControl>()
                .Where(control => control.ItemsSource?.Cast<object>().FirstOrDefault() is BacktestMetricGroupPresentation));
            Assert.Equal(6, groups.ItemsSource!.Cast<BacktestMetricGroupPresentation>().Count());
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The 18 fixed metrics, the 15 extended metrics and Closed Trades of a generated report, shown in six groups.</summary>
    private const int ExpectedGroupedMetricRowCount = 34;

    internal static (BacktestResult Result, BacktestReport Report) MakeRun(decimal finalEquity)
    {
        var configuration = new BacktestConfiguration
        {
            InitialCapital = 100m,
            SizingModel = PositionSizingModel.FixedQuantity,
            SizingParameter = 1m,
        };
        var points = ImmutableArray.Create(
            new EquityPoint(0, Start, 100m, 100m, 0m, 0m),
            new EquityPoint(1, Start.AddDays(1), finalEquity, finalEquity, 0m, 0m));
        var result = new BacktestResult(
            ImmutableArray<BacktestOrder>.Empty,
            ImmutableArray<BacktestFill>.Empty,
            ImmutableArray<BacktestTrade>.Empty,
            points,
            ImmutableArray<BacktestSignal>.Empty,
            configuration,
            RunStatus.Completed,
            "ResultExperienceAcceptance",
            new byte[32],
            false);
        var options = new BacktestReportOptions(TimeFrame.D1, 0, Start, Start.AddDays(2))
        {
            AnnualPeriods = 252,
        };
        return (result, new BacktestReportGenerator().Generate(result, options));
    }

    [AvaloniaFact]
    public void ColumnResize_ReflectsInRowGrids()
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
            var (baseline, _) = MakeRun(110m);
            var trades = ImmutableArray.Create(new BacktestTrade(1, TradeSide.Long,
                0, Start, 100m, 1, Start.AddDays(1), 110m,
                1m, 10m, 10m, 0m, 0m, 1, false));
            var result = new BacktestResult(baseline.Orders, baseline.Fills, trades,
                baseline.EquityPoints, baseline.Signals, baseline.Configuration, baseline.Status,
                baseline.StrategyName, baseline.ReproducibilityHash, baseline.IsInsufficientData);
            var report = new BacktestReportGenerator().Generate(result,
                new BacktestReportOptions(TimeFrame.D1, 0, Start, Start.AddDays(2)) { AnnualPeriods = 252 });
            viewModel.Update(result, report, 0);
            Render();

            var tradesHeader = view.FindControl<Grid>("TradesHeaderGrid")!;
            var tradeItems = view.GetVisualDescendants().OfType<ItemsControl>()
                .Single(c => global::Avalonia.Automation.AutomationProperties.GetAutomationId(c) == "Backtest_Results_Trades");
            var firstRowGrid = tradeItems.GetVisualDescendants().OfType<Grid>()
                .First(g => g.DataContext is BacktestTradeRow && g.ColumnDefinitions.Count == BacktestTradeHistoryColumnChooserViewModel.KnownColumns.Count);

            double initialHeaderCol0 = tradesHeader.ColumnDefinitions[0].ActualWidth;
            double initialRowCol0 = firstRowGrid.ColumnDefinitions[0].ActualWidth;
            Assert.Equal(initialHeaderCol0, initialRowCol0);

            var colDef = tradesHeader.ColumnDefinitions[0];

            // Test expanding via colDef.Width
            colDef.Width = new GridLength(initialHeaderCol0 + 50);
            Render();
            double expandedHeaderCol0 = tradesHeader.ColumnDefinitions[0].ActualWidth;
            double expandedRowCol0 = firstRowGrid.ColumnDefinitions[0].ActualWidth;
            Assert.Equal(initialHeaderCol0 + 50, expandedHeaderCol0);
            Assert.Equal(initialHeaderCol0 + 50, expandedRowCol0);

            // Test shrinking via colDef.Width
            colDef.Width = new GridLength(initialHeaderCol0 + 10);
            Render();
            double shrunkHeaderCol0 = tradesHeader.ColumnDefinitions[0].ActualWidth;
            double shrunkRowCol0 = firstRowGrid.ColumnDefinitions[0].ActualWidth;
            Assert.Equal(initialHeaderCol0 + 10, shrunkHeaderCol0);
            Assert.Equal(initialHeaderCol0 + 10, shrunkRowCol0);

            // Test Metrics column resize
            var metricsHeader = view.FindControl<Grid>("MetricsHeaderGrid")!;
            var metricsGroups = view.GetVisualDescendants().OfType<ItemsControl>()
                .Single(c => global::Avalonia.Automation.AutomationProperties.GetAutomationId(c) == "Backtest_Results_Metrics");
            var firstMetricRowGrid = metricsGroups.GetVisualDescendants().OfType<Grid>()
                .First(g => g.DataContext is BacktestMetricDisplayRow && g.ColumnDefinitions.Count == 2);

            double initialMetricCol0 = firstMetricRowGrid.ColumnDefinitions[0].ActualWidth;
            metricsHeader.ColumnDefinitions[0].Width = new GridLength(initialMetricCol0 + 40);
            Render();

            double updatedMetricCol0 = firstMetricRowGrid.ColumnDefinitions[0].ActualWidth;
            Assert.Equal(initialMetricCol0 + 40, updatedMetricCol0);

            // Verify Metrics Value column is compact and decoupled from fingerprint width (<= 300 under NullLocalizationService key names, whereas fingerprint was 587)
            double initialMetricCol1 = firstMetricRowGrid.ColumnDefinitions[1].ActualWidth;
            Assert.True(initialMetricCol1 <= 300, $"Metric value column was unexpectedly wide: {initialMetricCol1}");
            metricsHeader.ColumnDefinitions[1].Width = new GridLength(initialMetricCol1 + 25);
            Render();
            double updatedMetricCol1 = firstMetricRowGrid.ColumnDefinitions[1].ActualWidth;
            Assert.Equal(initialMetricCol1 + 25, updatedMetricCol1);

            // Verify top border line exists directly above the metrics table
            var metricsScroll = view.GetVisualDescendants().OfType<ScrollViewer>()
                .Single(c => global::Avalonia.Automation.AutomationProperties.GetAutomationId(c) == "Backtest_Results_MetricsScroll");
            var metricsStack = Assert.IsType<StackPanel>(metricsScroll.Content);
            int metricsIndex = metricsStack.Children.IndexOf(metricsGroups);
            Assert.True(metricsIndex > 0);
            var topBorder = Assert.IsType<Border>(metricsStack.Children[metricsIndex - 1]);
            Assert.Equal(1, topBorder.Height);

            // Verify Trades column 8 has expected min width
            Assert.True(tradesHeader.ColumnDefinitions[8].ActualWidth >= 110);
        }
        finally
        {
            window.Close();
        }
    }

    private static void Render()
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}

