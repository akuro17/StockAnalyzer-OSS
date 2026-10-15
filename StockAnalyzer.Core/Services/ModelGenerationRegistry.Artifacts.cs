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
    internal const string ArtifactRunFile = "owned_run.json";
    private const string RunLeaseKind = "run";
    private const string ArtifactTrashFolder = "artifact-trash";
    private const string DeletionTicketSuffix = ".json";
    private string ArtifactTrashRoot => Path.Combine(_root, Coordination.Folder, ArtifactTrashFolder);
    private string? _artifactDataRoot, _artifactRunRoot;
    private RlArtifactSettings? _artifactSettings;
    internal Func<DateTimeOffset> ArtifactClock { get; init; } = () => DateTimeOffset.UtcNow;
    internal Func<string, long> ArtifactFreeSpace { get; init; } = root => new DriveInfo(ArtifactVolume(root)).AvailableFreeSpace;
    private sealed record OwnedRun(int SchemaVersion, Guid RunId, string RegistryRoot, DateTimeOffset StartedUtc,
        DateTimeOffset? FinishedUtc, RlArtifactRunState State, string? ModelId, RlArtifactPolicy Policy);
    private sealed record DeletionTicket(int SchemaVersion, string Id, string Kind, string OriginalPath, DateTimeOffset CreatedUtc);

    /// <summary>Pairs explicit RL roots with the saved policy. Production DI enables this capability.</summary>
    public void ConfigureRlArtifacts(string dataRoot, RlArtifactSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            using var transaction = EnterStore();
            if (_artifactSettings is not null) throw new InvalidOperationException("RL artifact roots are already configured.");
            string root = Path.GetFullPath(dataRoot);
            string runs = Path.Combine(root, RlFiles.TrainingFolder, RlFiles.RlFolder);
            GuardArtifactPath(runs, runs); RlArtifactSettings.GuardPath(_root);
            if (!SameArtifactVolume(_root, runs)) throw new InvalidDataException("RL artifact roots must share one volume.");
            if (ContainsArtifactPath(runs, _root) || ContainsArtifactPath(_root, runs)) throw new InvalidDataException("RL artifact roots overlap.");
            _artifactDataRoot = root; _artifactRunRoot = runs; _artifactSettings = settings;
        }
    }
    public RlArtifactPolicy? RlArtifactPolicy => _artifactSettings?.Snapshot;
    public async Task SaveRlArtifactPolicyAsync(RlArtifactPolicy policy, CancellationToken ct = default)
    {
        await EnterMutationAsync(ct).ConfigureAwait(false);
        try
        {
            using var transaction = EnterStore(ct);
            await (_artifactSettings ?? throw new NotSupportedException("RL artifact management is not configured.")).SaveAsync(policy, ct).ConfigureAwait(false);
        }
        finally { _mutation.Release(); }
    }
    private static bool ContainsArtifactPath(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        path = Path.GetFullPath(path);
        return path.Equals(root, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }
    private static string ArtifactVolume(string path)
    {
        string full = Path.GetFullPath(path);
        return DriveInfo.GetDrives().Where(d => ContainsArtifactPath(d.Name, full)).OrderByDescending(d => d.Name.Length)
            .Select(d => d.Name).FirstOrDefault() ?? throw new InvalidDataException("Artifact volume cannot be verified.");
    }
    private static bool SameArtifactVolume(string a, string b) => string.Equals(ArtifactVolume(a), ArtifactVolume(b),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static void GuardArtifactPath(string root, string path)
    {
        if (!ContainsArtifactPath(root, path)) throw new InvalidDataException("Artifact operation escapes its explicit root.");
        RlArtifactSettings.GuardPath(path);
        if (!SameArtifactVolume(root, path)) throw new InvalidDataException("Artifact path crosses volumes.");
    }
    private (long Bytes, string Identity, DateTimeOffset NewestWriteUtc) InspectArtifactTree(string root, string path)
    {
        GuardArtifactPath(root, path);
        var parts = new List<string>(); long bytes = 0; DateTimeOffset newest = DateTimeOffset.MinValue;
        void Inspect(string directory)
        {
            GuardArtifactPath(root, directory);
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory).OrderBy(p => p, StringComparer.Ordinal))
            {
                GuardArtifactPath(root, entry);
                if (Directory.Exists(entry)) Inspect(entry);
                else
                {
                    var file = new FileInfo(entry); bytes = checked(bytes + file.Length);
                    if (file.LastWriteTimeUtc > newest) newest = file.LastWriteTimeUtc;
                    parts.Add(Path.GetRelativePath(path, entry) + ":" + file.Length + ":" + file.LastWriteTimeUtc.Ticks);
                    if (Path.GetFileName(entry) is ArtifactRunFile or RlFiles.Manifest) parts.Add(ContentHash.OfFile(entry));
                }
            }
        }
        if (Directory.Exists(path)) Inspect(path);
        return (bytes, ContentHash.OfText(string.Join("\n", parts)), newest);
    }

    private Dictionary<string, HashSet<string>> RlProtectedGenerations(out bool verified)
    {
        EnsureRlLoaded(); verified = _rlPointerError is null && _rlContinuousPointerError is null;
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Add(string id, RlArtifactRetentionReason reason)
        { if (!result.TryGetValue(id, out var reasons)) result[id] = reasons = new(); reasons.Add(reason.ToString()); }
        foreach (var pointer in new[] { _rlPointer, _rlContinuousPointer })
        {
            if (pointer.ActiveId is { } id) Add(id, RlArtifactRetentionReason.Active);
            foreach (string prior in pointer.History) Add(prior, RlArtifactRetentionReason.Rollback);
        }
        foreach (string target in new[] { RlFiles.Target, RlContinuousContract.Target })
        {
            string backup = RlPointerPath(target) + Coordination.BackupSuffix;
            if (!File.Exists(backup)) continue;
            try
            {
                var pointer = ReadStorePointer(backup, RlContract.RollbackGenerationCount, false); ValidateRlPointer(pointer, target);
                if (pointer.ActiveId is { } id) Add(id, RlArtifactRetentionReason.Backup);
                foreach (string prior in pointer.History) Add(prior, RlArtifactRetentionReason.Backup);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException)
            { verified = false; RecordStoreFailure("rl-artifact-backup", error); }
        }
        foreach (var generation in _rlKnown.Values)
        {
            if (generation.Target == RlContinuousContract.Target && generation.FrozenDirectionModelId is { } direction)
                Add(direction, RlArtifactRetentionReason.Dependency);
            if (_leases.GetValueOrDefault(RlFiles.Target + ":" + generation.ModelId) > 0 || HasStoreLease(Coordination.RlLease, generation.ModelId))
                Add(generation.ModelId, RlArtifactRetentionReason.Lease);
        }
        if (_artifactRunRoot is not null && Directory.Exists(_artifactRunRoot))
            foreach (string directory in Directory.EnumerateDirectories(_artifactRunRoot))
                if (HasResumableCheckpoint(directory, out string? direction))
                {
                    if (direction is not null && IsHash(direction)) Add(direction, RlArtifactRetentionReason.Dependency);
                    else verified = false;
                }
        return result;
    }
    public RlArtifactPreview? PreviewRlArtifacts() => PreviewRlArtifacts(default);
    public RlArtifactPreview? PreviewRlArtifacts(CancellationToken ct)
    {
        lock (_gate) { using var transaction = EnterStore(ct); return PreviewArtifactsCore(); }
    }
    private RlArtifactPreview? PreviewArtifactsCore()
    {
        if (_artifactSettings is null) return null;
        var policy = _artifactSettings.Snapshot; policy.Validate();
        GuardArtifactPath(_artifactRunRoot!, _artifactRunRoot!);
        var protection = RlProtectedGenerations(out bool verified);
        if (Directory.EnumerateDirectories(RlRoot).Any(d => IsHash(Path.GetFileName(d)) && !_rlKnown.ContainsKey(Path.GetFileName(d)))
            || _rlKnown.Values.Any(g => !VerifyRlBytes(g))) verified = false;
        DateTimeOffset now = ArtifactClock();
        string?[] active = { _rlPointer.ActiveId, _rlContinuousPointer.ActiveId };
        var newest = _rlKnown.Values.Where(g => !active.Contains(g.ModelId)).OrderByDescending(g => g.CreatedUtc)
            .ThenBy(g => g.ModelId, StringComparer.Ordinal).Take(policy.NonactiveKeepCount ?? 0).Select(g => g.ModelId).ToHashSet(StringComparer.Ordinal);
        var entries = new List<RlArtifactEntry>();
        foreach (string directory in Directory.EnumerateDirectories(RlRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            string id = Path.GetFileName(directory); var tree = InspectArtifactTree(RlRoot, directory);
            bool known = _rlKnown.TryGetValue(id, out var generation);
            var reasons = protection.TryGetValue(id, out var retained) ? new HashSet<string>(retained) : new();
            if (!known) reasons.Add(RlArtifactRetentionReason.UnknownOwner.ToString());
            if (!policy.IsComplete) reasons.Add(RlArtifactRetentionReason.SettingsIncomplete.ToString());
            if (!verified) reasons.Add(RlArtifactRetentionReason.UnverifiedPointers.ToString());
            if (newest.Contains(id)) reasons.Add(RlArtifactRetentionReason.Newest.ToString());
            if (known && (!policy.NonactiveRetentionDays.HasValue || now - generation!.CreatedUtc <= TimeSpan.FromDays(policy.NonactiveRetentionDays.Value)))
                reasons.Add(RlArtifactRetentionReason.NotExpired.ToString());
            bool candidate = reasons.Count == 0;
            entries.Add(new(id, (known ? RlArtifactKind.Generation : RlArtifactKind.UnknownGeneration).ToString(), directory, tree.Bytes,
                known ? generation!.CreatedUtc : DateTimeOffset.MinValue, tree.Identity,
                (candidate ? new[] { RlArtifactRetentionReason.ExpiredGeneration.ToString() } : reasons.OrderBy(r => r, StringComparer.Ordinal).ToArray()).ToImmutableArray(), candidate));
        }
        foreach (string directory in Directory.Exists(_artifactRunRoot) ? Directory.EnumerateDirectories(_artifactRunRoot) : Array.Empty<string>())
        {
            string id = Path.GetFileName(directory); var tree = InspectArtifactTree(_artifactRunRoot!, directory);
            var reasons = new HashSet<string>(); OwnedRun? run = null;
            try
            {
                string descriptor = Path.Combine(directory, ArtifactRunFile);
                if (File.Exists(descriptor) && new FileInfo(descriptor).Length <= Coordination.MetadataByteLimit)
                    run = JsonSerializer.Deserialize<OwnedRun>(File.ReadAllText(descriptor), RlArtifactSettings.Options);
                if (run is null || run.Policy is null || run.SchemaVersion != RlArtifactSettings.SchemaVersion || run.RunId.ToString("N") != id
                    || run.RegistryRoot != _root || !Enum.IsDefined(run.State) || run.StartedUtc > now
                    || run.FinishedUtc < run.StartedUtc || run.FinishedUtc > now || run.State != RlArtifactRunState.Running && run.FinishedUtc is null)
                    run = null;
                run?.Policy.Validate(complete: true);
            }
            catch (Exception error) when (error is JsonException or ArgumentException) { run = null; }
            if (run is null) reasons.Add(RlArtifactRetentionReason.UnknownOwner.ToString());
            if (!policy.IsComplete) reasons.Add(RlArtifactRetentionReason.SettingsIncomplete.ToString());
            if (!verified) reasons.Add(RlArtifactRetentionReason.UnverifiedPointers.ToString());
            if (HasStoreLease(RunLeaseKind, id)) reasons.Add(RlArtifactRetentionReason.Lease.ToString());
            if (HasResumableCheckpoint(directory, out _)) reasons.Add(RlArtifactRetentionReason.Resumable.ToString());
            RlArtifactKind kind = RlArtifactKind.UnknownRun;
            if (run is not null)
            {
                if (run.State == RlArtifactRunState.Running)
                {
                    // Ended without a terminal state (closed or crashed). A live owner (Lease) or a resumable save (Resumable) is already a reason above; otherwise the run
                    // expires like a failed one, counted from its last change, and is deleted only through the reviewed cleanup.
                    kind = RlArtifactKind.FailedRun;
                    if (now - tree.NewestWriteUtc <= TimeSpan.FromDays(policy.FailedRetentionDays)) reasons.Add(RlArtifactRetentionReason.NotExpired.ToString());
                }
                else if (run.State is RlArtifactRunState.Failed or RlArtifactRunState.Cancelled)
                {
                    kind = RlArtifactKind.FailedRun;
                    if (now - run.FinishedUtc!.Value <= TimeSpan.FromDays(policy.FailedRetentionDays)) reasons.Add(RlArtifactRetentionReason.NotExpired.ToString());
                }
                else
                {
                    kind = RlArtifactKind.CompletedRun;
                    var generationEntry = entries.FirstOrDefault(e => e.Id == run.ModelId && e.Kind == RlArtifactKind.Generation.ToString());
                    if (generationEntry is null || !_rlKnown.TryGetValue(run.ModelId!, out var generation) || !VerifyRlBytes(generation))
                        reasons.Add(RlArtifactRetentionReason.UnregisteredResult.ToString());
                    else if (!generationEntry.Candidate) reasons.UnionWith(generationEntry.Reasons);
                }
            }
            bool candidate = reasons.Count == 0;
            entries.Add(new(id, kind.ToString(), directory, tree.Bytes, run?.StartedUtc ?? DateTimeOffset.MinValue, tree.Identity,
                (candidate ? new[] { (kind == RlArtifactKind.FailedRun ? RlArtifactRetentionReason.ExpiredFailure : RlArtifactRetentionReason.CompletedCopy).ToString() }
                    : reasons.OrderBy(r => r, StringComparer.Ordinal).ToArray()).ToImmutableArray(), candidate));
            if (!candidate && run is not null) AddCheckpointInventory(entries, directory, id, policy, verified);
        }
        RequireStorePath(ArtifactTrashRoot);
        foreach (string directory in Directory.Exists(ArtifactTrashRoot) ? Directory.EnumerateDirectories(ArtifactTrashRoot) : Array.Empty<string>())
        {
            string ticketId = Path.GetFileName(directory); var tree = InspectArtifactTree(ArtifactTrashRoot, directory);
            bool owned = false; DeletionTicket? ticket = null;
            string ticketPath = directory + DeletionTicketSuffix; RequireStorePath(ticketPath);
            try
            {
                if (Guid.TryParseExact(ticketId, "N", out _) && File.Exists(ticketPath) && new FileInfo(ticketPath).Length <= Coordination.MetadataByteLimit)
                    ticket = JsonSerializer.Deserialize<DeletionTicket>(File.ReadAllText(ticketPath), RlArtifactSettings.Options);
                if (ticket is not null && ticket.SchemaVersion == RlArtifactSettings.SchemaVersion)
                {
                    string root = ticket.Kind == RlArtifactKind.Generation.ToString() ? RlRoot : _artifactRunRoot!;
                    owned = (ticket.Kind is "Generation" or "FailedRun" or "CompletedRun" or "Checkpoint")
                        && (ticket.Kind == RlArtifactKind.Generation.ToString() ? IsHash(ticket.Id) : Guid.TryParseExact(ticket.Id, "N", out _))
                        && (ticket.Kind == "Checkpoint" ? ContainsArtifactPath(root, ticket.OriginalPath)
                            && Path.GetFileName(ticket.OriginalPath) == ticket.Id
                            && Path.GetFileName(Path.GetDirectoryName(ticket.OriginalPath)) == RlCheckpointContract.Folder
                            && Guid.TryParseExact(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(ticket.OriginalPath))), "N", out _)
                            : Path.GetFullPath(Path.Combine(root, ticket.Id)) == ticket.OriginalPath);
                }
            }
            catch (Exception error) when (error is JsonException or ArgumentException) { owned = false; }
            bool candidate = owned && policy.IsComplete && verified;
            entries.Add(new(ticketId, RlArtifactKind.PendingDeletion.ToString(), directory, tree.Bytes, ticket?.CreatedUtc ?? DateTimeOffset.MinValue,
                ContentHash.OfText(tree.Identity + FileIdentity(ticketPath)), ImmutableArray.Create((candidate ? RlArtifactRetentionReason.RetryDeletion
                    : !owned ? RlArtifactRetentionReason.UnknownOwner : !policy.IsComplete ? RlArtifactRetentionReason.SettingsIncomplete
                    : RlArtifactRetentionReason.UnverifiedPointers).ToString()), candidate));
        }
        long used = checked(InspectArtifactTree(RlRoot, RlRoot).Bytes + InspectArtifactTree(_artifactRunRoot!, _artifactRunRoot!).Bytes
            + InspectArtifactTree(ArtifactTrashRoot, ArtifactTrashRoot).Bytes);
        long free = ArtifactFreeSpace(_artifactRunRoot!);
        string context = ContentHash.OfText(JsonSerializer.Serialize(new { policy, _root, _artifactRunRoot,
            pointers = new[] { FileIdentity(RlPointerPath(RlFiles.Target)), FileIdentity(RlPointerPath(RlContinuousContract.Target)),
                FileIdentity(RlPointerPath(RlFiles.Target) + Coordination.BackupSuffix), FileIdentity(RlPointerPath(RlContinuousContract.Target) + Coordination.BackupSuffix) } }, RlArtifactSettings.Options));
        return new(policy, context, used, free, entries.OrderBy(e => Enum.Parse<RlArtifactKind>(e.Kind)).ThenBy(e => e.CreatedUtc).ThenBy(e => e.Id, StringComparer.Ordinal).ToImmutableArray());
    }
    public async Task<RlArtifactRunLease?> BeginRlArtifactRunAsync(Guid runId, string dataRoot, CancellationToken ct = default)
    {
        if (_artifactSettings is null) return null;
        await EnterMutationAsync(ct).ConfigureAwait(false);
        IDisposable? lease = null;
        try
        {
            using var transaction = EnterStore(ct);
            lock (_gate)
            {
                if (!Path.GetFullPath(dataRoot).Equals(_artifactDataRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidDataException("Training uses a different artifact data root.");
                var preview = PreviewArtifactsCore()!; preview.Policy.Validate(complete: true);
                if (preview.UsedBytes >= preview.Policy.QuotaBytes || preview.FreeBytes < preview.Policy.MinimumFreeBytes)
                    throw new InvalidOperationException("Unavailable: RL artifact quota or minimum free-space admission failed. Review manual cleanup.");
                string directory = Path.Combine(_artifactRunRoot!, runId.ToString("N")); GuardArtifactPath(_artifactRunRoot!, directory);
                if (Directory.Exists(directory)) throw new InvalidOperationException("Artifact run identity already exists.");
                Directory.CreateDirectory(directory);
                lease = CreateStoreLease(RunLeaseKind, runId.ToString("N"));
                var run = new OwnedRun(RlArtifactSettings.SchemaVersion, runId, _root, ArtifactClock(), null, RlArtifactRunState.Running, null, preview.Policy);
                string descriptor = Path.Combine(directory, ArtifactRunFile);
                AtomicJsonFile.SaveAsync(descriptor, run, RlArtifactSettings.Options).GetAwaiter().GetResult();
                return new(directory, preview.Policy, lease, (state, model, token) => FinishArtifactRunAsync(descriptor, run, state, model, token));
            }
        }
        catch (Exception error) { lease?.Dispose(); RecordStoreFailure("rl-artifact-admission", error); throw; }
        finally { _mutation.Release(); }
    }
    private async Task FinishArtifactRunAsync(string descriptor, OwnedRun run, RlArtifactRunState state, string? modelId, CancellationToken ct)
    {
        if (state == RlArtifactRunState.Running || !Enum.IsDefined(state) || modelId is not null && !IsHash(modelId)) throw new ArgumentException("Invalid artifact terminal state.");
        await EnterMutationAsync(ct).ConfigureAwait(false);
        try
        {
            using var transaction = EnterStore(ct); GuardArtifactPath(_artifactRunRoot!, descriptor);
            GuardArtifactPath(_artifactRunRoot!, descriptor + AtomicJsonFile.TemporarySuffix);
            GuardArtifactPath(_artifactRunRoot!, descriptor + AtomicJsonFile.BackupSuffix);
            await AtomicJsonFile.SaveAsync(descriptor, run with { FinishedUtc = ArtifactClock(), State = state, ModelId = modelId }, RlArtifactSettings.Options).ConfigureAwait(false);
        }
        catch (Exception error) { RecordStoreFailure("rl-artifact-completion", error); throw; }
        finally { _mutation.Release(); }
    }
    /// <summary>Reasons an explicit owner removal does not ask: the retention policy, and the references the removal itself releases from the active/previous/recovery pointers.</summary>
    private static readonly HashSet<string> OwnerRemovalOverriddenReasons = new(StringComparer.Ordinal)
    {
        RlArtifactRetentionReason.Newest.ToString(), RlArtifactRetentionReason.NotExpired.ToString(), RlArtifactRetentionReason.SettingsIncomplete.ToString(),
        RlArtifactRetentionReason.Active.ToString(), RlArtifactRetentionReason.Rollback.ToString(), RlArtifactRetentionReason.Backup.ToString(),
    };

    /// <summary>The pointer without any reference to <paramref name="modelId"/> (an active reference becomes "no active model").</summary>
    private static Pointer WithoutRlGeneration(Pointer pointer, string modelId)
        => new(string.Equals(pointer.ActiveId, modelId, StringComparison.Ordinal) ? null : pointer.ActiveId,
            pointer.History.Where(id => !string.Equals(id, modelId, StringComparison.Ordinal)).ToArray());

    /// <summary>
    /// Releases every pointer reference to a generation that is about to be removed, so no pointer or recovery copy is left naming a missing generation
    /// (which would make the store unverified). The current pointer is written first; the recovery copy is scrubbed before anything is deleted.
    /// </summary>
    private void ReleaseRlPointerReferences(RlGeneration generation)
    {
        bool continuous = generation.Target == RlContinuousContract.Target;
        var current = continuous ? _rlContinuousPointer : _rlPointer;
        string path = RlPointerPath(generation.Target), backup = path + Coordination.BackupSuffix;
        RequirePointerWritePaths(path); RequirePointerWritePaths(backup);
        if (string.Equals(current.ActiveId, generation.ModelId, StringComparison.Ordinal) || current.History.Contains(generation.ModelId, StringComparer.Ordinal))
        {
            var next = WithoutRlGeneration(current, generation.ModelId);
            _savePointer(path, next).GetAwaiter().GetResult();
            if (continuous) _rlContinuousPointer = next; else _rlPointer = next;
        }
        if (!File.Exists(backup)) return;
        var saved = ReadStorePointer(backup, RlContract.RollbackGenerationCount, false);
        if (string.Equals(saved.ActiveId, generation.ModelId, StringComparison.Ordinal) || saved.History.Contains(generation.ModelId, StringComparer.Ordinal))
            AtomicJsonFile.SaveAsync(backup, WithoutRlGeneration(saved, generation.ModelId), JsonOptions, preserveBackup: true).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Captures the current pointer and its recovery copy of a generation's target and returns the action that puts both back, for a removal that fails
    /// before the generation folder is moved (a refusal must change nothing). Call it before <see cref="ReleaseRlPointerReferences"/>.
    /// </summary>
    private Action CaptureRlPointerRestore(RlGeneration generation)
    {
        bool continuous = generation.Target == RlContinuousContract.Target;
        var current = continuous ? _rlContinuousPointer : _rlPointer;
        string path = RlPointerPath(generation.Target), backup = path + Coordination.BackupSuffix;
        var originalBackup = File.Exists(backup) ? ReadStorePointer(backup, RlContract.RollbackGenerationCount, false) : null;
        return () =>
        {
            _savePointer(path, current).GetAwaiter().GetResult();
            if (continuous) _rlContinuousPointer = current; else _rlPointer = current;
            if (originalBackup is not null) AtomicJsonFile.SaveAsync(backup, originalBackup, JsonOptions, preserveBackup: true).GetAwaiter().GetResult();
            else File.Delete(backup);
        };
    }

    /// <summary>
    /// The registered generations whose work folder (<c>&lt;Data&gt;/TrainingArtifacts/RL/&lt;run_id&gt;</c>, the run id recorded in the generation's own job file) no longer exists.
    /// A generation is a self-contained registered copy, so it stays usable; this only lets a list say that the folder it was trained in is gone.
    /// Empty when the RL artifact roots are not configured or a job file cannot be read: the registry never claims a folder is missing without evidence.
    /// </summary>
    public IReadOnlySet<string> RlModelIdsWithoutWorkFolder()
    {
        lock (_gate)
        {
            using var transaction = EnterStore();
            EnsureRlLoaded();
            return _rlKnown.Keys.Where(id => HasRlWorkFolderCore(id) == false).ToHashSet(StringComparer.Ordinal);
        }
    }

    private bool? HasRlWorkFolderCore(string modelId)
    {
        if (_artifactRunRoot is null || !_rlKnown.ContainsKey(modelId)) return null;
        try
        {
            using var job = JsonDocument.Parse(File.ReadAllText(Path.Combine(RlRoot, modelId, RlFiles.Job)));
            if (!job.RootElement.TryGetProperty("run_id", out var run) || run.ValueKind != JsonValueKind.String
                || !Guid.TryParseExact(run.GetString(), "N", out var runId)) return null;
            return Directory.Exists(Path.Combine(_artifactRunRoot, runId.ToString("N")));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>
    /// Removes one registered generation whose work folder is gone. Refused (nothing deleted, the reasons returned) while the generation is a referenced direction
    /// or an in-use model, while its work folder still exists or cannot be determined (those are deleted by the retention cleanup), or while the store is unverified.
    /// The retention period and keep count are deliberately not asked, and neither are the active/previous/recovery references: the owner removes this model
    /// explicitly, so those pointers are released first (removing the active model leaves "no active model", the state of a store before its first activation).
    /// </summary>
    public async Task<RlGenerationRemoval> RemoveRlGenerationAsync(string modelId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        await EnterMutationAsync(ct).ConfigureAwait(false);
        try
        {
            using var transaction = EnterStore(ct);
            lock (_gate)
            {
                EnsureRlLoaded();
                var entry = PreviewArtifactsCore()?.Entries.FirstOrDefault(e => e.Id == modelId && e.Kind == RlArtifactKind.Generation.ToString());
                if (entry is null) return new(false, ImmutableArray.Create(RlArtifactRetentionReason.UnknownOwner.ToString()));
                var blocked = entry.Candidate ? ImmutableArray<string>.Empty : entry.Reasons.Where(reason => !OwnerRemovalOverriddenReasons.Contains(reason)).ToImmutableArray();
                if (blocked.Length == 0 && HasRlWorkFolderCore(modelId) != false) blocked = ImmutableArray.Create(RlArtifactRetentionReason.WorkFolderPresent.ToString());
                if (blocked.Length > 0) return new(false, blocked);
                Action? restorePointers = null;
                try { restorePointers = CaptureRlPointerRestore(_rlKnown[modelId]); ReleaseRlPointerReferences(_rlKnown[modelId]); RetireAndDeleteArtifact(entry); }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
                {
                    RecordStoreFailure("rl-generation-remove", error);
                    // A refusal changes nothing: while the folder is still in place the owner's active/previous pointers come back. Once it has been moved the removal stands.
                    if (restorePointers is not null && Directory.Exists(entry.Path)) restorePointers();
                    throw;
                }
                return new(true, ImmutableArray<string>.Empty);
            }
        }
        finally { _mutation.Release(); }
    }

    /// <summary>
    /// Deletes one reviewed artifact entry: verifies its identity, retires it by same-volume rename behind an external deletion ticket (the ticket survives a
    /// partial recursive delete), then deletes it. The caller holds the mutation and store locks and has already decided that the entry may be deleted.
    /// </summary>
    private void RetireAndDeleteArtifact(RlArtifactEntry entry)
    {
        bool pending = entry.Kind == RlArtifactKind.PendingDeletion.ToString();
        string root = pending ? ArtifactTrashRoot : entry.Kind == RlArtifactKind.Generation.ToString() ? RlRoot : _artifactRunRoot!;
        var tree = InspectArtifactTree(root, entry.Path);
        string identity = pending ? ContentHash.OfText(tree.Identity + FileIdentity(entry.Path + DeletionTicketSuffix)) : tree.Identity;
        if (identity != entry.Identity) throw new InvalidOperationException("Artifact changed before deletion.");
        if (entry.Kind == RlArtifactKind.Generation.ToString() && (!_rlKnown.TryGetValue(entry.Id, out var generation) || !VerifyRlBytes(generation)))
            throw new InvalidDataException("Generation no longer verifies.");
        string retiring = entry.Path;
        if (!pending)
        {
            // Retire by same-volume rename first. An external ticket survives a partial recursive delete.
            RequireStorePath(ArtifactTrashRoot); Directory.CreateDirectory(ArtifactTrashRoot);
            retiring = Path.Combine(ArtifactTrashRoot, Guid.NewGuid().ToString("N"));
            RequireStorePath(retiring); RequirePointerWritePaths(retiring + DeletionTicketSuffix);
            AtomicJsonFile.SaveAsync(retiring + DeletionTicketSuffix,
                new DeletionTicket(RlArtifactSettings.SchemaVersion, entry.Id, entry.Kind, entry.Path, ArtifactClock()), RlArtifactSettings.Options).GetAwaiter().GetResult();
            Directory.Move(entry.Path, retiring);
            if (entry.Kind == RlArtifactKind.Generation.ToString()) _rlKnown.Remove(entry.Id);
        }
        RequireStoreTree(retiring); DeleteDirectory(retiring);
        File.Delete(retiring + DeletionTicketSuffix);
    }

    public async Task<RlArtifactCleanupResult> CleanupRlArtifactsAsync(RlArtifactPreview reviewed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reviewed);
        var selected = reviewed.Entries.Where(e => e.Candidate).ToArray();
        await EnterMutationAsync(ct).ConfigureAwait(false);
        var deleted = ImmutableArray.CreateBuilder<string>(); var failures = ImmutableArray.CreateBuilder<string>();
        try
        {
            using var transaction = EnterStore(ct);
            lock (_gate)
            {
                var current = PreviewArtifactsCore() ?? throw new NotSupportedException(); current.Policy.Validate(complete: true);
                if (current.Context != reviewed.Context || current.Policy != reviewed.Policy) throw new InvalidOperationException("Artifact review context changed; inspect again.");
                foreach (var entry in selected)
                {
                    var now = current.Entries.FirstOrDefault(e => e.Id == entry.Id && e.Kind == entry.Kind);
                    if (now is null || !now.Candidate || now.Identity != entry.Identity || now.Path != entry.Path)
                        throw new InvalidOperationException("Artifact candidate or protection changed; inspect again.");
                }
                foreach (var entry in selected)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        // Re-read protection after each partial operation. Never widen the reviewed set.
                        var now = PreviewArtifactsCore()!.Entries.FirstOrDefault(e => e.Id == entry.Id && e.Kind == entry.Kind);
                        if (now is null || !now.Candidate || now.Identity != entry.Identity) { failures.Add(entry.Id + ": changed or protected"); continue; }
                        RetireAndDeleteArtifact(entry);
                        deleted.Add(entry.Id);
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
                    { failures.Add(entry.Id + ": " + error.Message); RecordStoreFailure("rl-artifact-delete", error); }
                }
                return new(deleted.ToImmutable(), failures.ToImmutable(), PreviewArtifactsCore()!.UsedBytes);
            }
        }
        catch (Exception error) { RecordStoreFailure("rl-artifact-cleanup", error); throw; }
        finally { _mutation.Release(); }
    }
}
