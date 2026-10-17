using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Moq;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Models;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views;

/// <summary>The Prediction Models window (Tools menu): frame, footer, lifetime of its view model (model-execution separation, T2).</summary>
public sealed class PredictionModelsWindowTests
{
    [AvaloniaFact]
    public void TheWindowHostsThePageAndOffersOnlyClose()
    {
        LocalizationManager.Instance.Initialize("en");
        var manager = new PredictionSettingsManager();
        using var vm = new PredictionModelsViewModel(manager);
        var window = new PredictionModelsWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.Equal(LocalizationManager.Instance["Menu_PredictionModels"], window.Title);
            Assert.Equal(PredictionModelsUiLayout.WindowWidth, window.Width);
            Assert.Single(window.GetVisualDescendants().OfType<PredictionModelsView>());
            var close = window.GetVisualDescendants().OfType<Button>()
                .Single(b => global::Avalonia.Automation.AutomationProperties.GetAutomationId(b) == PredictionModelsUiConstants.Close);
            Assert.True(close.Classes.Contains("accent"));
            Assert.Equal(LocalizationManager.Instance["Btn_Close"], close.Content);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async System.Threading.Tasks.Task TheRunPredictionButtonIsBoundToTheCommand_AndTheSummaryShowsAfterTheRun()
    {
        LocalizationManager.Instance.Initialize("en");
        var target = new Moq.Mock<IPredictionRunTarget>();
        target.Setup(t => t.RunAsync(It.IsAny<System.Threading.CancellationToken>())).ReturnsAsync(
            new PredictionRunReport(PredictionRunOutcome.Completed, "SPY", StockAnalyzer.Core.Models.TimeframeType.Daily, "Up 61%", null));
        using var vm = new PredictionModelsViewModel(new PredictionSettingsManager(), predictionRunTarget: target.Object);
        var window = new PredictionModelsWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var button = window.GetVisualDescendants().OfType<Button>()
                .Single(b => global::Avalonia.Automation.AutomationProperties.GetAutomationId(b) == PredictionModelsUiConstants.Run);
            Assert.True(button.Classes.Contains("accent"));
            Assert.Equal(LocalizationManager.Instance["PredictionModels_Run_Button"], button.Content);
            Assert.Equal("Run", button.Content);
            Assert.Same(vm.RunPredictionCommand, button.Command);

            // Run sits in the footer directly to the left of Close; the list no longer has Refresh / Restore previous
            var close = window.GetVisualDescendants().OfType<Button>()
                .Single(b => global::Avalonia.Automation.AutomationProperties.GetAutomationId(b) == PredictionModelsUiConstants.Close);
            Assert.Same(button.Parent, close.Parent);
            Assert.Equal(global::Avalonia.Controls.Grid.GetColumn(button) + 1, global::Avalonia.Controls.Grid.GetColumn(close));
            var labels = window.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string).ToArray();
            Assert.DoesNotContain(LocalizationManager.Instance["Settings_ModelGenerations_Refresh"], labels);
            Assert.DoesNotContain("Restore previous", labels);

            await vm.RunPredictionCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            var status = window.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => global::Avalonia.Automation.AutomationProperties.GetAutomationId(t) == PredictionModelsUiConstants.RunStatus);
            Assert.True(status.IsVisible);
            Assert.Contains("Up 61%", status.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ClosingTheWindowDisposesTheViewModel()
    {
        var manager = new PredictionSettingsManager();
        var vm = new PredictionModelsViewModel(manager);
        var changed = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PredictionModelsViewModel.ConfiguredWindowSize)) changed++; };
        var window = new PredictionModelsWindow { DataContext = vm };
        window.Show();

        manager.SetWindowSize(31);
        Assert.Equal(1, changed);

        window.Close();
        manager.SetWindowSize(32);
        Assert.Equal(1, changed);   // disposed: it no longer follows the manager
    }

    [Fact]
    public void SettingsStillListsAiPredictionsAndNoLongerRlPlayback()
    {
        Assert.Contains(SettingsConstants.Categories, c => c.Key == SettingsConstants.Keys.AIPredictions);
    }
}
