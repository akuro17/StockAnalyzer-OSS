using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

public interface IRetrainingScheduler
{
    event EventHandler? Changed;
    RetrainingSchedule? Current { get; }
    Task InitializeAsync();
    Task SaveConfigurationAsync(TrainingJobConfig config);
    Task SetEnabledAsync(bool enabled);
    Task ProcessDueAsync(CancellationToken ct = default);
    Task RunAsync(CancellationToken ct);
    /// <summary>
    /// Runner for the application lifetime: a failed state load or save is reported and retried after one poll interval
    /// instead of ending the runner. A retry only loads state; it never re-executes a consumed occurrence.
    /// </summary>
    Task RunWithRecoveryAsync(CancellationToken ct);
    void CancelCurrent();
}

/// <summary>Another scheduler owner (possibly another process) holds the short root mutation lease; nothing was changed.</summary>
public sealed class RetrainingScheduleBusyException() : InvalidOperationException("The retraining schedule is being updated by another scheduler owner.");

/// <summary>
/// Durable weekly UTC claims, one candidate run, no retries and no model activation.
/// Disk mutation is guarded by a root file lease and every occurrence by an owner file lease; all processes writing one
/// scheduler root must run this protocol (mixed-version safety with older, lease-unaware writers is not provided).
/// </summary>
public sealed class RetrainingScheduler : IRetrainingScheduler
{
    private const string ClaimExtension = ".claim", JournalExtension = ".json", OwnerExtension = ".owner";
    private const string OccurrenceDirectoryName = "occurrences", RootLeaseFileName = "schedule.lock";

    private readonly ITrainingOrchestrator _orchestrator;
    private readonly IModelGenerationRegistry _registry;
    private readonly IExperimentLogService _experiments;
    private readonly TimeProvider _clock;
    private readonly ILogger<RetrainingScheduler> _logger;
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _cancellationGate = new();
    private volatile RetrainingSchedule? _current;
    private bool _initialized;
    private int _running;
    private CancellationTokenSource? _runCancellation;

    public RetrainingScheduler(ITrainingOrchestrator orchestrator, IModelGenerationRegistry registry,
        IExperimentLogService experiments, ILogger<RetrainingScheduler>? logger = null,
        TimeProvider? clock = null, string? root = null)
    {
        _orchestrator = orchestrator; _registry = registry; _experiments = experiments;
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger<RetrainingScheduler>.Instance;
        _root = root ?? Path.Combine(PathDiscovery.ResolveDataPath(null, "Data"), "TrainingArtifacts", "retraining");
    }

    public event EventHandler? Changed;
    public RetrainingSchedule? Current => _current is { } state ? state with { Config = TrainingJobGate.Freeze(state.Config) } : null;
    private string SettingsPath => Path.Combine(_root, "schedule.json");
    private string RootLeasePath => Path.Combine(_root, RootLeaseFileName);

    public static DateTimeOffset LatestOccurrence(DateTimeOffset now)
    {
        var date = now.UtcDateTime.Date;
        return new DateTimeOffset(date.AddDays(-(int)date.DayOfWeek), TimeSpan.Zero);
    }

    // ---- occurrence naming and leases ------------------------------------------------------------------------

    private string OccurrenceDirectory(string jobId) => Path.Combine(_root, OccurrenceDirectoryName, jobId);

    /// <summary>UTC Gregorian yyyyMMdd, independent of the current culture's calendar.</summary>
    private static string DueStamp(DateTimeOffset due) => due.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private string OccurrencePath(string jobId, DateTimeOffset due, string extension) =>
        Path.Combine(OccurrenceDirectory(jobId), DueStamp(due) + extension);

    private IDisposable? TryAcquireRootLease()
    {
        Directory.CreateDirectory(_root);
        return SchedulerFileLease.TryAcquire(RootLeasePath);
    }

    private async Task<IDisposable> AcquireRootLeaseWithRetryAsync()
    {
        while (true)
        {
            if (TryAcquireRootLease() is { } lease) return lease;
            // The root lease is only ever held for short file I/O and its holders never wait for an owner lease.
            await Task.Delay(PredictionMonitoringPolicy.LeaseRetryInterval, _clock, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private IDisposable? TryAcquireOwnerLease(string jobId, DateTimeOffset due)
    {
        Directory.CreateDirectory(OccurrenceDirectory(jobId));
        return SchedulerFileLease.TryAcquire(OccurrencePath(jobId, due, OwnerExtension));
    }

    private bool IsOwnerLeaseHeld(string jobId, DateTimeOffset due) =>
        SchedulerFileLease.IsHeld(OccurrencePath(jobId, due, OwnerExtension));

    // ---- schedule state --------------------------------------------------------------------------------------

    private async Task<RetrainingSchedule?> LoadScheduleAsync()
    {
        if (!File.Exists(SettingsPath)) return null;
        var state = await AtomicJsonFile.LoadAsync<RetrainingSchedule>(SettingsPath, TrainingConfigJson.Options)
            .ConfigureAwait(false) ?? throw new InvalidDataException("Retraining schedule is empty.");
        state.Config.Validate();
        if (state.SchemaVersion != PredictionMonitoringPolicy.SchemaVersion
            || state.JobId != TrainingJobGate.Identity(state.Config) || !Enum.IsDefined(state.Status)
            || state.LastOccurrenceUtc.Offset != TimeSpan.Zero || state.LastOccurrenceUtc == default
            || LatestOccurrence(state.LastOccurrenceUtc) != state.LastOccurrenceUtc)
            throw new InvalidDataException("Retraining schedule identity or occurrence is invalid.");
        return state;
    }

    private async Task PersistAsync(RetrainingSchedule state)
    {
        Directory.CreateDirectory(_root);
        await AtomicJsonFile.SaveAsync(SettingsPath, state, TrainingConfigJson.Options).ConfigureAwait(false);
        _current = state;
    }

    private static bool SamePublicState(RetrainingSchedule? a, RetrainingSchedule? b) => a is null ? b is null
        : b is not null && a.JobId == b.JobId && a.Enabled == b.Enabled && a.LastOccurrenceUtc == b.LastOccurrenceUtc
            && a.Status == b.Status && a.CandidateModelId == b.CandidateModelId && a.Error == b.Error;

    public async Task InitializeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            if (File.Exists(SettingsPath))
            {
                using var root = TryAcquireRootLease() ?? throw new RetrainingScheduleBusyException();
                if (await LoadScheduleAsync().ConfigureAwait(false) is { } state)
                {
                    // A saved Running becomes Interrupted only when no live owner lease proves the occurrence is still running.
                    var reconciled = await ReconcileAsync(state).ConfigureAwait(false);
                    if (reconciled != state) await PersistAsync(reconciled).ConfigureAwait(false);
                    _current = reconciled;
                }
            }
            _initialized = true;
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveConfigurationAsync(TrainingJobConfig config)
    {
        var frozen = TrainingJobGate.Freeze(config);
        frozen.Validate();
        await InitializeAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _running) != 0) throw new TrainingJobBusyException();
            using var root = TryAcquireRootLease() ?? throw new RetrainingScheduleBusyException();
            // An occurrence owned by any process keeps its saved configuration.
            if (await LoadScheduleAsync().ConfigureAwait(false) is { } disk
                && (await ReconcileAsync(disk).ConfigureAwait(false)).Status == RetrainingStatus.Running)
                throw new TrainingJobBusyException();
            await PersistAsync(new(PredictionMonitoringPolicy.SchemaVersion, TrainingJobGate.Identity(frozen),
                frozen, false, LatestOccurrence(_clock.GetUtcNow()), RetrainingStatus.Disabled)).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        await InitializeAsync().ConfigureAwait(false);
        bool notify = true;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var root = TryAcquireRootLease() ?? throw new RetrainingScheduleBusyException();
            // The disk copy is authoritative; only the enable state and its baseline change, never the configuration.
            var disk = await LoadScheduleAsync().ConfigureAwait(false)
                ?? throw new InvalidOperationException("Save a training configuration first.");
            var state = await ReconcileAsync(disk).ConfigureAwait(false);
            if (state.Enabled == enabled)
            {
                var before = _current;
                if (state != disk) await PersistAsync(state).ConfigureAwait(false); else _current = state;
                notify = !SamePublicState(before, state);
            }
            else
            {
                bool active = Volatile.Read(ref _running) != 0 || state.Status == RetrainingStatus.Running;
                await PersistAsync(state with { Enabled = enabled,
                    LastOccurrenceUtc = enabled ? LatestOccurrence(_clock.GetUtcNow()) : state.LastOccurrenceUtc,
                    Status = active ? state.Status : enabled ? RetrainingStatus.Waiting : RetrainingStatus.Disabled,
                    Error = null }).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
        if (notify) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void CancelCurrent()
    {
        lock (_cancellationGate) _runCancellation?.Cancel();
    }

    // ---- consumed-occurrence evidence ------------------------------------------------------------------------

    private sealed record OccurrenceScan(IReadOnlyList<RetrainingOccurrence> Claims, IReadOnlyList<RetrainingOccurrence> Journals);

    /// <summary>
    /// Reads every claim/journal of a job by payload, so an occurrence is recognized whatever basename an older culture
    /// gave it. A malformed entry fails explicitly and is never deleted or renamed.
    /// </summary>
    private async Task<OccurrenceScan> ScanOccurrencesAsync(string jobId)
    {
        var claims = new List<RetrainingOccurrence>(); var journals = new List<RetrainingOccurrence>();
        var directory = OccurrenceDirectory(jobId);
        if (!Directory.Exists(directory)) return new(claims, journals);
        foreach (var (extension, target) in new[] { (ClaimExtension, claims), (JournalExtension, journals) })
            foreach (var path in Directory.EnumerateFiles(directory, "*" + extension).OrderBy(p => p, StringComparer.Ordinal))
            {
                RetrainingOccurrence? entry;
                try { entry = await AtomicJsonFile.LoadAsync<RetrainingOccurrence>(path, TrainingConfigJson.Options).ConfigureAwait(false); }
                catch (JsonException ex) { throw new InvalidDataException($"Retraining occurrence '{Path.GetFileName(path)}' is malformed.", ex); }
                bool terminal = entry is not null && entry.Status is RetrainingStatus.Succeeded or RetrainingStatus.Failed
                    or RetrainingStatus.Cancelled or RetrainingStatus.Skipped;
                if (entry is null || entry.JobId != jobId || entry.DueUtc == default || entry.DueUtc.Offset != TimeSpan.Zero
                    || LatestOccurrence(entry.DueUtc) != entry.DueUtc
                    || (extension == ClaimExtension ? entry.Status != RetrainingStatus.Running : !terminal))
                    throw new InvalidDataException($"Retraining occurrence '{Path.GetFileName(path)}' is invalid.");
                target.Add(entry);
            }
        return new(claims, journals);
    }

    private static RetrainingOccurrence? JournalFor(OccurrenceScan scan, DateTimeOffset due)
    {
        var matches = scan.Journals.Where(j => j.DueUtc == due).ToList();
        if (matches.Any(m => m != matches[0]))
            throw new InvalidDataException("Conflicting terminal journals exist for one retraining occurrence.");
        return matches.FirstOrDefault();
    }

    private static bool IsConsumed(OccurrenceScan scan, DateTimeOffset due) =>
        scan.Claims.Any(c => c.DueUtc == due) || scan.Journals.Any(j => j.DueUtc == due);

    /// <summary>Merges only the occurrence's due/status/candidate/error into the authoritative schedule.</summary>
    private static RetrainingSchedule MergeConsumed(RetrainingSchedule state, DateTimeOffset due, OccurrenceScan scan, bool ownerHeld)
    {
        if (JournalFor(scan, due) is { } journal)
            return state with { LastOccurrenceUtc = due, Status = journal.Status, CandidateModelId = journal.CandidateModelId, Error = journal.Error };
        return state with { LastOccurrenceUtc = due, Status = ownerHeld ? RetrainingStatus.Running : RetrainingStatus.Interrupted,
            CandidateModelId = null, Error = null };
    }

    /// <summary>
    /// For a Running/Interrupted schedule, re-derives the consumed occurrence's status from its terminal journal or, when
    /// none exists, from the live owner lease. Never starts or re-runs anything. Caller holds the root lease.
    /// </summary>
    private async Task<RetrainingSchedule> ReconcileAsync(RetrainingSchedule state)
    {
        if (state.Status is not (RetrainingStatus.Running or RetrainingStatus.Interrupted)) return state;
        var scan = await ScanOccurrencesAsync(state.JobId).ConfigureAwait(false);
        return MergeConsumed(state, state.LastOccurrenceUtc, scan, IsOwnerLeaseHeld(state.JobId, state.LastOccurrenceUtc));
    }

    // ---- one tick ----------------------------------------------------------------------------------------------

    private sealed class ClaimedRun
    {
        public IDisposable? Owner;
        public RetrainingSchedule? State;
    }

    public async Task ProcessDueAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_cancellationGate) _runCancellation = cancellation;
        var claim = new ClaimedRun();
        try
        {
            await InitializeAsync().ConfigureAwait(false);
            bool changed;
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try { changed = await TickLockedAsync(claim, cancellation.Token).ConfigureAwait(false); }
            finally { _gate.Release(); }
            if (changed) Changed?.Invoke(this, EventArgs.Empty);
            if (claim.State is not { } state) return;

            // Only the occurrence owner lease spans the training; no gate or root lease is held from here on.
            var due = state.LastOccurrenceUtc;
            var occurrence = new RetrainingOccurrence(state.JobId, due, RetrainingStatus.Running, _clock.GetUtcNow());
            try
            {
                var result = await _orchestrator.StartTrainingAsync(state.Config, ct: cancellation.Token).ConfigureAwait(false);
                cancellation.Token.ThrowIfCancellationRequested();
                await _experiments.RecordAsync(state.Config with { RunId = result.RunId }, result, cancellation.Token).ConfigureAwait(false);
                if (!result.Success || string.IsNullOrWhiteSpace(result.OnnxArtifactPath))
                    throw new InvalidOperationException(result.Message ?? "Scheduled training failed.");
                // Registration verifies T05 evidence; activation remains a separate existing T05 action.
                var candidate = await _registry.RegisterAsync(result.OnnxArtifactPath, result.MetricsArtifactPath,
                    cancellation.Token).ConfigureAwait(false);
                occurrence = occurrence with { Status = RetrainingStatus.Succeeded, RunId = result.RunId,
                    CandidateModelId = candidate.ModelId };
            }
            catch (TrainingJobBusyException) { occurrence = occurrence with { Status = RetrainingStatus.Skipped }; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            { occurrence = occurrence with { Status = RetrainingStatus.Cancelled }; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled training failed for {JobId} at {DueUtc}.", state.JobId, due);
                occurrence = occurrence with { Status = RetrainingStatus.Failed, Error = ex.Message };
            }
            occurrence = occurrence with { UpdatedUtc = _clock.GetUtcNow() };
            await CompleteOccurrenceAsync(occurrence).ConfigureAwait(false);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            // Released only after terminal persistence was attempted: a failed save leaves the due consumed, never re-run.
            claim.Owner?.Dispose();
            lock (_cancellationGate) _runCancellation = null;
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>Runs under <c>_gate</c>. Returns whether the published schedule state changed.</summary>
    private async Task<bool> TickLockedAsync(ClaimedRun claim, CancellationToken token)
    {
        if (!File.Exists(SettingsPath)) return false;
        // A busy root lease means another owner is mutating the schedule: consume nothing and look again next tick.
        using var root = TryAcquireRootLease();
        if (root is null) return false;
        if (await LoadScheduleAsync().ConfigureAwait(false) is not { } disk) return false;
        var before = _current;
        var state = await ReconcileAsync(disk).ConfigureAwait(false);   // later terminal results of a consumed due, no re-execution

        var due = LatestOccurrence(_clock.GetUtcNow());
        if (state.Enabled && due > state.LastOccurrenceUtc)
        {
            token.ThrowIfCancellationRequested();
            if (TryAcquireOwnerLease(state.JobId, due) is not { } owner)
            {
                // Another owner holds this due: follow it, never start it.
                var scan = await ScanOccurrencesAsync(state.JobId).ConfigureAwait(false);
                if (IsConsumed(scan, due)) state = MergeConsumed(state, due, scan, ownerHeld: true);
            }
            else
            {
                claim.Owner = owner;
                var scan = await ScanOccurrencesAsync(state.JobId).ConfigureAwait(false);
                if (IsConsumed(scan, due))
                {
                    // Consumed by an owner that has since exited (we hold its lease): reconcile, never re-run.
                    state = MergeConsumed(state, due, scan, ownerHeld: false);
                    owner.Dispose(); claim.Owner = null;
                }
                else
                {
                    // Claim the latest missed occurrence once; earlier missed weeks are skipped.
                    await CreateClaimAsync(new RetrainingOccurrence(state.JobId, due, RetrainingStatus.Running, _clock.GetUtcNow()))
                        .ConfigureAwait(false);
                    state = state with { LastOccurrenceUtc = due, Status = RetrainingStatus.Running, Error = null, CandidateModelId = null };
                    claim.State = state;
                }
            }
        }
        // The schedule is persisted before it is published.
        if (state != disk) await PersistAsync(state).ConfigureAwait(false);
        _current = state;
        return !SamePublicState(before, state);
    }

    /// <summary>Atomically creates the immutable claim. A claim this call created but could not finish writing is removed again.</summary>
    private async Task CreateClaimAsync(RetrainingOccurrence occurrence)
    {
        Directory.CreateDirectory(OccurrenceDirectory(occurrence.JobId));
        var path = OccurrencePath(occurrence.JobId, occurrence.DueUtc, ClaimExtension);
        bool created = false;
        try
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            await JsonSerializer.SerializeAsync(stream, occurrence, TrainingConfigJson.Options).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            if (created) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            throw;
        }
    }

    /// <summary>Persists the terminal journal, then merges it into the authoritative schedule, under the root lease.</summary>
    private async Task CompleteOccurrenceAsync(RetrainingOccurrence occurrence)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var root = await AcquireRootLeaseWithRetryAsync().ConfigureAwait(false);
            Directory.CreateDirectory(OccurrenceDirectory(occurrence.JobId));
            await AtomicJsonFile.SaveAsync(OccurrencePath(occurrence.JobId, occurrence.DueUtc, JournalExtension),
                occurrence, TrainingConfigJson.Options).ConfigureAwait(false);
            if (await LoadScheduleAsync().ConfigureAwait(false) is not { } disk) return;
            if (disk.JobId == occurrence.JobId && disk.LastOccurrenceUtc == occurrence.DueUtc)
                await PersistAsync(disk with { Status = occurrence.Status,
                    CandidateModelId = occurrence.CandidateModelId, Error = occurrence.Error }).ConfigureAwait(false);
            else _current = disk;
        }
        finally { _gate.Release(); }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await InitializeAsync().ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            try { await ProcessDueAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Retraining scheduler persistence failed."); }
            try { await Task.Delay(PredictionMonitoringPolicy.SchedulerPollInterval, _clock, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    public async Task RunWithRecoveryAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // ProcessDueAsync initializes first, so a failed startup load is reported and retried at the poll cadence.
            try { await ProcessDueAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RetrainingScheduleBusyException)
            { _logger.LogWarning(ex, "Retraining scheduler state could not be accessed; retrying after the poll interval."); }
            catch (Exception ex)
            { _logger.LogError(ex, "Retraining scheduler state is invalid or could not be persisted; it is re-read after the poll interval."); }
            try { await Task.Delay(PredictionMonitoringPolicy.SchedulerPollInterval, _clock, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }
}
