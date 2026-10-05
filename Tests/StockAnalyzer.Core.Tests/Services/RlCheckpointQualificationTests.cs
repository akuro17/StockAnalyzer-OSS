#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

[Collection("ChildProcess")]
public sealed class RlCheckpointQualificationTests
{
    public sealed class CheckpointQualificationAttribute : FactAttribute
    { public CheckpointQualificationAttribute() { if (Environment.GetEnvironmentVariable("SA_RL_R09_QUALIFICATION") != "1") Skip = "Owned actual same-environment T16 continuation opt-in."; } }
    [CheckpointQualification]
    public async Task ActualPpoPauseResumeMatchesUninterruptedPolicyOptimizerRngDiagnosticsAndFinancialResults()
        => await RunAsync(smoke: true);
    [CheckpointQualification]
    public async Task ActualFullPpoWorkerFailureSeedContinuationAndOosContinuationMatchUninterruptedTraining()
        => await RunAsync(smoke: false);
    [CheckpointQualification]
    public async Task OwnedCheckpointsRejectCorruptionStaleReviewChangedInputsAndCapacityFailureAndProtectManualCleanup()
    {
        string output = Environment.GetEnvironmentVariable("SA_RL_R09_OUTPUT")!;
        using var config = JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("SA_RL_R07_QUALIFICATION_CONFIG")!));
        using var sourceJob = JsonDocument.Parse(File.ReadAllText(Path.Combine(config.RootElement.GetProperty("LaterActor").GetString()!, RlFiles.Job)));
        var captured = sourceJob.RootElement;
        string root = Path.Combine(PathDiscovery.ResolveDataPath(null, "Data"), "R09TestRuns", Guid.NewGuid().ToString("N"));
        using var registry = new ModelGenerationRegistry(Path.Combine(root, "Generations"));
        registry.ConfigureRlArtifacts(root, new RlArtifactSettings(Path.Combine(root, "Config", RlArtifactSettings.FileName)));
        var policy = new RlArtifactPolicy(NonactiveRetentionDays: 0, NonactiveKeepCount: 0);
        await registry.SaveRlArtifactPolicyAsync(policy);
        var direction = await registry.RegisterRlAsync(config.RootElement.GetProperty("LaterDirection").GetString()!);
        var job = new RlTrainingJob(captured.GetProperty("symbol").GetString()!,
            captured.GetProperty("start_date").Deserialize<DateOnly>(RlFiles.Json), captured.GetProperty("end_date_exclusive").Deserialize<DateOnly>(RlFiles.Json),
            captured.GetProperty("costs").Deserialize<BacktestConfiguration>(RlFiles.Json)!, SmokeOnly: true) { FrozenDirectionModelId = direction.ModelId };
        var training = new RlTrainingService(registry, new TrainingJobGate(Path.Combine(root, "job-locks")), dataRoot: root);
        using var pause = new CancellationTokenSource();
        var paused = await Assert.ThrowsAsync<RlTrainingPausedException>(() => training.RunResumableAsync(job, pause.Token,
            new InlineProgress(p => { if (p.Message.StartsWith("Checkpoint saved: selected;", StringComparison.Ordinal)) pause.Cancel(); })));
        var rows = registry.ListRlCheckpoints(); Assert.True(rows.Count >= 3); Assert.All(rows, row => Assert.True(row.Verified, row.Error));
        var latest = rows[0]; var older = rows[1];
        string pointerPath = Path.Combine(paused.Directory, RlCheckpointContract.Pointer);
        byte[] pointer = File.ReadAllBytes(pointerPath);
        await File.WriteAllTextAsync(pointerPath, System.Text.Encoding.UTF8.GetString(pointer) + "\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() => training.ResumeAsync(older, CancellationToken.None));
        await File.WriteAllBytesAsync(pointerPath, pointer);
        string barsPath = Path.Combine(paused.Directory, RlFiles.Bars); byte[] bars = File.ReadAllBytes(barsPath);
        await File.WriteAllTextAsync(barsPath, System.Text.Encoding.UTF8.GetString(bars) + "\n");
        Assert.All(registry.ListRlCheckpoints(), row => Assert.False(row.Verified));
        await Assert.ThrowsAsync<InvalidDataException>(() => training.ResumeAsync(older, CancellationToken.None));
        await File.WriteAllBytesAsync(barsPath, bars);
        string tensorPath = Path.Combine(paused.Directory, RlCheckpointContract.Folder, latest.CheckpointId, RlCheckpointContract.PythonState);
        byte[] tensor = File.ReadAllBytes(tensorPath); byte[] broken = tensor.ToArray(); broken[broken.Length / 2] ^= 1;
        await File.WriteAllBytesAsync(tensorPath, broken);
        Assert.False(registry.ListRlCheckpoints().Single(r => r.CheckpointId == latest.CheckpointId).Verified);
        Assert.True(registry.ListRlCheckpoints().Single(r => r.CheckpointId == older.CheckpointId).Verified);
        await Assert.ThrowsAsync<InvalidDataException>(() => training.ResumeAsync(latest, CancellationToken.None));
        Assert.Equal(pointer, File.ReadAllBytes(pointerPath)); // No automatic older fallback.
        await File.WriteAllBytesAsync(tensorPath, tensor);
        using (var lease = await registry.ResumeArtifactRunAsync(older, CancellationToken.None))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => registry.AbandonRlResumeAsync(older));
            Assert.All(registry.PreviewRlArtifacts()!.Entries.Where(e => e.Kind == RlArtifactKind.Checkpoint.ToString()),
                e => { Assert.False(e.Candidate); Assert.Contains("Lease", e.Reasons); });
            await lease.FinishAsync(RlArtifactRunState.Failed);
        }
        using (var store = registry.CreateCheckpointStore(paused.Directory, paused.RunId, () => false))
        {
            var core = RlCheckpointStore.Read<JsonElement>(Path.Combine(paused.Directory, RlCheckpointContract.Folder, older.CheckpointId, RlCheckpointContract.CoreState));
            var pending = JsonSerializer.SerializeToElement(store.Prepare(core, older.Seed, older.Steps, older.Phase), RlFiles.Json);
            File.Copy(tensorPath, Path.Combine(pending.GetProperty("directory").GetString()!, RlCheckpointContract.PythonState));
            await registry.SaveRlArtifactPolicyAsync(policy with { QuotaGiB = 2m / RlArtifactSettings.BytesPerGiB });
            Assert.Throws<InvalidOperationException>(() => store.Commit(pending.GetProperty("id").GetString()!));
            Assert.Equal(pointer, File.ReadAllBytes(pointerPath));
            await registry.SaveRlArtifactPolicyAsync(policy);
        }
        CheckActiveEpisodeReplay(paused.Directory, root);
        var preview = registry.PreviewRlArtifacts()!;
        Assert.Equal(2, preview.Entries.Count(e => e.Kind == RlArtifactKind.Checkpoint.ToString() && !e.Candidate));
        Assert.Contains(preview.Entries, e => e.Kind == RlArtifactKind.Checkpoint.ToString() && e.Candidate);
        var cleanup = await registry.CleanupRlArtifactsAsync(preview); Assert.Empty(cleanup.Failures);
        Assert.True(Directory.Exists(Path.Combine(paused.Directory, RlCheckpointContract.Folder, older.CheckpointId)));
        var resumed = await training.ResumeAsync(older, CancellationToken.None); Assert.True(File.Exists(resumed.EvaluationPath));
        using var earlyPause = new CancellationTokenSource(); earlyPause.Cancel();
        var abandoned = await Assert.ThrowsAsync<RlTrainingPausedException>(() => training.RunResumableAsync(job, earlyPause.Token));
        var abandonedReview = Assert.Single(registry.ListRlCheckpoints());
        Assert.Contains("Resumable", registry.PreviewRlArtifacts()!.Entries.Single(e => e.Id == abandoned.RunId.ToString("N")).Reasons);
        await registry.AbandonRlResumeAsync(abandonedReview);
        Assert.Empty(registry.ListRlCheckpoints()); Assert.True(Directory.Exists(abandoned.Directory));
        Assert.DoesNotContain("Resumable", registry.PreviewRlArtifacts()!.Entries.Single(e => e.Id == abandoned.RunId.ToString("N")).Reasons);
        await File.WriteAllTextAsync(Path.Combine(output, "checkpoint_safety_actual_evidence.json"), JsonSerializer.Serialize(new
        { root, stale_review_rejected = true, changed_source_rejected = true, corrupt_latest_rejected = true,
            no_automatic_fallback = true, manual_older_resume_completed = true, live_run_lease_protected = true,
            capacity_failure_preserved_previous_commit = true, newest_two_manual_cleanup = true,
            abandon_only_releases_protection = true, active_raw_action_replay_exact_and_charged = true }, RlFiles.Json));
    }
    private static void CheckActiveEpisodeReplay(string directory, string root)
    {
        using var job = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, RlFiles.Job)));
        var metadata = job.RootElement.GetProperty("metadata").Deserialize<Dictionary<string, string>>(RlFiles.Json)!;
        var limits = job.RootElement.GetProperty("resource_limits").Deserialize<TrainingResourceLimits>(RlFiles.Json)!;
        var source = RlSourceSnapshot.Load(Path.Combine(directory, RlFiles.SourceFolder), limits, smokeOnly: true);
        var costs = RlContract.CreateConfiguration(job.RootElement.GetProperty("costs").Deserialize<BacktestConfiguration>(RlFiles.Json)!);
        var run = Guid.NewGuid(); string session = Guid.NewGuid().ToString("N");
        JsonElement Frame(long sequence, string method, object payload, string id) => JsonSerializer.SerializeToElement(new
        { protocol_version = RlContract.SchemaVersion, run_id = run.ToString("N"), sequence, session_id = id,
            contract_hash = RlContinuousContract.ContractHash, method, payload }, RlFiles.Json);
        using var original = new RlIpcHost(run, source, costs, directory, metadata, null, continuous: true);
        original.Handle(Frame(1, "Ready", new { temporal_producer_revision = RlTemporalContract.ProducerRevision }, session), default);
        original.Handle(Frame(2, "Reset", new { role = "train", block_id = source.Catalog.Train[0].Id, seed = RlContract.CandidateSeeds[0], checkpoint = 0 }, session), default);
        var actions = new { actions = new[] { 1f, .5f } };
        for (int i = 0; i < 3; i++) original.Handle(Frame(3 + i, "Step", actions, session), default);
        object saved = typeof(RlIpcHost).GetMethod("CaptureCheckpoint", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(original, null)!;
        string scratch = Path.Combine(root, "active-replay"); string id = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(scratch);
        using (var charged = new RlCheckpointStore(scratch, run, _ => true, action => action(), () => false))
            charged.Charge(original.Transitions, original.EpisodeStarts);
        string snapshot = Path.Combine(scratch, RlCheckpointContract.Folder, id); Directory.CreateDirectory(snapshot);
        RlCheckpointStore.Save(Path.Combine(snapshot, RlCheckpointContract.CoreState), saved);
        using var store = new RlCheckpointStore(scratch, run, _ => true, action => action(), () => false, id);
        using var restored = new RlIpcHost(run, source, costs, directory, metadata, null, continuous: true) { Checkpoints = store };
        restored.Handle(Frame(1, "Ready", new { temporal_producer_revision = RlTemporalContract.ProducerRevision }, session), default);
        var replay = JsonSerializer.SerializeToElement(restored.Handle(Frame(2, "RestoreCheckpoint", new { }, session), default), RlFiles.Json);
        string newSession = replay.GetProperty("result").GetProperty("sessions").GetProperty(session).GetString()!; Assert.NotEqual(session, newSession);
        Assert.Equal(6, restored.Transitions); Assert.Equal(2, restored.EpisodeStarts);
        Assert.Equal(JsonSerializer.SerializeToElement(original.Handle(Frame(6, "Step", actions, session), default), RlFiles.Json).GetProperty("result").GetRawText(),
            JsonSerializer.SerializeToElement(restored.Handle(Frame(3, "Step", actions, newSession), default), RlFiles.Json).GetProperty("result").GetRawText());
        Assert.Equal(7, restored.Transitions); Assert.Equal(7, store.Used.Transitions);
        restored.AccountQualificationWork(start: false, success: false);
        Assert.Equal(8, store.Used.Transitions); // Qualification failures are durable before success.
        restored.CountQualificationTransitions(12, 3);
        Assert.Equal(20, store.Used.Transitions); Assert.Equal(5, store.Used.Starts);
    }
    private static async Task RunAsync(bool smoke)
    {
        string output = Environment.GetEnvironmentVariable("SA_RL_R09_OUTPUT")!;
        using var config = JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("SA_RL_R07_QUALIFICATION_CONFIG")!));
        string actor = config.RootElement.GetProperty("LaterActor").GetString()!;
        using var sourceJob = JsonDocument.Parse(File.ReadAllText(Path.Combine(actor, RlFiles.Job)));
        var captured = sourceJob.RootElement;
        string root = Path.Combine(PathDiscovery.ResolveDataPath(null, "Data"), "R09TestRuns", Guid.NewGuid().ToString("N"));
        using var registry = new ModelGenerationRegistry(Path.Combine(root, "Generations"));
        registry.ConfigureRlArtifacts(root, new RlArtifactSettings(Path.Combine(root, "Config", RlArtifactSettings.FileName)));
        await registry.SaveRlArtifactPolicyAsync(new(NonactiveRetentionDays: 0, NonactiveKeepCount: 0));
        var direction = await registry.RegisterRlAsync(config.RootElement.GetProperty("LaterDirection").GetString()!);
        var job = new RlTrainingJob(captured.GetProperty("symbol").GetString()!,
            captured.GetProperty("start_date").Deserialize<DateOnly>(RlFiles.Json), captured.GetProperty("end_date_exclusive").Deserialize<DateOnly>(RlFiles.Json),
            captured.GetProperty("costs").Deserialize<BacktestConfiguration>(RlFiles.Json)!, SmokeOnly: smoke)
            { FrozenDirectionModelId = direction.ModelId };
        int ownedWorker = 0;
        var training = new RlTrainingService(registry, new TrainingJobGate(Path.Combine(root, "job-locks")), dataRoot: root)
        { OwnedWorkerStarted = pid => ownedWorker = pid };
        var uninterrupted = await training.RunResumableAsync(job, CancellationToken.None);
        using var pause = new CancellationTokenSource(); pause.Cancel();
        var paused = await Assert.ThrowsAsync<RlTrainingPausedException>(() => training.RunResumableAsync(job, pause.Token));
        var review = Assert.Single(registry.ListRlCheckpoints()); Assert.True(review.Verified, review.Error);
        Assert.Equal(RlContract.SmokeCheckpointInterval, review.Steps);
        Assert.Contains("Resumable", registry.PreviewRlArtifacts()!.Entries.Single(e => e.Id == paused.RunId.ToString("N")).Reasons);
        Assert.Contains("Dependency", registry.PreviewRlArtifacts()!.Entries.Single(e => e.Id == direction.ModelId).Reasons);
        if (!smoke)
        {
            bool killed = false;
            var failure = await Record.ExceptionAsync(() => training.ResumeAsync(review, CancellationToken.None,
                new InlineProgress(p =>
                {
                    if (!killed && p.Message == $"Checkpoint saved: train; seed={RlContract.CandidateSeeds[1]}" && p.CompletedSteps == RlContinuousContract.ValidationCheckpointInterval)
                    {
                        killed = true; using var worker = Process.GetProcessById(ownedWorker); worker.Kill(entireProcessTree: true);
                    }
                })));
            Assert.True(killed); Assert.NotNull(failure); Assert.IsNotType<RlTrainingPausedException>(failure);
            review = registry.ListRlCheckpoints().First(); Assert.Equal(RlContract.CandidateSeeds[1], review.Seed);
            using var oosPause = new CancellationTokenSource();
            await Assert.ThrowsAsync<RlTrainingPausedException>(() => training.ResumeAsync(review, oosPause.Token,
                new InlineProgress(p => { if (p.Message.StartsWith("Checkpoint saved: oos;", StringComparison.Ordinal)) oosPause.Cancel(); })));
            review = registry.ListRlCheckpoints().First(); Assert.Equal("oos", review.Phase);
        }
        var resumed = await training.ResumeAsync(review, CancellationToken.None);
        Assert.Empty(registry.ListRlCheckpoints());
        using var originalReport = JsonDocument.Parse(File.ReadAllText(uninterrupted.EvaluationPath));
        using var resumedReport = JsonDocument.Parse(File.ReadAllText(resumed.EvaluationPath));
        foreach (string field in new[] { "candidate_seed", "checkpoint_step", "validation_episodes", "oos_episodes", "candidate_evaluations", "baselines" })
            Assert.Equal(originalReport.RootElement.GetProperty(field).GetRawText(), resumedReport.RootElement.GetProperty(field).GetRawText());
        string Last(string directory) => Directory.EnumerateDirectories(Path.Combine(directory, RlCheckpointContract.Folder))
            .Where(path => File.Exists(Path.Combine(path, RlCheckpointContract.Manifest)))
            .Select(path => (Path: path, Data: RlCheckpointStore.Read<RlCheckpointStore.Manifest>(Path.Combine(path, RlCheckpointContract.Manifest))))
            .Single(row => row.Data.Phase == "worker_complete").Path;
        string pythonRoot = Directory.GetParent(Path.GetDirectoryName(PythonScriptLocator.Resolve(RlContinuousContract.TrainerScript))!)!.FullName;
        var start = new ProcessStartInfo(Path.Combine(pythonRoot, RlFiles.VirtualEnvironment, OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = pythonRoot };
        start.ArgumentList.Add(Path.Combine(Directory.GetParent(pythonRoot)!.FullName, "Tests", "StockAnalyzer.Core.Tests", "Assets", "check_rl_resume.py"));
        start.ArgumentList.Add(Last(uninterrupted.Directory)); start.ArgumentList.Add(Last(resumed.Directory));
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, await stderr);
        string label = smoke ? "smoke" : "full";
        await File.WriteAllTextAsync(Path.Combine(output, "strict_continuation_tensor_" + label + "_evidence.json"), await stdout);
        await File.WriteAllTextAsync(Path.Combine(output, "actual_continuation_" + label + "_evidence.json"), JsonSerializer.Serialize(new
        { root, uninterrupted, resumed, pause_boundary = review.Steps, policy_optimizer_rng_exact = true,
            decimal_evaluation_equal = true, frozen_dependency_protected = true }, RlFiles.Json));
    }
    private sealed class InlineProgress(Action<RlTrainingProgress> report) : IProgress<RlTrainingProgress>
    { public void Report(RlTrainingProgress value) => report(value); }
}
