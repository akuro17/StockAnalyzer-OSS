#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed partial class RlGenerationAndQValueTests
{
    private const string WorkerVariable = "SA_R07_WORKER_CONFIG";
    private static readonly TimeSpan WorkerLimit = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WorkerPoll = TimeSpan.FromMilliseconds(25);
    private sealed record WorkerConfig(string Operation, string Root, string Value, string Ready, string Trigger);
    public sealed class RegistryWorkerAttribute : FactAttribute
    { public RegistryWorkerAttribute() { if (Environment.GetEnvironmentVariable(WorkerVariable) is null) Skip = "Owned R07 child-process worker."; } }
    private static async Task WaitFile(string file)
    {
        var elapsed = Stopwatch.StartNew();
        while (!File.Exists(file))
        {
            if (elapsed.Elapsed > WorkerLimit) throw new TimeoutException("Owned registry worker synchronization timed out.");
            await Task.Delay(WorkerPoll);
        }
    }
    [RegistryWorker] public async Task R07Worker()
    {
        var config = JsonSerializer.Deserialize<WorkerConfig>(File.ReadAllText(Environment.GetEnvironmentVariable(WorkerVariable)!))!;
        if (config.Operation == "checkpoint-budget")
        {
            Directory.CreateDirectory(config.Value);
            using var budget = new StockAnalyzer.Core.Services.ReinforcementLearning.RlCheckpointStore(config.Value, Guid.NewGuid(),
                _ => true, action => action(), () => false);
            budget.Charge(20, 4); budget.Charge(35, 5);
            File.WriteAllText(config.Ready, "OS writes complete; owner still running"); await WaitFile(config.Trigger); return;
        }
        if (config.Operation == "hold-store")
        {
            using var held = new FileStream(Path.Combine(config.Root, ModelGenerationRegistry.Coordination.Folder, ModelGenerationRegistry.Coordination.LockFile),
                FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            File.WriteAllText(config.Ready, "held"); await WaitFile(config.Trigger); return;
        }
        using var registry = new ModelGenerationRegistry(config.Root,
            config.Operation == "activate-pause" ? async (path, pointer) => {
                File.WriteAllText(config.Ready, "before-pointer-commit"); await WaitFile(config.Trigger);
                await AtomicJsonFile.SaveAsync(path, pointer, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            } : null)
        {
            ArtifactFreeSpace = _ => long.MaxValue, // Owned lease worker uses the parent's injected free-space condition.
            RlStagingPrepared = config.Operation == "register-pause" ? stage => {
                File.WriteAllText(config.Ready, stage); WaitFile(config.Trigger).GetAwaiter().GetResult();
            } : null,
        };
        if (config.Operation == "artifact-run")
        {
            string dataRoot = Path.GetDirectoryName(config.Root)!;
            registry.ConfigureRlArtifacts(dataRoot, new StockAnalyzer.Core.Services.ReinforcementLearning.RlArtifactSettings(
                Path.Combine(dataRoot, "config", StockAnalyzer.Core.Services.ReinforcementLearning.RlArtifactSettings.FileName)));
            using var run = await registry.BeginRlArtifactRunAsync(Guid.ParseExact(config.Value,"N"), dataRoot);
            File.WriteAllText(config.Ready,run!.Directory); await WaitFile(config.Trigger);
            await run.FinishAsync(StockAnalyzer.Core.Services.ReinforcementLearning.RlArtifactRunState.Failed); return;
        }
        if (config.Operation is "register" or "register-pause")
        {
            var generation = await registry.RegisterRlAsync(config.Value);
            File.WriteAllText(config.Ready, generation.ModelId); return;
        }
        if (config.Operation == "lease")
        {
            using var lease = registry.AcquireRl(config.Value) ?? throw new InvalidOperationException("Owned generation unavailable.");
            File.WriteAllText(config.Ready, lease.ModelPath); await WaitFile(config.Trigger); return;
        }
        // Force a preexisting in-memory snapshot in two processes before the parent releases both writers.
        _ = registry.RlActiveId;
        using var candidateLease = registry.AcquireRl(config.Value) ?? throw new InvalidOperationException("Activation candidate was unavailable.");
        if (config.Operation == "activate") { File.WriteAllText(config.Ready, "snapshot-loaded"); await WaitFile(config.Trigger); }
        await registry.ActivateRlAsync(config.Value);
    }
    private sealed class OwnedWorker(Process process)
    {
        public Process Process { get; } = process;
        public Task<string> Output { get; } = process.StandardOutput.ReadToEndAsync();
        public Task<string> Error { get; } = process.StandardError.ReadToEndAsync();
        public bool Closed { get; set; }
    }
    private OwnedWorker StartWorker(string operation, string value, string ready, string trigger)
    {
        string configPath = Path.Combine(_root, "worker_" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(configPath, JsonSerializer.Serialize(new WorkerConfig(operation, RegistryRoot, value, ready, trigger)));
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory };
        start.ArgumentList.Add("vstest"); start.ArgumentList.Add(typeof(RlGenerationAndQValueTests).Assembly.Location);
        start.ArgumentList.Add("/Tests:StockAnalyzer.Core.Tests.Services.RlGenerationAndQValueTests.R07Worker");
        start.Environment[WorkerVariable] = configPath;
        return new OwnedWorker(Process.Start(start)!);
    }
    private static async Task EndWorker(OwnedWorker worker)
    {
        try
        {
            await worker.Process.WaitForExitAsync().WaitAsync(WorkerLimit);
            Assert.True(worker.Process.ExitCode == 0, await worker.Output + await worker.Error);
        }
        finally { StopWorker(worker); }
    }
    private static void StopWorker(OwnedWorker worker)
    {
        if (worker.Closed) return;
        worker.Closed = true;
        try { if (!worker.Process.HasExited) { worker.Process.Kill(entireProcessTree: true); worker.Process.WaitForExit(); } }
        catch (InvalidOperationException) { }
        finally { worker.Process.Dispose(); }
    }

    [Fact] public async Task R09_WorkJournalSurvivesAbruptOwnedWriterExitWithoutPerStepPhysicalFlush()
    {
        Directory.CreateDirectory(_root);
        string directory = Path.Combine(_root, "r09-journal"), ready = Path.Combine(_root, "r09-journal-ready"),
            trigger = Path.Combine(_root, "r09-journal-unused-trigger");
        var worker = StartWorker("checkpoint-budget", directory, ready, trigger);
        try { await WaitFile(ready); }
        finally { StopWorker(worker); } // OS termination: the child's store.Dispose cannot perform its final disk flush.
        // Financial charges survive, but the owner's unrecorded active-time tail is not provable after a kill.
        var floor = StockAnalyzer.Core.Services.ReinforcementLearning.RlCheckpointStore.ReadBudget(directory, activeOwner: true);
        Assert.Equal(35, floor.Transitions); Assert.Equal(5, floor.Starts);
        Assert.Throws<InvalidDataException>(() => StockAnalyzer.Core.Services.ReinforcementLearning.RlCheckpointStore.ReadBudget(directory));
    }

    [Fact] public async Task R07_ParallelRegistrationsAndStaleWriterSnapshotsKeepBothCommits()
    {
        using var registry = new ModelGenerationRegistry(RegistryRoot);
        var first = await registry.RegisterRlAsync(Candidate(.01m).Directory); await registry.ActivateRlAsync(first.ModelId);
        string readyA = Path.Combine(_root, "readyA"), readyB = Path.Combine(_root, "readyB"), trigger = Path.Combine(_root, "trigger");
        var processA = StartWorker("register", Candidate(.02m).Directory, readyA, trigger);
        var processB = StartWorker("register", Candidate(.03m).Directory, readyB, trigger);
        await Task.WhenAll(EndWorker(processA), EndWorker(processB));
        string a = File.ReadAllText(readyA), b = File.ReadAllText(readyB);
        Assert.NotNull(registry.GetRlGeneration(a)); Assert.NotNull(registry.GetRlGeneration(b));
        File.Delete(readyA); File.Delete(readyB);
        processA = StartWorker("activate", a, readyA, trigger); processB = StartWorker("activate", b, readyB, trigger);
        try
        {
            await Task.WhenAll(WaitFile(readyA), WaitFile(readyB)); File.WriteAllText(trigger, "go");
            await Task.WhenAll(EndWorker(processA), EndWorker(processB));
        }
        catch { StopWorker(processA); StopWorker(processB); throw; }
        string active = registry.RlActiveId!;
        Assert.True(active == a || active == b);
        using var pointer = JsonDocument.Parse(File.ReadAllText(Path.Combine(RegistryRoot, RlFiles.RlFolder, RlFiles.Active)));
        var history = pointer.RootElement.GetProperty("history").EnumerateArray().Select(v => v.GetString()).ToArray();
        Assert.Contains(first.ModelId, history); Assert.Contains(active == a ? b : a, history);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task R07_CrossProcessLeaseSurvivesPruneAndIsReleasedNormallyOrByExit(bool kill)
    {
        using var registry = new ModelGenerationRegistry(RegistryRoot);
        var first = await registry.RegisterRlAsync(Candidate(.01m).Directory); await registry.ActivateRlAsync(first.ModelId);
        string ready = Path.Combine(_root, "ready"), trigger = Path.Combine(_root, "trigger");
        var process = StartWorker("lease", first.ModelId, ready, trigger);
        try
        {
            await WaitFile(ready);
            for (int i = 0; i < RlContract.RollbackGenerationCount + 2; i++)
            { var next = await registry.RegisterRlAsync(Candidate(.02m + i / 100m).Directory); await registry.ActivateRlAsync(next.ModelId); }
            Assert.True(File.Exists(File.ReadAllText(ready))); Assert.NotNull(registry.GetRlGeneration(first.ModelId));
            if (kill) StopWorker(process); else { File.WriteAllText(trigger, "release"); await EndWorker(process); }
            var last = await registry.RegisterRlAsync(Candidate(.99m).Directory); await registry.ActivateRlAsync(last.ModelId);
            Assert.Null(registry.GetRlGeneration(first.ModelId));
        }
        catch { StopWorker(process); throw; }
    }
    [Fact] public async Task R07_OwnerExitAfterStagingOrBeforePointerCommitPreservesTheActive()
    {
        using var registry = new ModelGenerationRegistry(RegistryRoot);
        var first = await registry.RegisterRlAsync(Candidate(.01m).Directory); await registry.ActivateRlAsync(first.ModelId);
        string ready = Path.Combine(_root, "ready"), trigger = Path.Combine(_root, "trigger");
        var process = StartWorker("register-pause", Candidate(.02m).Directory, ready, trigger);
        await WaitFile(ready); string orphan = File.ReadAllText(ready); StopWorker(process);
        Assert.True(Directory.Exists(orphan)); Assert.Equal(first.ModelId, registry.RlActiveId);
        var second = await registry.RegisterRlAsync(Candidate(.03m).Directory);
        File.Delete(ready); process = StartWorker("activate-pause", second.ModelId, ready, trigger);
        await WaitFile(ready); StopWorker(process);
        Assert.Equal(first.ModelId, registry.RlActiveId); Assert.True(await registry.ActivateRlAsync(second.ModelId));
    }
    [Fact] public async Task R07_TimeoutAndCancellationDoNotStealOwnershipAndLeaveEvidence()
    {
        using var registry = new ModelGenerationRegistry(RegistryRoot) { StoreLockTimeout = TimeSpan.FromMilliseconds(150) };
        string ready = Path.Combine(_root, "ready"), trigger = Path.Combine(_root, "trigger");
        var process = StartWorker("hold-store", "", ready, trigger);
        try
        {
            await WaitFile(ready); Assert.Throws<TimeoutException>(() => registry.ListRl());
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registry.ActivateRlAsync(new string('a', 64), cancelled.Token));
            using var waiting = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registry.ActivateRlAsync(new string('a', 64), waiting.Token));
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(RegistryRoot, ModelGenerationRegistry.Coordination.Folder), "failure_*.json"));
            StopWorker(process); Assert.Empty(registry.ListRl());
        }
        catch { StopWorker(process); throw; }
    }
    [Fact] public async Task R07_LocalMutationWaitIsBoundedAndCancelledWithoutChangingThePointer()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registry = new ModelGenerationRegistry(RegistryRoot, async (path, pointer) => {
            started.TrySetResult(); await release.Task;
            await AtomicJsonFile.SaveAsync(path, pointer, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }) { StoreLockTimeout = TimeSpan.FromMilliseconds(150) };
        var candidate = await registry.RegisterRlAsync(Candidate(.01m).Directory);
        var commit = registry.ActivateRlAsync(candidate.ModelId);
        try
        {
            await started.Task.WaitAsync(WorkerLimit);
            await Assert.ThrowsAsync<TimeoutException>(() => registry.ActivateRlAsync(candidate.ModelId));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registry.ActivateRlAsync(candidate.ModelId, cancellation.Token));
            Assert.Null(registry.RlActiveId);
        }
        finally { release.TrySetResult(); await commit; }
        Assert.Equal(candidate.ModelId, registry.RlActiveId);
    }
    private async Task<(ModelGenerationRegistry Registry, string First, string Second, string Pointer)> RecoveryFixture()
    {
        var registry = new ModelGenerationRegistry(RegistryRoot);
        var first = await registry.RegisterRlAsync(Candidate(.01m).Directory); await registry.ActivateRlAsync(first.ModelId);
        var second = await registry.RegisterRlAsync(Candidate(.02m).Directory); await registry.ActivateRlAsync(second.ModelId);
        return (registry, first.ModelId, second.ModelId, Path.Combine(RegistryRoot, RlFiles.RlFolder, RlFiles.Active));
    }
    [Theory] [InlineData("malformed")] [InlineData("schema")] [InlineData("missing")]
    public async Task R07_OnlyExplicitVerifiedBackupRecoveryChangesUnavailablePointer(string damage)
    {
        var fixture = await RecoveryFixture(); using var registry = fixture.Registry;
        byte[] backup = File.ReadAllBytes(fixture.Pointer + ".bak");
        if (damage == "missing") File.Delete(fixture.Pointer);
        else if (damage == "schema")
        { var node = JsonNode.Parse(File.ReadAllText(fixture.Pointer))!; node["schemaVersion"] = 999; File.WriteAllText(fixture.Pointer, node.ToJsonString()); }
        else File.WriteAllText(fixture.Pointer, "{");
        Assert.Null(registry.RlActiveId); Assert.NotEmpty(registry.ListRl());
        using var reopened = new ModelGenerationRegistry(RegistryRoot); Assert.Null(reopened.RlActiveId);
        var preview = registry.PreviewRlRecovery(RlFiles.Target); Assert.True(preview.Unavailable); Assert.Equal(fixture.First, preview.BackupModelId);
        Assert.Null(registry.RlActiveId); Assert.Equal(backup, File.ReadAllBytes(fixture.Pointer + ".bak"));
        Assert.True(await registry.RecoverRlPointerAsync(preview)); Assert.Equal(fixture.First, registry.RlActiveId);
        Assert.Equal(backup, File.ReadAllBytes(fixture.Pointer + ".bak")); Assert.Equal(fixture.First, reopened.RlActiveId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.RecoverRlPointerAsync(preview));
    }
    [Fact] public async Task R07_ChangedOrUnverifiedBackupAndHealthyActiveAreNeverRestored()
    {
        var fixture = await RecoveryFixture(); using var registry = fixture.Registry;
        var healthy = registry.PreviewRlRecovery(RlFiles.Target); Assert.False(healthy.Unavailable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.RecoverRlPointerAsync(healthy)); Assert.Equal(fixture.Second, registry.RlActiveId);
        File.WriteAllText(fixture.Pointer, "{"); var reviewed = registry.PreviewRlRecovery(RlFiles.Target);
        File.AppendAllText(fixture.Pointer + ".bak", " ");
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.RecoverRlPointerAsync(reviewed)); Assert.Null(registry.RlActiveId);
        var missing = JsonNode.Parse(File.ReadAllText(fixture.Pointer + ".bak"))!; missing["activeId"] = new string('f', 64);
        File.WriteAllText(fixture.Pointer + ".bak", missing.ToJsonString()); Assert.Null(registry.PreviewRlRecovery(RlFiles.Target).BackupModelId);
        File.WriteAllText(fixture.Pointer + ".bak", "{"); Assert.Null(registry.PreviewRlRecovery(RlFiles.Target).BackupModelId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.RecoverRlPointerAsync(registry.PreviewRlRecovery(RlFiles.Target)));
    }
    [Fact] public async Task R07_FailedRecoveryWritePreservesCorruptionAndVerifiedBackup()
    {
        var fixture = await RecoveryFixture(); fixture.Registry.Dispose(); File.WriteAllText(fixture.Pointer, "{");
        byte[] backup = File.ReadAllBytes(fixture.Pointer + ".bak");
        using var registry = new ModelGenerationRegistry(RegistryRoot) { RecoveryPointerWriter = (_, _) => Task.FromException(new IOException("simulated disk full")) };
        await Assert.ThrowsAsync<IOException>(() => registry.RecoverRlPointerAsync(registry.PreviewRlRecovery(RlFiles.Target)));
        Assert.Equal("{", File.ReadAllText(fixture.Pointer)); Assert.Equal(backup, File.ReadAllBytes(fixture.Pointer + ".bak")); Assert.Null(registry.RlActiveId);
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(RegistryRoot, ModelGenerationRegistry.Coordination.Folder), "failure_*.json"));
    }
    [Fact] public async Task R07_WrongTargetBackupAndCorruptManifestCannotSelectAnUnknownGeneration()
    {
        var fixture = await RecoveryFixture(); using var registry = fixture.Registry;
        string actorPointer = Path.Combine(RegistryRoot, RlFiles.RlFolder, RlContinuousContract.ActiveFile);
        File.Copy(fixture.Pointer + ".bak", actorPointer + ".bak"); File.WriteAllText(actorPointer, "{");
        Assert.Null(registry.RlContinuousActiveId); Assert.Equal(fixture.Second, registry.RlActiveId);
        Assert.Null(registry.PreviewRlRecovery(RlContinuousContract.Target).BackupModelId);
        File.WriteAllText(fixture.Pointer, "{");
        string manifest = Path.Combine(RegistryRoot, RlFiles.RlFolder, fixture.First, RlFiles.Manifest);
        File.WriteAllText(manifest, "{"); Assert.Null(registry.PreviewRlRecovery(RlFiles.Target).BackupModelId);
        Assert.NotNull(registry.GetRlGeneration(fixture.Second)); Assert.Null(registry.RlActiveId);
    }
    [Fact] public async Task R07_UnknownLeaseInformationRetainsFilesAndReturnedLeaseOutlivesRegistry()
    {
        var fixture = await RecoveryFixture(); using var registry = fixture.Registry;
        var lease = registry.AcquireRl(fixture.First)!;
        fixture.Registry.Dispose(); Assert.True(File.Exists(lease.ModelPath)); lease.Dispose();
        using var reopened = new ModelGenerationRegistry(RegistryRoot);
        string leaseDirectory = Path.Combine(RegistryRoot, ModelGenerationRegistry.Coordination.Folder,
            ModelGenerationRegistry.Coordination.LeaseFolder, ModelGenerationRegistry.Coordination.RlLease + "_" + fixture.First);
        Directory.CreateDirectory(leaseDirectory); File.WriteAllText(Path.Combine(leaseDirectory, "unknown"), "unverified ownership");
        for (int i = 0; i < RlContract.RollbackGenerationCount + 2; i++)
        { var generation = await reopened.RegisterRlAsync(Candidate(.10m + i / 100m).Directory); await reopened.ActivateRlAsync(generation.ModelId); }
        Assert.NotNull(reopened.GetRlGeneration(fixture.First)); Assert.True(File.Exists(lease.ModelPath));
    }
}
