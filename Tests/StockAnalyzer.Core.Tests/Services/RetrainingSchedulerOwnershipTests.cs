#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// FP03: durable weekly occurrence ownership. Several scheduler objects on one root stand in for independent processes;
/// they contend through the same OS file leases and are ordered with barriers, never with sleeps.
/// </summary>
public sealed class RetrainingSchedulerOwnershipTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_fp03_" + Guid.NewGuid().ToString("N"));
    private readonly RetrainingSchedulerTests.MutableClock _clock = new(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));
    private static readonly DateTimeOffset Due = new(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
    private static TrainingJobConfig Config(int horizon = 1) => new() { Symbols = ["X"], Architecture = "lstm", WindowSize = 4, Horizon = horizon };

    /// <summary>One simulated scheduler process with its own trainer, registry and experiment log.</summary>
    private sealed class Owner
    {
        public Mock<ITrainingOrchestrator> Trainer { get; } = new();
        public Mock<IModelGenerationRegistry> Registry { get; } = new();
        public RetrainingScheduler Scheduler { get; }
        public Owner(TimeProvider clock, string root) =>
            Scheduler = new(Trainer.Object, Registry.Object, new Mock<IExperimentLogService>().Object, clock: clock, root: root);
        public RetrainingSchedule Current => Scheduler.Current!;
        public void VerifyStarts(Times times) => Trainer.Verify(t => t.StartTrainingAsync(It.IsAny<TrainingJobConfig>(),
            It.IsAny<IProgress<TrainingProgress>>(), It.IsAny<CancellationToken>()), times);
        public void Returns(Func<Task<TrainingRunResult>> run) => Trainer.Setup(t => t.StartTrainingAsync(It.IsAny<TrainingJobConfig>(),
            It.IsAny<IProgress<TrainingProgress>>(), It.IsAny<CancellationToken>())).Returns(run);
        public (TaskCompletionSource Entered, TaskCompletionSource<TrainingRunResult> Exit) Block()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var exit = new TaskCompletionSource<TrainingRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Returns(() => { entered.TrySetResult(); return exit.Task; });
            return (entered, exit);
        }
    }

    private static Task<TrainingRunResult> Failed() => Task.FromResult(new TrainingRunResult { RunId = "run", Success = false, Message = "failed" });

    private async Task<Owner> ScheduledAsync()
    {
        var owner = new Owner(_clock, _root);
        await owner.Scheduler.SaveConfigurationAsync(Config());
        await owner.Scheduler.SetEnabledAsync(true);
        return owner;
    }

    private Owner Fresh() => new(_clock, _root);
    private void PassOneWeek() => _clock.Now = _clock.Now.AddDays(7);
    private string SchedulePath => Path.Combine(_root, "schedule.json");
    private string OccurrenceDirectory(Owner owner) => Path.Combine(_root, "occurrences", owner.Current.JobId);
    private Task<RetrainingSchedule?> Disk() => AtomicJsonFile.LoadAsync<RetrainingSchedule>(SchedulePath, TrainingConfigJson.Options);

    private static string LegacyName(DateTimeOffset due) => due.ToString("yyyyMMdd", new CultureInfo("th-TH"));

    private Task WriteOccurrence(string path, RetrainingOccurrence occurrence)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return AtomicJsonFile.SaveAsync(path, occurrence, TrainingConfigJson.Options);
    }

    private static ModelGenerationManifest Model() => new(2, new string('a', 64), "candidate.onnx", new string('b', 64),
        Array.Empty<ModelSidecar>(), "contract", "evaluation", "classification", "daily", 1, "Up,Down,Neutral", .8);

    [Theory]
    [InlineData("en-US")]
    [InlineData("ja-JP")]
    [InlineData("th-TH")]
    public async Task OccurrenceFiles_UseInvariantGregorianNames_UnderAnyCurrentCulture(string culture)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            var owner = await ScheduledAsync(); owner.Returns(Failed); PassOneWeek();
            await owner.Scheduler.ProcessDueAsync();
            var names = Directory.GetFiles(OccurrenceDirectory(owner)).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(["20261004.claim", "20261004.json", "20261004.owner"], names);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public async Task ConsumedOccurrenceWithALegacyCalendarName_IsRecognizedByPayload_AndNeverReplayed()
    {
        var first = await ScheduledAsync(); PassOneWeek();
        var directory = OccurrenceDirectory(first);
        Assert.NotEqual("20261004", LegacyName(Due)); // the old calendar really produced another basename
        await WriteOccurrence(Path.Combine(directory, LegacyName(Due) + ".claim"), new(first.Current.JobId, Due, RetrainingStatus.Running, _clock.Now));

        var second = Fresh(); second.Returns(Failed);
        await second.Scheduler.ProcessDueAsync();
        Assert.Equal((RetrainingStatus.Interrupted, Due), (second.Current.Status, second.Current.LastOccurrenceUtc)); // consumed, owner gone
        second.VerifyStarts(Times.Never());

        // A terminal journal that appears later is adopted without any re-execution.
        await WriteOccurrence(Path.Combine(directory, LegacyName(Due) + ".json"), new(first.Current.JobId, Due, RetrainingStatus.Succeeded,
            _clock.Now, RunId: "run", CandidateModelId: new string('c', 64)));
        await second.Scheduler.ProcessDueAsync();
        Assert.Equal((RetrainingStatus.Succeeded, new string('c', 64)), (second.Current.Status, second.Current.CandidateModelId));
        second.VerifyStarts(Times.Never()); first.VerifyStarts(Times.Never());
        Assert.Equal(RetrainingStatus.Succeeded, (await Disk())!.Status);
    }

    [Fact]
    public async Task MalformedOrConflictingEntries_FailExplicitly_WithoutDeletionRenameOrTrainerStart()
    {
        var first = await ScheduledAsync(); PassOneWeek();
        var directory = OccurrenceDirectory(first); Directory.CreateDirectory(directory);
        var claim = Path.Combine(directory, "20261004.claim"); File.WriteAllText(claim, "{ not json");
        var second = Fresh(); second.Returns(Failed);
        await Assert.ThrowsAsync<InvalidDataException>(() => second.Scheduler.ProcessDueAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => second.Scheduler.ProcessDueAsync()); // owner lease was released again
        Assert.Equal("{ not json", File.ReadAllText(claim));
        second.VerifyStarts(Times.Never());

        File.Delete(claim);
        await WriteOccurrence(Path.Combine(directory, "20261004.json"), new(first.Current.JobId, Due, RetrainingStatus.Failed, _clock.Now, Error: "one"));
        await WriteOccurrence(Path.Combine(directory, LegacyName(Due) + ".json"), new(first.Current.JobId, Due, RetrainingStatus.Succeeded, _clock.Now));
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Fresh().Scheduler.ProcessDueAsync());
        Assert.Contains("Conflicting", ex.Message);
        Assert.Equal(2, Directory.GetFiles(directory, "*.json").Length);
    }

    [Fact]
    public async Task CompetingOwners_StartTheTrainerOnce_AndTheFollowerPublishesRunningThenTheTerminalStatus()
    {
        var owner = await ScheduledAsync(); var follower = Fresh(); await follower.Scheduler.InitializeAsync();
        PassOneWeek();
        var (entered, exit) = owner.Block();
        var run = owner.Scheduler.ProcessDueAsync(); await entered.Task.WaitAsync(Wait);

        await follower.Scheduler.ProcessDueAsync();
        Assert.Equal((RetrainingStatus.Running, Due), (follower.Current.Status, follower.Current.LastOccurrenceUtc));
        Assert.Equal(RetrainingStatus.Running, (await Disk())!.Status);
        follower.VerifyStarts(Times.Never());

        // Startup between the claim and the trainer's exit must not declare the live occurrence interrupted.
        var restarted = Fresh(); await restarted.Scheduler.InitializeAsync();
        Assert.Equal(RetrainingStatus.Running, restarted.Current.Status);
        Assert.Equal(RetrainingStatus.Running, (await Disk())!.Status);

        var model = Model(); owner.Registry.Setup(r => r.RegisterAsync("candidate.onnx", null, It.IsAny<CancellationToken>())).ReturnsAsync(model);
        exit.SetResult(new TrainingRunResult { RunId = "run", Success = true, OnnxArtifactPath = "candidate.onnx" });
        await run;
        await follower.Scheduler.ProcessDueAsync();
        Assert.Equal((RetrainingStatus.Succeeded, model.ModelId), (follower.Current.Status, follower.Current.CandidateModelId));
        owner.VerifyStarts(Times.Once()); follower.VerifyStarts(Times.Never()); restarted.VerifyStarts(Times.Never());
        owner.Registry.Verify(r => r.ActivateAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UnsynchronizedTicksOfIndependentOwners_StartTheTrainerExactlyOnce_AcrossManyRounds()
    {
        const int rounds = 40;
        for (int round = 0; round < rounds; round++)
        {
            var saved = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
            _clock.Now = saved; // the baseline is the week of saving, so every round starts one week before its due
            var root = Path.Combine(_root, "race" + round);
            var owners = Enumerable.Range(0, 3).Select(_ => new Owner(_clock, root)).ToArray();
            await owners[0].Scheduler.SaveConfigurationAsync(Config()); await owners[0].Scheduler.SetEnabledAsync(true);
            foreach (var owner in owners) { owner.Returns(Failed); await owner.Scheduler.InitializeAsync(); }
            _clock.Now = saved.AddDays(7);
            using var start = new Barrier(owners.Length);
            await Task.WhenAll(owners.Select(owner => Task.Run(() => { start.SignalAndWait(); return owner.Scheduler.ProcessDueAsync(); })));
            Assert.Equal(1, owners.Sum(o => o.Trainer.Invocations.Count));
            // Every owner converges on the single recorded outcome once the busy ticks are retried.
            foreach (var owner in owners) await owner.Scheduler.ProcessDueAsync();
            Assert.All(owners, o => Assert.Equal((RetrainingStatus.Failed, Due), (o.Current.Status, o.Current.LastOccurrenceUtc)));
            Assert.Equal(1, owners.Sum(o => o.Trainer.Invocations.Count));
        }
    }

    [Fact]
    public async Task OwnerThatDiedAfterItsJournal_IsAdopted_AndOneWithoutJournalIsInterrupted()
    {
        var first = await ScheduledAsync(); PassOneWeek();
        var run = first.Block(); var task = first.Scheduler.ProcessDueAsync(); await run.Entered.Task.WaitAsync(Wait);
        var directory = OccurrenceDirectory(first);
        // While the owner lease is held, a journal-less Running occurrence stays Running.
        Assert.Equal(RetrainingStatus.Running, (await Disk())!.Status);
        run.Exit.SetResult(new TrainingRunResult { RunId = "run", Success = false, Message = "failed" }); await task;

        // Rewind the schedule to Running while the journal already exists (crash between journal and schedule save).
        await AtomicJsonFile.SaveAsync(SchedulePath, (await Disk())! with { Status = RetrainingStatus.Running, Error = null }, TrainingConfigJson.Options);
        var adopter = Fresh(); await adopter.Scheduler.InitializeAsync();
        Assert.Equal(RetrainingStatus.Failed, adopter.Current.Status);

        // Rewind again and remove the journal: no live owner and no terminal evidence -> Interrupted, never re-run.
        File.Delete(Path.Combine(directory, "20261004.json"));
        await AtomicJsonFile.SaveAsync(SchedulePath, (await Disk())! with { Status = RetrainingStatus.Running }, TrainingConfigJson.Options);
        var restarted = Fresh(); await restarted.Scheduler.InitializeAsync(); await restarted.Scheduler.ProcessDueAsync();
        Assert.Equal(RetrainingStatus.Interrupted, restarted.Current.Status);
        restarted.VerifyStarts(Times.Never());
    }

    [Fact]
    public async Task StaleOwners_CannotOverwriteEnabledStateOrConfiguration()
    {
        var current = await ScheduledAsync(); var stale = Fresh(); await stale.Scheduler.InitializeAsync();
        await current.Scheduler.SetEnabledAsync(false); PassOneWeek(); stale.Returns(Failed);
        await stale.Scheduler.ProcessDueAsync();
        stale.VerifyStarts(Times.Never());
        Assert.False(stale.Current.Enabled);

        await current.Scheduler.SaveConfigurationAsync(Config(horizon: 2));
        await stale.Scheduler.SetEnabledAsync(true); // the stale object still holds the old configuration in memory
        var disk = (await Disk())!;
        Assert.Equal((2, true, TrainingJobGate.Identity(Config(2))), (disk.Config.Horizon, disk.Enabled, disk.JobId));
        Assert.Equal(disk.JobId, stale.Current.JobId);
    }

    [Fact]
    public async Task ActiveOccurrenceOwnedElsewhere_RejectsConfigurationSave_ButDisableStillWorksAndDoesNotCancel()
    {
        var owner = await ScheduledAsync(); var other = Fresh(); await other.Scheduler.InitializeAsync(); PassOneWeek();
        var (entered, exit) = owner.Block();
        var run = owner.Scheduler.ProcessDueAsync(); await entered.Task.WaitAsync(Wait);

        await Assert.ThrowsAsync<TrainingJobBusyException>(() => other.Scheduler.SaveConfigurationAsync(Config(horizon: 3)));
        Assert.Equal(1, (await Disk())!.Config.Horizon);
        await other.Scheduler.SetEnabledAsync(false);
        var disk = (await Disk())!;
        Assert.Equal((false, RetrainingStatus.Running), (disk.Enabled, disk.Status));
        Assert.False(run.IsCompleted);

        exit.SetResult(new TrainingRunResult { RunId = "run", Success = false, Message = "failed" }); await run;
        disk = (await Disk())!;
        Assert.Equal((false, RetrainingStatus.Failed), (disk.Enabled, disk.Status)); // terminal result merged, Enabled kept
    }

    [Fact]
    public async Task BusyRootLease_ConsumesNothing_AndReportsATypedErrorWithoutChangingState()
    {
        var owner = await ScheduledAsync(); var other = Fresh(); await other.Scheduler.InitializeAsync(); PassOneWeek();
        other.Returns(Failed);
        var before = File.ReadAllBytes(SchedulePath);
        using (new FileStream(Path.Combine(_root, "schedule.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await other.Scheduler.ProcessDueAsync();
            other.VerifyStarts(Times.Never());
            await Assert.ThrowsAsync<RetrainingScheduleBusyException>(() => other.Scheduler.SetEnabledAsync(false));
            await Assert.ThrowsAsync<RetrainingScheduleBusyException>(() => other.Scheduler.SaveConfigurationAsync(Config(horizon: 2)));
            await Assert.ThrowsAsync<RetrainingScheduleBusyException>(() => Fresh().Scheduler.InitializeAsync());
            Assert.Equal(before, File.ReadAllBytes(SchedulePath));
            Assert.False(Directory.Exists(Path.Combine(_root, "occurrences", owner.Current.JobId)));
        }
        await other.Scheduler.ProcessDueAsync(); // the due was not consumed by the busy ticks
        other.VerifyStarts(Times.Once());
    }

    [Fact]
    public async Task TerminalSaveFailure_KeepsTheDueConsumed_AndTheNextTickMarksItInterruptedWithoutRerun()
    {
        var owner = await ScheduledAsync(); PassOneWeek();
        owner.Returns(() =>
        {
            Directory.CreateDirectory(Path.Combine(OccurrenceDirectory(owner), "20261004.json")); // makes the journal write fail
            return Failed();
        });
        await Assert.ThrowsAnyAsync<Exception>(() => owner.Scheduler.ProcessDueAsync());
        await owner.Scheduler.ProcessDueAsync();
        Assert.Equal((RetrainingStatus.Interrupted, Due), (owner.Current.Status, owner.Current.LastOccurrenceUtc));
        owner.VerifyStarts(Times.Once());
    }

    [Fact]
    public async Task RecoveryRunner_ReportsAStartupFault_KeepsTheFile_AndResumesAfterItIsRepaired()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));
        var source = Path.Combine(_root, "source");
        var author = new Owner(clock, source);
        await author.Scheduler.SaveConfigurationAsync(Config()); await author.Scheduler.SetEnabledAsync(true);

        Directory.CreateDirectory(_root);
        File.WriteAllText(SchedulePath, "{ corrupt");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new Owner(clock, _root);
        owner.Returns(() => { started.TrySetResult(); return Failed(); });
        using var cts = new CancellationTokenSource();
        var loop = Task.Run(() => owner.Scheduler.RunWithRecoveryAsync(cts.Token));
        await clock.TimerArmed.WaitAsync(Wait); // first attempt failed, the poll delay is armed
        Assert.Null(owner.Scheduler.Current);
        Assert.Equal("{ corrupt", File.ReadAllText(SchedulePath)); // never rewritten as a Disabled schedule
        owner.VerifyStarts(Times.Never());

        File.Copy(Path.Combine(source, "schedule.json"), SchedulePath, overwrite: true); // external repair
        clock.SetNow(clock.GetUtcNow().AddDays(7));
        clock.Advance(PredictionMonitoringPolicy.SchedulerPollInterval);
        await started.Task.WaitAsync(Wait);
        cts.Cancel(); await loop.WaitAsync(Wait);
        owner.VerifyStarts(Times.Once());
    }

    [Fact]
    public async Task RunWithRecoveryAsync_StopsPromptlyOnCancellation_AndLegacyRunAsyncStillFailsFastOnAnInvalidStartup()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));
        Directory.CreateDirectory(_root); File.WriteAllText(SchedulePath, "{ corrupt");
        var owner = new Owner(clock, _root);
        await Assert.ThrowsAnyAsync<Exception>(() => owner.Scheduler.RunAsync(CancellationToken.None)); // legacy contract unchanged
        using var cts = new CancellationTokenSource();
        var loop = Task.Run(() => owner.Scheduler.RunWithRecoveryAsync(cts.Token));
        await clock.TimerArmed.WaitAsync(Wait);
        cts.Cancel(); await loop.WaitAsync(Wait);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
