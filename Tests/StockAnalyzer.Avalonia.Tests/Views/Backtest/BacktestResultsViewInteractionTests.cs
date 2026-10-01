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
        var folderChoice = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new Mock<IDialogService>();
        dialog.Setup(service => service.ShowOpenFolderDialogAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(folderChoice.Task);
        BacktestReport? exportedReport = null;
        var exporter = new Mock<IBacktestReportExporter>();
        exporter.Setup(service => service.ExportAsync(
                It.IsAny<BacktestReport>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback<BacktestReport, string, string>((report, _, _) => exportedReport = report)
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
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == viewModel.ExecutionModeDescription && text.IsVisible);

            export.Command!.Execute(null);
            Assert.False(viewModel.ExportReportCommand.ExecutionTask!.IsCompleted);

            (BacktestResult secondResult, BacktestReport secondReport) = MakeRun(90m);
            viewModel.Update(secondResult, secondReport, 0);
            Render();
            folderChoice.SetResult(System.IO.Path.GetTempPath());
            await viewModel.ExportReportCommand.ExecutionTask!;

            Assert.Same(firstReport, exportedReport);
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
    public void PublishedRun_ShowsSixMetricGroupsEachWithAHeadingAndNoExpander()
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

            // The culture is pinned to "en" by the constructor, so each heading is the localized text of its group key (never the bracketed missing-key marker).
            string[] expectedHeadings = groups.ItemsSource!.Cast<BacktestMetricGroupPresentation>()
                .Select(group => LocalizationManager.Instance.Get(group.GroupKey))
                .ToArray();
            Assert.DoesNotContain(expectedHeadings, heading => heading.StartsWith('['));
            string?[] headings = groups.GetVisualDescendants().OfType<TextBlock>()
                .Where(text => text.Classes.Contains("ColumnHeader"))
                .Select(text => text.Text)
                .ToArray();
            Assert.Equal(expectedHeadings, headings);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PublishedRun_ShowsTheFiveItemSummaryAboveTheMetricsAndHidesItBeforeAResult()
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
            Assert.False(summary.GetVisualAncestors().OfType<Border>().First().IsVisible);

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

    private static void Render()
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
