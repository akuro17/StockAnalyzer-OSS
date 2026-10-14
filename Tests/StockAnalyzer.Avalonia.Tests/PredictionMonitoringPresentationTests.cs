using System.Collections.Immutable;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.Tests.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests;

[Collection("LocalizationSharedState")]
public sealed class PredictionMonitoringPresentationTests : IDisposable
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
    private readonly string _language = LocalizationManager.Instance.CurrentLanguage;
    public PredictionMonitoringPresentationTests() => LocalizationManager.Instance.Initialize("en");
    public void Dispose() => LocalizationManager.Instance.Initialize(_language);
    private static ModelGenerationSummary Generation(string id, string target = "classification") =>
        new(id, "model.onnx", target, "daily", 5, .7, new string('a', 64), false);
    private static AIPredictionsSettingsViewModel Settings(IPredictionLogService log, IRetrainingScheduler? scheduler = null,
        ICurrentPredictionHealthService? currentHealth = null, IAttestedEvidenceDiagnostics? evidenceDiagnostics = null,
        IEnsembleSettingsManager? ensembleSettings = null) =>
        new(Mock.Of<IPredictionSettingsManager>(s => s.WindowSize == 75), Mock.Of<IClipboardService>(),
            Mock.Of<IToastNotificationService>(), analysisDispatcher: new SynchronousDispatcherService(),
            predictionLog: log, retrainingScheduler: scheduler, currentHealth: currentHealth, evidenceDiagnostics: evidenceDiagnostics,
            ensembleSettings: ensembleSettings);

    // ---- the saved ensemble is its own monitoring group ------------------------------------------------------------------

    private static readonly EnsembleSpec SavedEnsemble = FixedEnsembleSettingsManager.SampleSaved;

    private static Mock<ICurrentPredictionHealthService> Health(string id, params ModelMonitoringSnapshot[] snapshots)
    {
        var health = new Mock<ICurrentPredictionHealthService>();
        health.Setup(h => h.EvaluateCurrentAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(ImmutableArray<ModelMonitoringSnapshot>.Empty);
        health.Setup(h => h.EvaluateCurrentAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(snapshots.ToImmutableArray());
        return health;
    }

    [Fact]
    public async Task SavedEnsemble_ShowsItsOwnHealthLine_FromItsMonitoringId()
    {
        var id = SavedEnsemble.MonitoringId();
        var health = Health(id, Snapshot(id, ModelHealthState.Healthy, 30, 16));
        using var vm = Settings(Mock.Of<IPredictionLogService>(), currentHealth: health.Object, ensembleSettings: new FixedEnsembleSettingsManager(SavedEnsemble));
        await vm.MonitoringRefreshTask;

        Assert.StartsWith(string.Format(LocalizationManager.Instance.Get("Monitoring_EnsembleState"), "").TrimEnd(), vm.EnsembleMonitoringStatus);
        Assert.Contains("Healthy", vm.EnsembleMonitoringStatus);
        Assert.Contains("30", vm.EnsembleMonitoringStatus);
        Assert.Contains(string.Format(System.Globalization.CultureInfo.CurrentCulture,
            LocalizationManager.Instance.Get("Monitoring_Accuracy"), LocalizationManager.Instance.Get("Monitoring_Health_Healthy"),
            (16m / 30m).ToString("P2", System.Globalization.CultureInfo.CurrentCulture), 30), vm.EnsembleMonitoringStatus);
        Assert.Null(vm.MonitoringStatus);   // no generation is selected: the generation line is unaffected
        health.Verify(h => h.EvaluateCurrentAsync(id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SavedEnsembleWithoutSnapshots_ShowsTheInsufficientDataText()
    {
        var health = Health(SavedEnsemble.MonitoringId());
        using var vm = Settings(Mock.Of<IPredictionLogService>(), currentHealth: health.Object, ensembleSettings: new FixedEnsembleSettingsManager(SavedEnsemble));
        await vm.MonitoringRefreshTask;

        Assert.Equal(string.Format(LocalizationManager.Instance.Get("Monitoring_EnsembleState"),
            LocalizationManager.Instance.Get("Monitoring_InsufficientData")), vm.EnsembleMonitoringStatus);
    }

    [Fact]
    public async Task NoSavedEnsemble_ShowsNoEnsembleLine_AndWithoutCurrentHealthTheCachedReadIsUsed()
    {
        var id = SavedEnsemble.MonitoringId();
        var health = Health(id, Snapshot(id, ModelHealthState.Healthy, 30, 16));
        using var none = Settings(Mock.Of<IPredictionLogService>(), currentHealth: health.Object, ensembleSettings: new FixedEnsembleSettingsManager(null));
        await none.MonitoringRefreshTask;
        using var plain = Settings(Mock.Of<IPredictionLogService>(), currentHealth: health.Object);
        await plain.MonitoringRefreshTask;
        var log = new Mock<IPredictionLogService>();
        log.Setup(l => l.GetSnapshotsAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(
            ImmutableArray.Create(Snapshot(id, ModelHealthState.Decayed, 30, 14)));
        using var cached = Settings(log.Object, ensembleSettings: new FixedEnsembleSettingsManager(SavedEnsemble));
        await cached.MonitoringRefreshTask;

        Assert.Null(none.EnsembleMonitoringStatus);
        Assert.Null(plain.EnsembleMonitoringStatus);
        health.Verify(h => h.EvaluateCurrentAsync(id, It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains("Decayed", cached.EnsembleMonitoringStatus);
    }

    [Fact]
    public async Task EnsembleFailure_ShowsLoadFailedOnItsOwnLine_AndLeavesTheGenerationStatusAlone()
    {
        var generation = new string('b', 64);
        var health = new Mock<ICurrentPredictionHealthService>();
        health.Setup(h => h.EvaluateCurrentAsync(generation, It.IsAny<CancellationToken>())).ReturnsAsync(
            ImmutableArray.Create(Snapshot(generation, ModelHealthState.Healthy, 30, 16)));
        health.Setup(h => h.EvaluateCurrentAsync(SavedEnsemble.MonitoringId(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidDataException("corrupt ensemble state"));
        using var vm = Settings(Mock.Of<IPredictionLogService>(), currentHealth: health.Object, ensembleSettings: new FixedEnsembleSettingsManager(SavedEnsemble));
        vm.SelectedGeneration = Generation(generation); await vm.MonitoringRefreshTask;

        Assert.Equal(string.Format(LocalizationManager.Instance.Get("Monitoring_EnsembleState"),
            LocalizationManager.Instance.Get("Monitoring_LoadFailed")), vm.EnsembleMonitoringStatus);
        Assert.Contains("Healthy", vm.MonitoringStatus);
        Assert.DoesNotContain(LocalizationManager.Instance.Get("Monitoring_LoadFailed"), vm.MonitoringStatus);
    }

    [Fact]
    public async Task OlderEnsembleCompletion_DoesNotOverwriteTheNewerOne()
    {
        var id = SavedEnsemble.MonitoringId();
        var first = new TaskCompletionSource<ImmutableArray<ModelMonitoringSnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var health = new Mock<ICurrentPredictionHealthService>();
        health.SetupSequence(h => h.EvaluateCurrentAsync(id, It.IsAny<CancellationToken>()))
            .Returns(first.Task)
            .ReturnsAsync(ImmutableArray.Create(Snapshot(id, ModelHealthState.Healthy, 30, 16)));
        health.Setup(h => h.EvaluateCurrentAsync("other", It.IsAny<CancellationToken>())).ReturnsAsync(ImmutableArray<ModelMonitoringSnapshot>.Empty);
        using var vm = Settings(Mock.Of<IPredictionLogService>(), currentHealth: health.Object, ensembleSettings: new FixedEnsembleSettingsManager(SavedEnsemble));
        var earlier = vm.MonitoringRefreshTask;

        vm.SelectedGeneration = Generation("other"); await vm.MonitoringRefreshTask;
        Assert.Contains("Healthy", vm.EnsembleMonitoringStatus);
        first.SetResult(ImmutableArray.Create(Snapshot(id, ModelHealthState.Decayed, 30, 10))); await earlier;

        Assert.Contains("Healthy", vm.EnsembleMonitoringStatus);
        Assert.DoesNotContain("Decayed", vm.EnsembleMonitoringStatus);
    }

    [Fact]
    public async Task SavingAndClearingTheEnsemble_ReadItsHealthAgain()
    {
        var id = SavedEnsemble.MonitoringId();
        var health = Health(id, Snapshot(id, ModelHealthState.Healthy, 30, 16));
        var settings = new FixedEnsembleSettingsManager(null);
        using var vm = Settings(Mock.Of<IPredictionLogService>(), currentHealth: health.Object, ensembleSettings: settings);
        await vm.MonitoringRefreshTask;
        Assert.Null(vm.EnsembleMonitoringStatus);
        foreach (var member in SavedEnsemble.Members) vm.EnsembleMembers.Add(new EnsembleWeightRow(member.ModelId, (decimal)(member.Weight * 100)));

        await vm.SaveEnsembleCommand.ExecuteAsync(null); await vm.MonitoringRefreshTask;
        Assert.Contains("Healthy", vm.EnsembleMonitoringStatus);

        await vm.ClearEnsembleCommand.ExecuteAsync(null); await vm.MonitoringRefreshTask;
        Assert.Null(vm.EnsembleMonitoringStatus);
    }

    [Fact]
    public async Task EnsembleLoadedAfterTheFirstRefresh_TriggersAnotherRefresh()
    {
        var id = SavedEnsemble.MonitoringId();
        var health = Health(id, Snapshot(id, ModelHealthState.Healthy, 30, 16));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = new FixedEnsembleSettingsManager(SavedEnsemble, release.Task);
        using var vm = Settings(Mock.Of<IPredictionLogService>(), currentHealth: health.Object, ensembleSettings: settings);
        await vm.MonitoringRefreshTask;
        Assert.Null(vm.EnsembleMonitoringStatus);                  // nothing is saved as far as the first refresh could see

        release.SetResult();
        // The rows are added before the follow-up refresh is requested, so the rows (or the refresh task) are intermediate states; wait for the outcome itself.
        await ChartViewModelTestWaits.EventuallyAsync(() => Assert.Contains("Healthy", vm.EnsembleMonitoringStatus));

        Assert.Equal(SavedEnsemble.Members.Count, vm.EnsembleMembers.Count);
    }

    [Fact]
    public async Task LoadedEnsemble_ShowsItsHealthLineWithoutAnyUserAction()
    {
        var id = SavedEnsemble.MonitoringId();
        var health = Health(id, Snapshot(id, ModelHealthState.Decayed, 30, 14));
        using var vm = Settings(Mock.Of<IPredictionLogService>(), currentHealth: health.Object, ensembleSettings: new FixedEnsembleSettingsManager(SavedEnsemble));
        await vm.MonitoringRefreshTask;

        Assert.Equal(SavedEnsemble.Members.Count, vm.EnsembleMembers.Count);
        Assert.Contains("Decayed", vm.EnsembleMonitoringStatus);
    }

    [Fact]
    public async Task OlderSelectionAndDisposedCompletions_DoNotOverwriteCurrentMonitoring()
    {
        var first = new TaskCompletionSource<ImmutableArray<ModelMonitoringSnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new Mock<IPredictionLogService>();
        log.Setup(l => l.GetSnapshotsAsync("first", It.IsAny<CancellationToken>())).Returns(first.Task);
        log.Setup(l => l.GetSnapshotsAsync("second", It.IsAny<CancellationToken>())).ReturnsAsync(
            ImmutableArray.Create(new ModelMonitoringSnapshot("second", "target", 30, 15, .5m, ModelHealthState.Healthy, DateTimeOffset.UtcNow)));
        using var vm = Settings(log.Object);
        vm.SelectedGeneration = Generation("first"); var earlier = vm.MonitoringRefreshTask;
        vm.SelectedGeneration = Generation("second"); await vm.MonitoringRefreshTask;
        Assert.Contains("Healthy", vm.MonitoringStatus); Assert.Contains("30", vm.MonitoringStatus);
        first.SetResult(ImmutableArray<ModelMonitoringSnapshot>.Empty); await earlier;
        Assert.Contains("Healthy", vm.MonitoringStatus);
        vm.Dispose(); var before = vm.MonitoringStatus;
        log.Raise(l => l.Changed += null, EventArgs.Empty);
        Assert.Equal(before, vm.MonitoringStatus);
    }

    [Fact]
    public async Task RegressionIsUnavailable_AndEnableDisableCancelUseScheduler()
    {
        var config = new TrainingJobConfig { Symbols = ["X"], Architecture = "lstm", WindowSize = 4, Horizon = 1 };
        var scheduler = new Mock<IRetrainingScheduler>();
        scheduler.SetupGet(s => s.Current).Returns(new RetrainingSchedule(1, "job", config, false,
            new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero), RetrainingStatus.Disabled));
        using var vm = Settings(Mock.Of<IPredictionLogService>(), scheduler.Object);
        vm.SelectedGeneration = Generation("regression", "regression"); await vm.MonitoringRefreshTask;
        Assert.Contains("Regression", vm.MonitoringStatus);
        Assert.True(vm.HasScheduleConfiguration); Assert.False(vm.IsScheduleEnabled);
        await vm.EnableRetrainingCommand.ExecuteAsync(null); await vm.DisableRetrainingCommand.ExecuteAsync(null);
        vm.CancelRetrainingCommand.Execute(null);
        scheduler.Verify(s => s.SetEnabledAsync(true), Times.Once); scheduler.Verify(s => s.SetEnabledAsync(false), Times.Once);
        scheduler.Verify(s => s.CancelCurrent(), Times.Once);
    }

    [Fact]
    public async Task WizardSavesExplicitCurrentConfigurationAndDates_WithoutStartingTraining()
    {
        var market = new Mock<IMarketDataProvider>();
        market.Setup(m => m.GetAvailableTickersAsync()).ReturnsAsync(new[] { "X" });
        var scheduler = new Mock<IRetrainingScheduler>(); var trainer = new Mock<ITrainingOrchestrator>();
        using var vm = new TrainingWizardViewModel(orchestrator: trainer.Object, marketDataProvider: market.Object,
            retrainingScheduler: scheduler.Object)
        {
            StartDate = new DateTime(2026, 1, 1), EndDate = new DateTime(2026, 9, 30),
            WindowSize = 4, Horizon = 1,
        };
        await vm.SaveRetrainingConfigurationCommand.ExecuteAsync(null);
        scheduler.Verify(s => s.SaveConfigurationAsync(It.Is<TrainingJobConfig>(c => c.Symbols.SequenceEqual(new[] { "X" })
            && c.StartDate == new DateOnly(2026, 1, 1) && c.EndDate == new DateOnly(2026, 9, 30)
            && c.WindowSize == 4 && c.Horizon == 1)), Times.Once);
        trainer.Verify(t => t.StartTrainingAsync(It.IsAny<TrainingJobConfig>(), It.IsAny<IProgress<TrainingProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains("disabled", vm.StatusMessage);
    }

    [Fact]
    public async Task ApplicationLifetime_StartsOnce_StopsAfterRunnerCleanup_AndUnsubscribes()
    {
        var scheduler = new Mock<IRetrainingScheduler>(); var log = new Mock<IPredictionLogService>();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.Setup(s => s.RunWithRecoveryAsync(It.IsAny<CancellationToken>())).Returns((CancellationToken ct) => { entered.TrySetResult(ct); return exit.Task; });
        var toast = new Mock<IToastNotificationService>();
        using var lifetime = new PredictionOperationsLifetime(scheduler.Object, log.Object, new SynchronousDispatcherService(),
            toast.Object, NullLogger<PredictionOperationsLifetime>.Instance);
        lifetime.Start(); lifetime.Start(); var token = await entered.Task;
        var stop = lifetime.StopAsync(); Assert.True(token.IsCancellationRequested); Assert.False(stop.IsCompleted);
        log.Raise(l => l.Decayed += null, new ModelDecayAlert("alert", new("model", "target", 30, 10, 1m / 3,
            ModelHealthState.Decayed, DateTimeOffset.UtcNow)));
        toast.Verify(t => t.ShowNotification(It.IsAny<string>()), Times.Never);
        exit.SetResult(); await stop;
        scheduler.Verify(s => s.RunWithRecoveryAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    private static readonly string CurrentModel = new('a', 64);
    private static ModelMonitoringSnapshot Snapshot(string model, ModelHealthState state, int matured, int correct) =>
        new(model, "target", matured, correct, PredictionMonitoringPolicy.ComputeAccuracy(matured, correct), state, DateTimeOffset.UtcNow);

    [Fact]
    public async Task CurrentHealthCapability_ReplacesTheCachedRead()
    {
        var log = new Mock<IPredictionLogService>(); var health = new Mock<ICurrentPredictionHealthService>();
        log.Setup(l => l.GetSnapshotsAsync(CurrentModel, It.IsAny<CancellationToken>())).ReturnsAsync(
            ImmutableArray.Create(Snapshot(CurrentModel, ModelHealthState.Healthy, 30, 16)));
        health.Setup(h => h.EvaluateCurrentAsync(CurrentModel, It.IsAny<CancellationToken>())).ReturnsAsync(
            ImmutableArray.Create(Snapshot(CurrentModel, ModelHealthState.Decayed, 30, 14)));
        using var vm = Settings(log.Object, currentHealth: health.Object);
        vm.SelectedGeneration = Generation(CurrentModel); await vm.MonitoringRefreshTask;
        Assert.Contains("Decayed", vm.MonitoringStatus); Assert.DoesNotContain("Healthy", vm.MonitoringStatus);
        health.Verify(h => h.EvaluateCurrentAsync(CurrentModel, It.IsAny<CancellationToken>()), Times.Once);
        log.Verify(l => l.GetSnapshotsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProducerEvidenceConflict_IsItsOwnIntegrityLine_NeverRelabelingThePerformanceState()
    {
        var health = new Mock<ICurrentPredictionHealthService>(); var diagnostics = new Mock<IAttestedEvidenceDiagnostics>();
        health.Setup(h => h.EvaluateCurrentAsync(CurrentModel, It.IsAny<CancellationToken>())).ReturnsAsync(
            ImmutableArray.Create(Snapshot(CurrentModel, ModelHealthState.Healthy, 30, 16)));
        diagnostics.Setup(d => d.GetConflictedStreamsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AttestedStreamFault("prov", "INST", TimeframeType.Daily)]);
        using var vm = Settings(Mock.Of<IPredictionLogService>(), currentHealth: health.Object, evidenceDiagnostics: diagnostics.Object);
        vm.SelectedGeneration = Generation(CurrentModel); await vm.MonitoringRefreshTask;
        Assert.Contains("Healthy", vm.MonitoringStatus);                                         // the performance state is unchanged ...
        Assert.Contains(string.Format(LocalizationManager.Instance.Get("Monitoring_EvidenceConflict"), 1), vm.MonitoringStatus); // ... the fault is separate
    }

    [Fact]
    public async Task FailedCurrentEvaluation_ShowsLoadFailure_NeverTheCachedHealthyCard()
    {
        var log = new Mock<IPredictionLogService>(); var health = new Mock<ICurrentPredictionHealthService>();
        log.Setup(l => l.GetSnapshotsAsync(CurrentModel, It.IsAny<CancellationToken>())).ReturnsAsync(
            ImmutableArray.Create(Snapshot(CurrentModel, ModelHealthState.Healthy, 30, 16)));
        health.Setup(h => h.EvaluateCurrentAsync(CurrentModel, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidDataException("corrupt state"));
        using var vm = Settings(log.Object, currentHealth: health.Object);
        vm.SelectedGeneration = Generation(CurrentModel); await vm.MonitoringRefreshTask;
        Assert.Equal(LocalizationManager.Instance.Get("Monitoring_LoadFailed"), vm.MonitoringStatus);
        log.Verify(l => l.GetSnapshotsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StaleOrDisposedCurrentEvaluations_DoNotOverwriteTheCurrentCard()
    {
        var first = new TaskCompletionSource<ImmutableArray<ModelMonitoringSnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var other = new string('b', 64); var health = new Mock<ICurrentPredictionHealthService>();
        health.Setup(h => h.EvaluateCurrentAsync(CurrentModel, It.IsAny<CancellationToken>())).Returns(first.Task);
        health.Setup(h => h.EvaluateCurrentAsync(other, It.IsAny<CancellationToken>())).ReturnsAsync(
            ImmutableArray.Create(Snapshot(other, ModelHealthState.Healthy, 30, 15)));
        using var vm = Settings(Mock.Of<IPredictionLogService>(), currentHealth: health.Object);
        vm.SelectedGeneration = Generation(CurrentModel); var earlier = vm.MonitoringRefreshTask;
        vm.SelectedGeneration = Generation(other); await vm.MonitoringRefreshTask;
        Assert.Contains("Healthy", vm.MonitoringStatus);
        first.SetResult(ImmutableArray.Create(Snapshot(CurrentModel, ModelHealthState.Decayed, 30, 14))); await earlier;
        Assert.Contains("Healthy", vm.MonitoringStatus); Assert.DoesNotContain("Decayed", vm.MonitoringStatus);
        vm.Dispose(); var before = vm.MonitoringStatus;
        await Task.Yield();
        Assert.Equal(before, vm.MonitoringStatus);
    }

    [Fact]
    public async Task HealthLoop_EvaluatesAtOnceThenEveryInterval_SurvivesFailures_AndStopsWithTheLifetime()
    {
        var scheduler = new Mock<IRetrainingScheduler>(); var toast = new Mock<IToastNotificationService>();
        scheduler.Setup(s => s.RunWithRecoveryAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var clock = new ManualTimeProvider(Epoch); var calls = new SemaphoreSlim(0); int count = 0;
        var configured = new PredictionMonitoringSettings { HealthEvaluationIntervalSeconds = 90 };
        var health = new Mock<ICurrentPredictionHealthService>();
        health.Setup(h => h.EvaluateCurrentAsync(null, It.IsAny<CancellationToken>())).Returns((string? _, CancellationToken _) =>
        {
            var n = Interlocked.Increment(ref count); calls.Release();
            return n == 2 ? throw new InvalidDataException("corrupt state") : Task.FromResult(ImmutableArray<ModelMonitoringSnapshot>.Empty);
        });
        var lifetime = new PredictionOperationsLifetime(scheduler.Object, Mock.Of<IPredictionLogService>(), new SynchronousDispatcherService(),
            toast.Object, NullLogger<PredictionOperationsLifetime>.Instance, health.Object, clock, monitoring: Options.Create(configured));
        var interval = configured.HealthEvaluationInterval;
        Assert.Equal(TimeSpan.FromSeconds(90), interval);                                            // the configured cadence, not the default
        lifetime.Start(); lifetime.Start();
        await calls.WaitAsync(TimeSpan.FromSeconds(10)); await clock.TimerArmed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, count);
        clock.Advance(interval - TimeSpan.FromSeconds(1)); Assert.Equal(1, count);                  // not yet due
        clock.Advance(TimeSpan.FromSeconds(1)); await calls.WaitAsync(TimeSpan.FromSeconds(10));     // failing evaluation
        await clock.TimerArmed.WaitAsync(TimeSpan.FromSeconds(10));                                  // loop kept going
        clock.Advance(interval); await calls.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3, count);
        await lifetime.StopAsync();
        clock.Advance(interval + interval);
        Assert.Equal(3, count);
        toast.Verify(t => t.ShowNotification(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task WithoutTheCurrentHealthCapability_NoEvaluationLoopStarts()
    {
        var scheduler = new Mock<IRetrainingScheduler>();
        scheduler.Setup(s => s.RunWithRecoveryAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var clock = new ManualTimeProvider(Epoch);
        var lifetime = new PredictionOperationsLifetime(scheduler.Object, Mock.Of<IPredictionLogService>(), new SynchronousDispatcherService(),
            Mock.Of<IToastNotificationService>(), NullLogger<PredictionOperationsLifetime>.Instance, clock: clock);
        lifetime.Start(); await lifetime.StopAsync();
        Assert.Equal(0, clock.TimerArmed.CurrentCount);
    }

    private static ModelDecayAlert Alert(string id) =>
        new(id, new ModelMonitoringSnapshot("model", "target", 30, 10, 1m / 3, ModelHealthState.Decayed, DateTimeOffset.UtcNow, id));

    [Fact]
    public async Task ActiveOutbox_DeliversThroughTheDispatcherSink_WakesOnDecayed_AndStopsWithTheLifetime()
    {
        var scheduler = new Mock<IRetrainingScheduler>(); var log = new Mock<IPredictionLogService>(); var toast = new Mock<IToastNotificationService>();
        scheduler.Setup(s => s.RunWithRecoveryAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var clock = new ManualTimeProvider(Epoch); var passes = new SemaphoreSlim(0); int count = 0; var stable = Alert("stable-id");
        var outbox = new Mock<IMonitoringNotificationOutbox>();
        outbox.SetupGet(o => o.IsActive).Returns(true); outbox.SetupGet(o => o.PollInterval).Returns(TimeSpan.FromMinutes(1));
        outbox.Setup(o => o.DispatchPendingAsync(It.IsAny<Func<ModelDecayAlert, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(async (Func<ModelDecayAlert, CancellationToken, Task> sink, CancellationToken ct) =>
            {
                int pass = Interlocked.Increment(ref count);
                if (pass == 1) await sink(stable, ct);
                passes.Release();
                return pass == 1 ? 1 : 0;
            });
        var lifetime = new PredictionOperationsLifetime(scheduler.Object, log.Object, new SynchronousDispatcherService(), toast.Object,
            NullLogger<PredictionOperationsLifetime>.Instance, clock: clock, outbox: outbox.Object);
        lifetime.Start(); lifetime.Start();
        await passes.WaitAsync(TimeSpan.FromSeconds(10)); await clock.TimerArmed.WaitAsync(TimeSpan.FromSeconds(10));
        toast.Verify(t => t.ShowNotification(It.IsAny<string>()), Times.Once);                 // delivered by the sink on the dispatcher

        log.Raise(l => l.Decayed += null, log.Object, Alert("committed-event"));                            // wakes the loop; the event itself shows nothing
        await passes.WaitAsync(TimeSpan.FromSeconds(10)); await clock.TimerArmed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, count);
        toast.Verify(t => t.ShowNotification(It.IsAny<string>()), Times.Once);

        clock.Advance(TimeSpan.FromMinutes(1)); await passes.WaitAsync(TimeSpan.FromSeconds(10)); // the retry cadence
        Assert.True(count >= 3);
        await lifetime.StopAsync();
        int stopped = count; clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(stopped, count);
    }

    [Fact]
    public async Task FailingDelivery_IsLoggedAndRetriedAtTheNextCadence_NeverEndingTheLoop()
    {
        var scheduler = new Mock<IRetrainingScheduler>(); scheduler.Setup(s => s.RunWithRecoveryAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var clock = new ManualTimeProvider(Epoch); var passes = new SemaphoreSlim(0); int count = 0;
        var outbox = new Mock<IMonitoringNotificationOutbox>();
        outbox.SetupGet(o => o.IsActive).Returns(true); outbox.SetupGet(o => o.PollInterval).Returns(TimeSpan.FromMinutes(1));
        outbox.Setup(o => o.DispatchPendingAsync(It.IsAny<Func<ModelDecayAlert, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns((Func<ModelDecayAlert, CancellationToken, Task> _, CancellationToken _) =>
            {
                Interlocked.Increment(ref count); passes.Release();
                return Task.FromException<int>(new IOException("database busy"));
            });
        var lifetime = new PredictionOperationsLifetime(scheduler.Object, Mock.Of<IPredictionLogService>(), new SynchronousDispatcherService(),
            Mock.Of<IToastNotificationService>(), NullLogger<PredictionOperationsLifetime>.Instance, clock: clock, outbox: outbox.Object);
        lifetime.Start();
        await passes.WaitAsync(TimeSpan.FromSeconds(10)); await clock.TimerArmed.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromMinutes(1)); await passes.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, count);
        await lifetime.StopAsync();
    }

    [Fact]
    public async Task InactiveOutbox_KeepsTheLegacyInProcessDecayToast()
    {
        var scheduler = new Mock<IRetrainingScheduler>(); scheduler.Setup(s => s.RunWithRecoveryAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var log = new Mock<IPredictionLogService>(); var toast = new Mock<IToastNotificationService>(); var outbox = new Mock<IMonitoringNotificationOutbox>();
        outbox.SetupGet(o => o.IsActive).Returns(false);
        var lifetime = new PredictionOperationsLifetime(scheduler.Object, log.Object, new SynchronousDispatcherService(), toast.Object,
            NullLogger<PredictionOperationsLifetime>.Instance, outbox: outbox.Object);
        lifetime.Start();
        log.Raise(l => l.Decayed += null, log.Object, Alert("legacy"));
        toast.Verify(t => t.ShowNotification(It.IsAny<string>()), Times.Once);
        await lifetime.StopAsync();
        outbox.Verify(o => o.DispatchPendingAsync(It.IsAny<Func<ModelDecayAlert, CancellationToken, Task>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Stop_DrainsWorkRegisteredThroughTheSharedOwner_RejectsLaterWork_AndIsRepeatSafe()
    {
        var scheduler = new Mock<IRetrainingScheduler>(); scheduler.Setup(s => s.RunWithRecoveryAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var owner = new MonitoringWorkOwner(); var entered = new TaskCompletionSource<CancellationToken>(); var exit = new TaskCompletionSource();
        var lifetime = new PredictionOperationsLifetime(scheduler.Object, Mock.Of<IPredictionLogService>(), new SynchronousDispatcherService(),
            Mock.Of<IToastNotificationService>(), NullLogger<PredictionOperationsLifetime>.Instance, work: owner);
        lifetime.Start();
        var chartWork = owner.Run(async ct => { entered.SetResult(ct); await exit.Task; }, CancellationToken.None); // as a chart registers its record/observation task
        var token = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var stop = lifetime.StopAsync();
        Assert.True(token.IsCancellationRequested); Assert.False(stop.IsCompleted);   // the lifetime does not finish before the chart's worker
        exit.SetResult(); await stop.WaitAsync(TimeSpan.FromSeconds(10)); await chartWork.WaitAsync(TimeSpan.FromSeconds(10));

        int late = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.Run(_ => { late++; return Task.CompletedTask; }, CancellationToken.None));
        Assert.Equal(0, late);
        await lifetime.StopAsync(); lifetime.Dispose();                               // repeat Stop / Dispose are safe
        lifetime.Start();                                                              // a stopped lifetime never starts again
        scheduler.Verify(s => s.RunWithRecoveryAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OutboxSinkWaitingForAStuckUiThread_DoesNotBlockShutdown()
    {
        var scheduler = new Mock<IRetrainingScheduler>(); scheduler.Setup(s => s.RunWithRecoveryAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var toast = new Mock<IToastNotificationService>(); var sinkEntered = new TaskCompletionSource();
        var outbox = new Mock<IMonitoringNotificationOutbox>();
        outbox.SetupGet(o => o.IsActive).Returns(true); outbox.SetupGet(o => o.PollInterval).Returns(TimeSpan.FromMinutes(1));
        outbox.Setup(o => o.DispatchPendingAsync(It.IsAny<Func<ModelDecayAlert, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(async (Func<ModelDecayAlert, CancellationToken, Task> sink, CancellationToken ct) =>
            {
                sinkEntered.TrySetResult();
                await sink(Alert("stuck"), ct);                                        // the UI thread never runs the posted action
                return 1;
            });
        var lifetime = new PredictionOperationsLifetime(scheduler.Object, Mock.Of<IPredictionLogService>(), new NeverRunningUiDispatcher(),
            toast.Object, NullLogger<PredictionOperationsLifetime>.Instance, clock: new ManualTimeProvider(Epoch), outbox: outbox.Object);
        lifetime.Start();
        await sinkEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await lifetime.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));               // shutdown blocks the UI thread, so it cannot wait for it
        toast.Verify(t => t.ShowNotification(It.IsAny<string>()), Times.Never);
    }

    /// <summary>A dispatcher whose UI thread is blocked: posted work never starts, so awaiting it never completes.</summary>
    private sealed class NeverRunningUiDispatcher : IDispatcherService
    {
        public void Post(Action action) { }
        public void Post<T>(Action<T> action, T state) { }
        public Task PostAsync(Func<Task> action) => new TaskCompletionSource().Task;
        public Task PostAsync<TState>(Func<TState, Task> action, TState state) => new TaskCompletionSource().Task;
        public bool CheckAccess() => false;
        public void VerifyAccess() { }
    }
}
