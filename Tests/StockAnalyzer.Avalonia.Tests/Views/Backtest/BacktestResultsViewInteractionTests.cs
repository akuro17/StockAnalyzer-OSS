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
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Avalonia.Views.Backtest;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Backtest;

public class BacktestResultsViewInteractionTests
{
    private static readonly DateTime Start = new(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);

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
                .Where(control => control.ItemsSource?.Cast<object>().FirstOrDefault() is BacktestMetricRow));
            Button export = Assert.Single(view.GetVisualDescendants().OfType<Button>()
                .Where(button => ReferenceEquals(button.Command, viewModel.ExportReportCommand)));

            Assert.True(viewModel.HasResult);
            Assert.True(export.IsEnabled);
            Assert.Equal(18, metrics.ItemsSource!.Cast<BacktestMetricRow>().Count());
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
            Assert.Equal(18, metrics.ItemsSource!.Cast<BacktestMetricRow>().Count());
        }
        finally
        {
            window.Close();
        }
    }

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
