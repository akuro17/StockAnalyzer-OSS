using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public sealed class TrainingWizardManifestTests
{
    [Fact]
    public void MissingManifestReason_AppearsOnlyForWeeklyOrMonthlyIndicatorComposition()
    {
        using var wizard = new TrainingWizardViewModel();
        wizard.FeaturePicker.Channels.Add(new FeatureChannelRowViewModel
        {
            Kind = FeatureChannelKind.Indicator,
            Indicator = IndicatorType.SMA,
        });
        Assert.False(wizard.IsManifestMissing);
        wizard.SelectedTimeframe = TrainingTimeframe.Weekly;
        Assert.True(wizard.IsManifestMissing);
        wizard.SourceManifestPath = "  ";
        Assert.True(wizard.IsManifestMissing);
        wizard.SourceManifestPath = "provider.json";
        Assert.False(wizard.IsManifestMissing);
        wizard.SourceManifestPath = null;
        wizard.SelectedTimeframe = TrainingTimeframe.Monthly;
        Assert.True(wizard.IsManifestMissing);
        wizard.FeaturePicker.Channels.Clear();
        wizard.FeaturePicker.Channels.Add(new FeatureChannelRowViewModel
        {
            Kind = FeatureChannelKind.Price,
            Price = PriceType.Close,
        });
        Assert.False(wizard.IsManifestMissing);
    }

    [Fact]
    public async Task CompletedRun_IgnoresQueuedProgressWhileExperimentLogIsSaving()
    {
        var orchestrator = new Mock<ITrainingOrchestrator>();
        var log = new Mock<IExperimentLogService>();
        var market = new Mock<IMarketDataProvider>();
        market.Setup(provider => provider.GetAvailableTickersAsync())
            .ReturnsAsync(new List<string> { "AAA" });
        IProgress<TrainingProgress>? capturedProgress = null;
        orchestrator.Setup(service => service.StartTrainingAsync(
                It.IsAny<TrainingJobConfig>(), It.IsAny<IProgress<TrainingProgress>>(),
                It.IsAny<CancellationToken>()))
            .Returns((TrainingJobConfig config, IProgress<TrainingProgress> progress, CancellationToken token) =>
            {
                capturedProgress = progress;
                return Task.FromResult(new TrainingRunResult { RunId = "test-run", Success = true });
            });
        var pendingLog = new TaskCompletionSource();
        var logStarted = false;
        log.Setup(service => service.RecordAsync(It.IsAny<TrainingJobConfig>(),
                It.IsAny<TrainingRunResult>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                logStarted = true;
                return pendingLog.Task;
            });
        using var wizard = new TrainingWizardViewModel(
            orchestrator: orchestrator.Object, experimentLogService: log.Object,
            marketDataProvider: market.Object);
        wizard.Horizon = 1;
        wizard.SelectedTimeframe = TrainingTimeframe.Weekly;
        wizard.SourceManifestPath = "manifest.json";
        wizard.FeaturePicker.Channels.Add(new FeatureChannelRowViewModel
        {
            Kind = FeatureChannelKind.Indicator,
            Indicator = IndicatorType.SMA,
        });
        Assert.True(wizard.StartTrainingCommand.CanExecute(null));

        var previousContext = SynchronizationContext.Current;
        var queuedContext = new QueuedSynchronizationContext();
        Task running;
        try
        {
            SynchronizationContext.SetSynchronizationContext(queuedContext);
            running = wizard.StartTrainingCommand.ExecuteAsync(null);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        Assert.True(logStarted);
        Assert.Equal("Completed", wizard.CurrentStage);
        Assert.False(wizard.CancelTrainingCommand.CanExecute(null));
        wizard.CancelTrainingCommand.Execute(null);
        Assert.Equal("Completed", wizard.CurrentStage);
        Assert.NotNull(capturedProgress);
        capturedProgress.Report(new TrainingProgress { Stage = "LateStage", Percent = 93 });
        queuedContext.Drain();
        Assert.Equal("Completed", wizard.CurrentStage);
        Assert.Equal(0, wizard.ProgressPercent);

        pendingLog.SetResult();
        queuedContext.Drain();
        await running;
        Assert.False(wizard.IsTraining);
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _posted = new();
        public override void Post(SendOrPostCallback callback, object? state) => _posted.Enqueue((callback, state));
        public void Drain()
        {
            while (_posted.TryDequeue(out var item)) item.Callback(item.State);
        }
    }
}
