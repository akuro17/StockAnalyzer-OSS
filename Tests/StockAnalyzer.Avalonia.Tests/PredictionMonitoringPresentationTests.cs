using System.Collections.Immutable;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests;

[Collection("LocalizationSharedState")]
public sealed class PredictionMonitoringPresentationTests : IDisposable
{
    private readonly string _language = LocalizationManager.Instance.CurrentLanguage;
    public PredictionMonitoringPresentationTests() => LocalizationManager.Instance.Initialize("en");
    public void Dispose() => LocalizationManager.Instance.Initialize(_language);
    private static ModelGenerationSummary Generation(string id, string target = "classification") =>
        new(id, "model.onnx", target, "daily", 5, .7, new string('a', 64), false);
    private static AIPredictionsSettingsViewModel Settings(IPredictionLogService log, IRetrainingScheduler? scheduler = null,
        ICurrentPredictionHealthService? currentHealth = null) =>
        new(Mock.Of<IPredictionSettingsManager>(s => s.WindowSize == 75), Mock.Of<IClipboardService>(),
            Mock.Of<IToastNotificationService>(), analysisDispatcher: new SynchronousDispatcherService(),
            predictionLog: log, retrainingScheduler: scheduler, currentHealth: currentHealth);

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
        var clock = new ManualTimeProvider(); var calls = new SemaphoreSlim(0); int count = 0;
        var health = new Mock<ICurrentPredictionHealthService>();
        health.Setup(h => h.EvaluateCurrentAsync(null, It.IsAny<CancellationToken>())).Returns((string? _, CancellationToken _) =>
        {
            var n = Interlocked.Increment(ref count); calls.Release();
            return n == 2 ? throw new InvalidDataException("corrupt state") : Task.FromResult(ImmutableArray<ModelMonitoringSnapshot>.Empty);
        });
        var lifetime = new PredictionOperationsLifetime(scheduler.Object, Mock.Of<IPredictionLogService>(), new SynchronousDispatcherService(),
            toast.Object, NullLogger<PredictionOperationsLifetime>.Instance, health.Object, clock);
        var interval = PredictionMonitoringPolicy.HealthEvaluationInterval;
        Assert.Equal(TimeSpan.FromMinutes(1), interval);
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
        var clock = new ManualTimeProvider();
        var lifetime = new PredictionOperationsLifetime(scheduler.Object, Mock.Of<IPredictionLogService>(), new SynchronousDispatcherService(),
            Mock.Of<IToastNotificationService>(), NullLogger<PredictionOperationsLifetime>.Instance, clock: clock);
        lifetime.Start(); await lifetime.StopAsync();
        Assert.Equal(0, clock.TimerArmed.CurrentCount);
    }

    /// <summary>Manual clock whose delay timers only fire when <see cref="Advance"/> passes their due time.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
        public SemaphoreSlim TimerArmed { get; } = new(0);
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_gate) _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan by)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                _now += by;
                due = _timers.Where(t => t.DueAt is { } at && at <= _now).ToList();
                foreach (var t in due) t.Disarm();
            }
            foreach (var t in due) t.Fire();
        }
        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? DueAt { get; private set; }
            public void Disarm() => DueAt = null;
            public void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate) DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                if (dueTime != Timeout.InfiniteTimeSpan) owner.TimerArmed.Release();
                return true;
            }
            public void Dispose() { lock (owner._gate) { DueAt = null; owner._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
