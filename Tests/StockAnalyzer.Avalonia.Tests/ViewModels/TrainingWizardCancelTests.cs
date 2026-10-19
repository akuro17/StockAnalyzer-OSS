using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>Cancel is available for the whole time a run is active - the first run of a window and every later run of the same window.</summary>
[Collection("LocalizationSharedState")]
public sealed class TrainingWizardCancelTests
{
    private sealed class Runs
    {
        public readonly List<TaskCompletionSource<TrainingRunResult>> Pending = new();
        public readonly List<CancellationToken> Tokens = new();
        public int Count => Tokens.Count;
    }

    /// <summary>An orchestrator whose every run stays active until the test completes it (or the wizard cancels it).</summary>
    private static (TrainingWizardViewModel Wizard, Runs Runs) Create()
    {
        var runs = new Runs();
        var orchestrator = new Mock<ITrainingOrchestrator>();
        orchestrator.Setup(o => o.StartTrainingAsync(It.IsAny<TrainingJobConfig>(), It.IsAny<IProgress<TrainingProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((TrainingJobConfig _, IProgress<TrainingProgress> _, CancellationToken token) =>
            {
                var source = new TaskCompletionSource<TrainingRunResult>();
                runs.Pending.Add(source);
                runs.Tokens.Add(token);
                token.Register(() => source.TrySetCanceled(token));
                return source.Task;
            });
        var market = new Mock<IMarketDataProvider>();
        market.Setup(p => p.GetAvailableTickersAsync()).ReturnsAsync(new List<string> { "AAA" });
        var store = StockAnalyzer.Avalonia.Tests.TestHelpers.FakeTickerStateStore.FromProfiles(Array.Empty<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile>());
        return (new TrainingWizardViewModel(orchestrator: orchestrator.Object, marketDataProvider: market.Object, tickerStore: store), runs);
    }

    [Fact]
    public async Task CancelIsEnabledOnlyWhileARunIsActive_AndStopsTheFirstRun()
    {
        var (wizard, runs) = Create();
        using (wizard)
        {
            Assert.False(wizard.CancelTrainingCommand.CanExecute(null), "nothing to cancel before a run");
            var run = wizard.StartTrainingCommand.ExecuteAsync(null);
            Assert.True(wizard.IsTraining);
            Assert.True(wizard.CancelTrainingCommand.CanExecute(null), "the first run can be cancelled");
            wizard.CancelTrainingCommand.Execute(null);
            Assert.True(runs.Tokens[0].IsCancellationRequested);
            await run;
            Assert.False(wizard.IsTraining);
            Assert.False(wizard.CancelTrainingCommand.CanExecute(null), "nothing to cancel after the run");
        }
    }

    [Fact]
    public async Task CancelIsEnabledAgainWhenTheSameWindowStartsAnotherRun()
    {
        var (wizard, runs) = Create();
        using (wizard)
        {
            var first = wizard.StartTrainingCommand.ExecuteAsync(null);
            runs.Pending[0].SetResult(new TrainingRunResult { RunId = "run-1", Success = true });
            await first;
            Assert.False(wizard.IsTraining);

            // A bound button shows the result of the last CanExecuteChanged notification, not of a fresh CanExecute call.
            bool shownToTheButton = false;
            wizard.CancelTrainingCommand.CanExecuteChanged += (_, _) => shownToTheButton = wizard.CancelTrainingCommand.CanExecute(null);
            var second = wizard.StartTrainingCommand.ExecuteAsync(null);
            Assert.Equal(2, runs.Count);
            Assert.True(wizard.IsTraining);
            Assert.True(wizard.CancelTrainingCommand.CanExecute(null), "the second run can be cancelled");
            Assert.True(shownToTheButton, "the Cancel button is told that the second run can be cancelled");
            wizard.CancelTrainingCommand.Execute(null);
            Assert.True(runs.Tokens[1].IsCancellationRequested);
            await second;
            Assert.False(wizard.CancelTrainingCommand.CanExecute(null));
        }
    }

    [Fact]
    public async Task CancelIsEnabledForARunStartedAfterACancelledRun_AndAfterAFailedRun()
    {
        var (wizard, runs) = Create();
        using (wizard)
        {
            var cancelled = wizard.StartTrainingCommand.ExecuteAsync(null);
            wizard.CancelTrainingCommand.Execute(null);
            await cancelled;

            var failed = wizard.StartTrainingCommand.ExecuteAsync(null);
            Assert.True(wizard.CancelTrainingCommand.CanExecute(null), "a run after a cancelled run can be cancelled");
            runs.Pending[1].SetException(new InvalidOperationException("the trainer failed"));
            await failed;
            Assert.False(wizard.IsTraining);

            var third = wizard.StartTrainingCommand.ExecuteAsync(null);
            Assert.True(wizard.CancelTrainingCommand.CanExecute(null), "a run after a failed run can be cancelled");
            wizard.CancelTrainingCommand.Execute(null);
            await third;
        }
    }

    [AvaloniaFact]
    public async Task TheCancelButtonOfTheWindowIsEnabledDuringEveryRunOfTheWindow()
    {
        var (wizard, runs) = Create();
        var window = new TrainingWizardWindow { DataContext = wizard };
        try
        {
            window.Show();
            Pump();
            var cancel = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, wizard.CancelTrainingCommand));
            Assert.False(cancel.IsEffectivelyEnabled);
            for (int run = 0; run < 3; run++)
            {
                var active = wizard.StartTrainingCommand.ExecuteAsync(null);
                Pump();
                Assert.True(cancel.IsEffectivelyEnabled, $"run {run + 1}: the Cancel button is enabled while the run is active");
                runs.Pending[run].SetResult(new TrainingRunResult { RunId = "run-" + run, Success = true });
                await active;
                Pump();
                Assert.False(cancel.IsEffectivelyEnabled, $"run {run + 1}: the Cancel button is disabled after the run");
            }
        }
        finally { window.Close(); wizard.Dispose(); }
    }

    private static void Pump()
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
