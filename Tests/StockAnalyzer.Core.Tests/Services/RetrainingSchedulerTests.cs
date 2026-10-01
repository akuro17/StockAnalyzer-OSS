#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class RetrainingSchedulerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_t10_schedule_" + Guid.NewGuid().ToString("N"));
    private readonly MutableClock _clock = new(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));
    private readonly Mock<ITrainingOrchestrator> _trainer = new();
    private readonly Mock<IModelGenerationRegistry> _registry = new();
    private readonly Mock<IExperimentLogService> _experiments = new();
    private static TrainingJobConfig Config() => new() { Symbols = ["X"], Architecture = "lstm", WindowSize = 4, Horizon = 1 };
    private RetrainingScheduler Create() => new(_trainer.Object, _registry.Object, _experiments.Object, clock: _clock, root: _root);

    [Theory]
    [InlineData("2026-10-04T00:00:00Z", "2026-10-04T00:00:00Z")]
    [InlineData("2026-10-03T23:59:59Z", "2026-09-27T00:00:00Z")]
    [InlineData("2026-11-01T01:30:00-04:00", "2026-11-01T00:00:00Z")]
    public void Occurrences_AreSundayUtc_NotLocalDst(string now, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected), RetrainingScheduler.LatestOccurrence(DateTimeOffset.Parse(now)));

    [Fact]
    public async Task ExplicitConfiguration_IsFrozenDisabled_AndEnableDoesNotRunOldWeeks()
    {
        var config = Config(); var scheduler = Create();
        await scheduler.SaveConfigurationAsync(config);
        config.Symbols[0] = "MUTATED";
        Assert.Equal("X", scheduler.Current!.Config.Symbols[0]);
        scheduler.Current.Config.Symbols[0] = "MUTATED_AGAIN";
        Assert.Equal("X", scheduler.Current.Config.Symbols[0]);
        Assert.False(scheduler.Current.Enabled);
        _clock.Now = _clock.Now.AddDays(28);
        await scheduler.ProcessDueAsync();
        await scheduler.SetEnabledAsync(true);
        await scheduler.ProcessDueAsync();
        _trainer.Verify(t => t.StartTrainingAsync(It.IsAny<TrainingJobConfig>(), It.IsAny<IProgress<TrainingProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(RetrainingScheduler.LatestOccurrence(_clock.Now), scheduler.Current.LastOccurrenceUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_CatchesUpOnlyLatestOnce_NoRetryForFailureOrManualCollision(bool busy)
    {
        var scheduler = Create();
        await scheduler.SaveConfigurationAsync(Config()); await scheduler.SetEnabledAsync(true);
        _clock.Now = _clock.Now.AddDays(35);
        _trainer.Setup(t => t.StartTrainingAsync(It.IsAny<TrainingJobConfig>(), It.IsAny<IProgress<TrainingProgress>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(busy ? new TrainingJobBusyException() : new InvalidOperationException("training failed"));
        scheduler = Create();
        await scheduler.ProcessDueAsync();
        Assert.Equal(busy ? RetrainingStatus.Skipped : RetrainingStatus.Failed, scheduler.Current!.Status);
        Assert.Equal(RetrainingScheduler.LatestOccurrence(_clock.Now), scheduler.Current.LastOccurrenceUtc);
        await scheduler.ProcessDueAsync(); await Create().ProcessDueAsync();
        _trainer.Verify(t => t.StartTrainingAsync(It.IsAny<TrainingJobConfig>(), It.IsAny<IProgress<TrainingProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "occurrences", scheduler.Current.JobId), "*.json"));
    }

    [Fact]
    public async Task ConcurrentTicks_CancelAndDisable_KeepSingleFlightThroughAwaitedExit()
    {
        var scheduler = Create(); await scheduler.SaveConfigurationAsync(Config()); await scheduler.SetEnabledAsync(true);
        _clock.Now = _clock.Now.AddDays(7);
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exit = new TaskCompletionSource<TrainingRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _trainer.Setup(t => t.StartTrainingAsync(It.IsAny<TrainingJobConfig>(), It.IsAny<IProgress<TrainingProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((TrainingJobConfig _, IProgress<TrainingProgress>? _, CancellationToken token) => { entered.SetResult(token); return exit.Task; });
        var run = scheduler.ProcessDueAsync(); var token = await entered.Task;
        scheduler.CancelCurrent(); Assert.True(token.IsCancellationRequested); Assert.False(run.IsCompleted);
        await scheduler.ProcessDueAsync();
        await Assert.ThrowsAsync<TrainingJobBusyException>(() => scheduler.SaveConfigurationAsync(Config()));
        await scheduler.SetEnabledAsync(false);
        exit.SetCanceled(token); await run;
        Assert.Equal(RetrainingStatus.Cancelled, scheduler.Current!.Status); Assert.False(scheduler.Current.Enabled);
        _trainer.Verify(t => t.StartTrainingAsync(It.IsAny<TrainingJobConfig>(), It.IsAny<IProgress<TrainingProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SuccessfulRetraining_RegistersCandidateAndExperiment_NeverActivates()
    {
        var scheduler = Create(); await scheduler.SaveConfigurationAsync(Config()); await scheduler.SetEnabledAsync(true);
        _clock.Now = _clock.Now.AddDays(7);
        var result = new TrainingRunResult { RunId = "scheduled", Success = true, OnnxArtifactPath = "candidate.onnx" };
        _trainer.Setup(t => t.StartTrainingAsync(It.IsAny<TrainingJobConfig>(), It.IsAny<IProgress<TrainingProgress>>(), It.IsAny<CancellationToken>())).ReturnsAsync(result);
        var model = new ModelGenerationManifest(2, new string('a', 64), "candidate.onnx", new string('b', 64),
            Array.Empty<ModelSidecar>(), "contract", "evaluation", "classification", "daily", 1, "Up,Down,Neutral", .8);
        _registry.Setup(r => r.RegisterAsync("candidate.onnx", null, It.IsAny<CancellationToken>())).ReturnsAsync(model);
        await scheduler.ProcessDueAsync();
        Assert.Equal(model.ModelId, scheduler.Current!.CandidateModelId); Assert.Equal(RetrainingStatus.Succeeded, scheduler.Current.Status);
        _experiments.Verify(e => e.RecordAsync(It.Is<TrainingJobConfig>(c => c.RunId == "scheduled"), result, It.IsAny<CancellationToken>()), Times.Once);
        _registry.Verify(r => r.ActivateAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void JobLease_IsSharedAcrossOwners_StableAcrossRuntimeFields_ReleasedOnlyByOwner()
    {
        var config = Config(); var first = new TrainingJobGate(_root); var second = new TrainingJobGate(_root);
        using (var lease = first.Acquire(config))
        {
            Assert.Throws<TrainingJobBusyException>(() => second.Acquire(config with { RunId = "new" }));
            using var different = second.Acquire(config with { Horizon = 2 });
        }
        using var released = second.Acquire(config);
        Assert.Equal(TrainingJobGate.Identity(config), TrainingJobGate.Identity(config with { RunId = "different", PreparedInputDir = "runtime" }));
    }

    [Fact]
    public async Task InterruptedClaim_IsNotRetriedAndCorruptIdentityIsRejected()
    {
        var scheduler = Create(); await scheduler.SaveConfigurationAsync(Config()); await scheduler.SetEnabledAsync(true);
        var path = Path.Combine(_root, "schedule.json");
        var state = scheduler.Current! with { Status = RetrainingStatus.Running };
        await Core.Common.AtomicJsonFile.SaveAsync(path, state, TrainingConfigJson.Options);
        var restarted = Create(); await restarted.InitializeAsync();
        Assert.Equal(RetrainingStatus.Interrupted, restarted.Current!.Status);
        await restarted.ProcessDueAsync();
        await Core.Common.AtomicJsonFile.SaveAsync(path, state with { JobId = "corrupt" }, TrainingConfigJson.Options);
        await Assert.ThrowsAsync<InvalidDataException>(() => Create().InitializeAsync());
    }

    [Fact]
    public async Task SeparateSchedulerOwners_CannotReplayAnOccurrenceAfterTheFirstCompletes()
    {
        var first = Create(); await first.SaveConfigurationAsync(Config()); await first.SetEnabledAsync(true);
        var second = Create(); await second.InitializeAsync();
        _clock.Now = _clock.Now.AddDays(7);
        _trainer.Setup(t => t.StartTrainingAsync(It.IsAny<TrainingJobConfig>(), It.IsAny<IProgress<TrainingProgress>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("failed"));
        await first.ProcessDueAsync(); await second.ProcessDueAsync(); await second.ProcessDueAsync();
        _trainer.Verify(t => t.StartTrainingAsync(It.IsAny<TrainingJobConfig>(), It.IsAny<IProgress<TrainingProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(first.Current!.LastOccurrenceUtc, second.Current!.LastOccurrenceUtc);
        Assert.Equal(RetrainingStatus.Failed, second.Current.Status);
    }

    [Fact]
    public async Task CancelAtRunningNotification_IsNotLostBeforeTrainerReceivesItsToken()
    {
        var scheduler = Create(); await scheduler.SaveConfigurationAsync(Config()); await scheduler.SetEnabledAsync(true);
        _clock.Now = _clock.Now.AddDays(7);
        scheduler.Changed += (_, _) => { if (scheduler.Current?.Status == RetrainingStatus.Running) scheduler.CancelCurrent(); };
        _trainer.Setup(t => t.StartTrainingAsync(It.IsAny<TrainingJobConfig>(), It.IsAny<IProgress<TrainingProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((TrainingJobConfig _, IProgress<TrainingProgress>? _, CancellationToken token) =>
            {
                Assert.True(token.IsCancellationRequested);
                return Task.FromCanceled<TrainingRunResult>(token);
            });
        await scheduler.ProcessDueAsync(); Assert.Equal(RetrainingStatus.Cancelled, scheduler.Current!.Status);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    internal sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
