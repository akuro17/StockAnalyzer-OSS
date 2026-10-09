#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.ReinforcementLearning;

namespace StockAnalyzer.Core.Services;

public sealed partial class ModelGenerationRegistry
{
    private void AddCheckpointInventory(List<RlArtifactEntry> entries, string directory, string runId, RlArtifactPolicy policy, bool verified)
    {
        string root = Path.Combine(directory, RlCheckpointContract.Folder); GuardArtifactPath(_artifactRunRoot!, root);
        if (!Directory.Exists(root)) return;
        var snapshots = Directory.EnumerateDirectories(root).Where(p => Guid.TryParseExact(Path.GetFileName(p), "N", out _))
            .OrderByDescending(p => Directory.GetCreationTimeUtc(p)).ThenBy(p => p, StringComparer.Ordinal).ToArray();
        var newest = snapshots.Take(policy.CheckpointKeepCount).ToHashSet(StringComparer.Ordinal);
        string? latest = null;
        try { latest = RlCheckpointStore.Read<RlCheckpointStore.Pointer>(Path.Combine(directory, RlCheckpointContract.Pointer)).Latest; }
        catch (Exception error) when (error is IOException or JsonException or InvalidDataException) { verified = false; }
        bool activeOwner = HasStoreLease(RunLeaseKind, runId);
        RlCheckpointStore.VerificationContext? context = null;
        try { context = RlCheckpointStore.CaptureVerification(directory, activeOwner); }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException) { }
        foreach (string snapshot in snapshots)
        {
            string id = Path.GetFileName(snapshot); var reasons = new List<string>(); var tree = InspectArtifactTree(_artifactRunRoot!, snapshot);
            DateTimeOffset created = DateTimeOffset.MinValue;
            try
            {
                if (context is null) throw new InvalidDataException("Unavailable: common checkpoint provenance is unverified.");
                created = RlCheckpointStore.Verify(directory, id, verifyExecutionFiles: false, activeOwner: activeOwner, context: context).CreatedUtc;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException)
            { reasons.Add(RlArtifactRetentionReason.UnknownOwner.ToString()); }
            if (newest.Contains(snapshot) || latest == id) reasons.Add(RlArtifactRetentionReason.Newest.ToString());
            if (HasStoreLease(RunLeaseKind, runId)) reasons.Add(RlArtifactRetentionReason.Lease.ToString());
            if (!verified) reasons.Add(RlArtifactRetentionReason.UnverifiedPointers.ToString());
            if (!policy.IsComplete) reasons.Add(RlArtifactRetentionReason.SettingsIncomplete.ToString());
            bool candidate = reasons.Count == 0;
            entries.Add(new(id, RlArtifactKind.Checkpoint.ToString(), snapshot, tree.Bytes, created, tree.Identity,
                (candidate ? new[] { RlArtifactRetentionReason.SurplusCheckpoint.ToString() } : reasons.ToArray()).ToImmutableArray(), candidate));
        }
    }
    internal JsonElement ReadCheckpointJob(RlCheckpointReview reviewed)
    {
        lock (_gate) { using var transaction = EnterStore(); ValidateCheckpointReview(reviewed);
            RlCheckpointStore.Verify(reviewed.RunDirectory, reviewed.CheckpointId);
            return RlCheckpointStore.Read<JsonElement>(Path.Combine(reviewed.RunDirectory, RlFiles.Job)); }
    }
    private bool HasResumableCheckpoint(string directory, out string? direction)
    {
        direction = null; string path = Path.Combine(directory, RlCheckpointContract.Pointer);
        if (!File.Exists(path))
        {
            string snapshots = Path.Combine(directory, RlCheckpointContract.Folder);
            GuardArtifactPath(_artifactRunRoot!, snapshots);
            if (!Directory.Exists(snapshots) || !Directory.EnumerateDirectories(snapshots).Any(p => Guid.TryParseExact(Path.GetFileName(p), "N", out _))) return false;
            try { direction = RlCheckpointStore.Read<JsonElement>(Path.Combine(directory, RlFiles.Job)).GetProperty("direction").GetProperty("model_id").GetString(); }
            catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException or InvalidDataException) { }
            return true;
        }
        try
        {
            GuardArtifactPath(_artifactRunRoot!, path);
            var pointer = RlCheckpointStore.Read<RlCheckpointStore.Pointer>(path);
            direction = pointer.DirectionId;
            return pointer.SchemaVersion != RlCheckpointContract.SchemaVersion || pointer.Resumable;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException)
        { return true; } // Ambiguous provenance retains the run.
    }
    private string ReviewIdentity(string directory, string id)
    {
        string pointer = Path.Combine(directory, RlCheckpointContract.Pointer);
        string manifest = Path.Combine(directory, RlCheckpointContract.Folder, id, RlCheckpointContract.Manifest);
        GuardArtifactPath(_artifactRunRoot!, pointer); GuardArtifactPath(_artifactRunRoot!, manifest);
        return ContentHash.OfText((File.Exists(pointer) ? ContentHash.OfFile(pointer) : string.Empty) + ":" +
            (File.Exists(manifest) ? ContentHash.OfFile(manifest) : string.Empty) + ":" + id);
    }
    public IReadOnlyList<RlCheckpointReview> ListRlCheckpoints() => ListRlCheckpoints(default);
    public IReadOnlyList<RlCheckpointReview> ListRlCheckpoints(CancellationToken ct)
    {
        lock (_gate)
        {
            using var transaction = EnterStore(ct); var rows = new List<RlCheckpointReview>();
            if (_artifactRunRoot is null || !Directory.Exists(_artifactRunRoot)) return rows;
            foreach (string directory in Directory.EnumerateDirectories(_artifactRunRoot))
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var runId)) continue;
                GuardArtifactPath(_artifactRunRoot, directory);
                if (!HasResumableCheckpoint(directory, out _)) continue;
                string checkpoints = Path.Combine(directory, RlCheckpointContract.Folder); GuardArtifactPath(_artifactRunRoot, checkpoints);
                if (!Directory.Exists(checkpoints)) continue;
                Exception? executionError = null;
                RlCheckpointStore.VerificationContext? context = null;
                try
                {
                    context = RlCheckpointStore.CaptureVerification(directory);
                    RlExecutionIdentity.Verify(Path.Combine(directory, RlFiles.Dependencies));
                }
                catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException)
                { executionError = error; }
                foreach (string child in Directory.EnumerateDirectories(checkpoints).OrderByDescending(p => Directory.GetCreationTimeUtc(p)))
                {
                    string id = Path.GetFileName(child); if (!Guid.TryParseExact(id, "N", out _)) continue;
                    string identity = ReviewIdentity(directory, id);
                    try
                    {
                        if (executionError is not null) throw new InvalidDataException("Unavailable: execution identity differs.", executionError);
                        var manifest = RlCheckpointStore.Verify(directory, id, verifyExecutionFiles: false, context: context);
                        rows.Add(new(runId, directory, id, manifest.Seed, manifest.Steps, manifest.Phase, identity, true, null));
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException)
                    { rows.Add(new(runId, directory, id, 0, 0, "Unavailable", identity, false, error.Message)); }
                }
            }
            return rows;
        }
    }
    private void ValidateCheckpointReview(RlCheckpointReview review)
    {
        if (_artifactRunRoot is null || Path.GetFullPath(review.RunDirectory) != Path.Combine(_artifactRunRoot, review.RunId.ToString("N")))
            throw new InvalidDataException("Checkpoint review escapes the owned root.");
        GuardArtifactPath(_artifactRunRoot, review.RunDirectory);
        var run = JsonSerializer.Deserialize<OwnedRun>(File.ReadAllText(Path.Combine(review.RunDirectory, ArtifactRunFile)), RlArtifactSettings.Options)!;
        if (run.RunId != review.RunId || run.RegistryRoot != _root || HasStoreLease(RunLeaseKind, review.RunId.ToString("N"))
            || !HasResumableCheckpoint(review.RunDirectory, out _) || review.Identity != ReviewIdentity(review.RunDirectory, review.CheckpointId))
            throw new InvalidOperationException("Checkpoint review or run ownership changed; review again.");
    }
    public async Task AbandonRlResumeAsync(RlCheckpointReview reviewed, CancellationToken ct = default)
    {
        await EnterMutationAsync(ct).ConfigureAwait(false);
        try
        {
            using var transaction = EnterStore(ct); lock (_gate)
            {
                ValidateCheckpointReview(reviewed);
                string path = Path.Combine(reviewed.RunDirectory, RlCheckpointContract.Pointer);
                var job = RlCheckpointStore.Read<JsonElement>(Path.Combine(reviewed.RunDirectory, RlFiles.Job));
                var pointer = new RlCheckpointStore.Pointer(RlCheckpointContract.SchemaVersion, false, reviewed.CheckpointId,
                    job.GetProperty("direction").GetProperty("model_id").GetString()!);
                string descriptor = Path.Combine(reviewed.RunDirectory, ArtifactRunFile);
                GuardArtifactPath(_artifactRunRoot!, descriptor + AtomicJsonFile.TemporarySuffix);
                GuardArtifactPath(_artifactRunRoot!, descriptor + AtomicJsonFile.BackupSuffix);
                var owned = JsonSerializer.Deserialize<OwnedRun>(File.ReadAllText(descriptor), RlArtifactSettings.Options)!;
                if (owned.State == RlArtifactRunState.Running)
                    AtomicJsonFile.SaveAsync(descriptor, owned with { State = RlArtifactRunState.Cancelled, FinishedUtc = ArtifactClock() }, RlArtifactSettings.Options).GetAwaiter().GetResult();
                RlCheckpointStore.Save(path, pointer with { Resumable = false });
            }
        }
        finally { _mutation.Release(); }
    }
    internal RlCheckpointStore CreateCheckpointStore(string directory, Guid runId, Func<bool> pause, string? checkpoint = null, decimal preparationSeconds = 0)
    {
        if (_artifactSettings is null || directory != Path.Combine(_artifactRunRoot!, runId.ToString("N")))
            throw new InvalidOperationException("Unavailable: configured owned RL artifact storage is required for continuation.");
        return new(directory, runId, additional =>
        {
            var preview = PreviewRlArtifacts()!;
            return preview.UsedBytes + additional < preview.Policy.QuotaBytes && preview.FreeBytes - additional >= preview.Policy.MinimumFreeBytes;
        }, operation =>
        {
            EnterMutationAsync(CancellationToken.None).GetAwaiter().GetResult();
            try { using var transaction = EnterStore(); lock (_gate) operation(); }
            finally { _mutation.Release(); }
        }, pause, checkpoint, preparationSeconds);
    }
    internal async Task<RlArtifactRunLease> ResumeArtifactRunAsync(RlCheckpointReview reviewed, CancellationToken ct)
    {
        await EnterMutationAsync(ct).ConfigureAwait(false); IDisposable? lease = null;
        try
        {
            using var transaction = EnterStore(ct); lock (_gate)
            {
                ValidateCheckpointReview(reviewed);
                if (!reviewed.Verified) throw new InvalidDataException("Unavailable: selected checkpoint is unverified.");
                RlCheckpointStore.Verify(reviewed.RunDirectory, reviewed.CheckpointId);
                var preview = PreviewArtifactsCore()!; preview.Policy.Validate(true);
                if (preview.UsedBytes >= preview.Policy.QuotaBytes || preview.FreeBytes < preview.Policy.MinimumFreeBytes)
                    throw new InvalidOperationException("Unavailable: insufficient capacity for manual resume.");
                string descriptor = Path.Combine(reviewed.RunDirectory, ArtifactRunFile);
                var run = JsonSerializer.Deserialize<OwnedRun>(File.ReadAllText(descriptor), RlArtifactSettings.Options)!;
                lease = CreateStoreLease(RunLeaseKind, reviewed.RunId.ToString("N"));
                run = run with { FinishedUtc = null, State = RlArtifactRunState.Running };
                AtomicJsonFile.SaveAsync(descriptor, run, RlArtifactSettings.Options).GetAwaiter().GetResult();
                return new(reviewed.RunDirectory, run.Policy, lease, (state, model, token) => FinishArtifactRunAsync(descriptor, run, state, model, token));
            }
        }
        catch { lease?.Dispose(); throw; }
        finally { _mutation.Release(); }
    }
}
