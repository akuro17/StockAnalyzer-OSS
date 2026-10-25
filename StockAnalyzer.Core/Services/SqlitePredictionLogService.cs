using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// The transactional monitoring backend. Same public contract and the same domain rules as <see cref="PredictionLogService"/>
/// (both call <see cref="PredictionMonitoringReducer"/>), but every operation is ONE writer transaction: new outcome
/// revisions, health rows, the transition event and its outbox row commit or roll back together, and Changed/Decayed are
/// published only after a successful commit. Notification delivery is the separate <see cref="Outbox"/>.
/// </summary>
internal sealed partial class SqlitePredictionLogService : IPredictionLogService, ICurrentPredictionHealthService, IAttestedPredictionMonitoring, IAttestedEvidenceDiagnostics
{
    private const string TransitionName = "Healthy->Decayed";
    private const int AlertIdentityVersion = 1;

    private readonly IModelGenerationRegistry _registry;
    private readonly TimeProvider _clock;
    private readonly SqliteMonitoringStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly IReadOnlyList<AttestedSourceEnrollment> _attestedSources;
    private readonly ILogger _logger;

    public SqlitePredictionLogService(IModelGenerationRegistry registry, SqliteMonitoringStore store, TimeSpan outboxPollInterval,
        TimeProvider? clock = null, IReadOnlyList<AttestedSourceEnrollment>? attestedSources = null, ILogger? logger = null)
    {
        _registry = registry; _store = store; _clock = clock ?? TimeProvider.System;
        _attestedSources = attestedSources ?? [];
        _logger = logger ?? NullLogger.Instance;
        Outbox = new SqliteMonitoringOutbox(store, outboxPollInterval);
    }

    public event EventHandler? Changed;
    public event EventHandler<ModelDecayAlert>? Decayed;

    /// <summary>The durable notification delivery of this store.</summary>
    public IMonitoringNotificationOutbox Outbox { get; }

    private static string RequireHash(string id) => PredictionMonitoringReducer.ValidHash(id) ? id
        : throw new ArgumentException("Prediction identity must be a SHA256.", nameof(id));

    /// <summary>New alert identity: SHA256 of a versioned, length-prefixed encoding of (ModelId, TargetHash, HealthRevision, transition).</summary>
    internal static string AlertIdentity(string modelId, string targetHash, long healthRevision)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new System.IO.BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((uint)AlertIdentityVersion);
            foreach (var text in new[] { modelId, targetHash })
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                writer.Write((uint)bytes.Length); writer.Write(bytes);
            }
            writer.Write(healthRevision);
            var transition = Encoding.UTF8.GetBytes(TransitionName);
            writer.Write((uint)transition.Length); writer.Write(transition);
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    public async Task<string?> RecordAsync(PredictionResult result, string symbol, TimeframeType timeframe,
        IReadOnlyList<CandleData> inputs, CancellationToken ct = default)
    {
        var prediction = await MonitoredPredictionFactory.CreateAsync(_registry, _clock, result, symbol, timeframe, inputs, ct).ConfigureAwait(false);
        if (prediction is null) return null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await _store.OpenAsync(requireInitialized: true, ct).ConfigureAwait(false);
            using var transaction = _store.BeginWriter(connection, ct);
            // An identical replay (same identity) is validated and leaves the original forecast untouched: one vote per identity.
            if (await SqliteMonitoringStore.ReadAuditAsync(connection, transaction, prediction.PredictionId, ct).ConfigureAwait(false) is null)
            {
                await SqliteMonitoringStore.InsertPredictionAsync(connection, transaction, prediction, ct).ConfigureAwait(false);
                await SqliteMonitoringStore.InsertOutcomeAsync(connection, transaction, prediction.PredictionId,
                    PredictionMonitoringReducer.InitialOutcome(prediction), ct).ConfigureAwait(false);
            }
            _store.Commit(transaction, ct);
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
        return prediction.PredictionId;
    }

    public async Task<PredictionAuditRecord?> ReadAuditAsync(string predictionId, CancellationToken ct = default)
    {
        var id = RequireHash(predictionId);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await _store.OpenAsync(requireInitialized: true, ct).ConfigureAwait(false);
            return await SqliteMonitoringStore.ReadAuditAsync(connection, null, id, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task RefreshAsync(string symbol, TimeframeType timeframe, MonitoringObservation observations, CancellationToken ct = default)
    {
        PredictionMonitoringReducer.ValidateObservation(observations, _clock.GetUtcNow());
        var rowByAnchor = MonitoringAnchorIndex.Create(observations.Candles);
        HealthEvaluation evaluation;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await _store.OpenAsync(requireInitialized: true, ct).ConfigureAwait(false);
            using var transaction = _store.BeginWriter(connection, ct);
            // Forecasts governed by a producer stream are left to that stream; everything else keeps the legacy semantics.
            bool attested = await SqliteMonitoringStore.AttestationSchemaExistsAsync(connection, transaction, ct).ConfigureAwait(false);
            foreach (var (p, latest) in await SqliteMonitoringStore.ReadLatestForSymbolAsync(connection, transaction, symbol, timeframe, ct, attested).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                if (!PredictionMonitoringReducer.HasCanonicalAnchor(p) || latest.State == PredictionOutcomeState.Unavailable) continue;
                if (PredictionMonitoringReducer.TryMature(p, latest, observations, rowByAnchor) is not { } outcome) continue;
                await SqliteMonitoringStore.InsertOutcomeAsync(connection, transaction, p.PredictionId, outcome, ct).ConfigureAwait(false);
            }
            evaluation = await EvaluateAsync(connection, transaction, _clock.GetUtcNow(), ct).ConfigureAwait(false);
            _store.Commit(transaction, ct);
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
        foreach (var alert in evaluation.Alerts) Decayed?.Invoke(this, alert);
    }

    public async Task MarkUnavailableAsync(string predictionId, string terminalError, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(terminalError)) throw new ArgumentException("A terminal source error is required.", nameof(terminalError));
        var id = RequireHash(predictionId);
        HealthEvaluation evaluation;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await _store.OpenAsync(requireInitialized: true, ct).ConfigureAwait(false);
            using var transaction = _store.BeginWriter(connection, ct);
            var record = await SqliteMonitoringStore.ReadAuditAsync(connection, transaction, id, ct).ConfigureAwait(false)
                ?? throw new System.IO.InvalidDataException("Prediction is absent.");
            if (record.Outcomes[^1].State == PredictionOutcomeState.Unavailable) return;
            await SqliteMonitoringStore.InsertOutcomeAsync(connection, transaction, id,
                PredictionMonitoringReducer.Unavailable(record.Outcomes[^1], _clock.GetUtcNow(), terminalError), ct).ConfigureAwait(false);
            evaluation = await EvaluateAsync(connection, transaction, _clock.GetUtcNow(), ct).ConfigureAwait(false);
            _store.Commit(transaction, ct);
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
        foreach (var alert in evaluation.Alerts) Decayed?.Invoke(this, alert);
    }

    public async Task<ImmutableArray<ModelMonitoringSnapshot>> GetSnapshotsAsync(string modelId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await _store.OpenAsync(requireInitialized: true, ct).ConfigureAwait(false);
            var rows = await SqliteMonitoringStore.ReadHealthAsync(connection, null, ct).ConfigureAwait(false);
            return rows.Where(r => r.Snapshot.ModelId == modelId).Select(r => r.Snapshot).ToImmutableArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<ImmutableArray<ModelMonitoringSnapshot>> EvaluateCurrentAsync(string? modelId = null, CancellationToken ct = default)
    {
        if (modelId is not null && !PredictionMonitoringReducer.ValidHash(modelId))
            throw new ArgumentException("Model identity must be a SHA256.", nameof(modelId));
        HealthEvaluation evaluation;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            await using var connection = await _store.OpenAsync(requireInitialized: true, ct).ConfigureAwait(false);
            using var transaction = _store.BeginWriter(connection, ct);
            // One instant for the whole evaluation, captured after the writer lock is held.
            var now = _clock.GetUtcNow();
            if (now == default || now.Offset != TimeSpan.Zero)
                throw new System.IO.InvalidDataException("Evaluation time must be a non-default UTC instant.");
            evaluation = await EvaluateAsync(connection, transaction, now, ct).ConfigureAwait(false);
            _store.Commit(transaction, ct);
        }
        finally { _gate.Release(); }
        if (PredictionMonitoringReducer.HealthChanged(evaluation.Previous, evaluation.Snapshots)) Changed?.Invoke(this, EventArgs.Empty);
        foreach (var alert in evaluation.Alerts) Decayed?.Invoke(this, alert);
        return evaluation.Snapshots.Where(s => modelId is null || s.ModelId == modelId).ToImmutableArray();
    }

    private sealed record HealthEvaluation(List<ModelMonitoringSnapshot> Previous, List<ModelMonitoringSnapshot> Snapshots, List<ModelDecayAlert> Alerts);

    /// <summary>
    /// Inside the caller's transaction: reads the committed predecessor and every window vote, computes all groups at
    /// <paramref name="now"/> with the shared reducer, and writes health, the transition event and its outbox row.
    /// </summary>
    private static async Task<HealthEvaluation> EvaluateAsync(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset now, CancellationToken ct)
    {
        var previousRows = await SqliteMonitoringStore.ReadHealthAsync(connection, transaction, ct).ConfigureAwait(false);
        var votes = new PredictionMonitoringReducer.VoteAggregator(now, withVoteDigest: true);
        foreach (var (model, target) in await SqliteMonitoringStore.ReadGroupsAsync(connection, transaction, ct).ConfigureAwait(false))
            votes.Register(model, target);
        foreach (var (p, latest) in await SqliteMonitoringStore.ReadVotesAsync(connection, transaction, now, ct).ConfigureAwait(false))
        {
            if (!PredictionMonitoringReducer.HasCanonicalAnchor(p)) continue;
            votes.Add(p, latest);
        }
        var priorByGroup = previousRows.ToDictionary(r => (r.Snapshot.ModelId, r.Snapshot.TargetContractHash));
        var snapshots = new List<ModelMonitoringSnapshot>(); var alerts = new List<ModelDecayAlert>();
        foreach (var (key, value) in votes.Groups())
        {
            ct.ThrowIfCancellationRequested();
            var accuracy = PredictionMonitoringPolicy.ComputeAccuracy(value.Count, value.Correct);
            var state = PredictionMonitoringPolicy.ComputeHealthState(value.Count, value.Correct);
            priorByGroup.TryGetValue((key.Model, key.Target), out var prior);
            var snapshot = new ModelMonitoringSnapshot(key.Model, key.Target, value.Count, value.Correct, accuracy, state, now, prior?.Snapshot.LastAlertId);
            // The revision moves only when the semantic health or the represented latest votes move, never for the instant alone.
            long revision = prior is null ? 1
                : prior.Snapshot.MaturedCount == snapshot.MaturedCount && prior.Snapshot.CorrectCount == snapshot.CorrectCount
                    && prior.Snapshot.Accuracy == snapshot.Accuracy && prior.Snapshot.State == state && prior.VoteDigest == value.VoteDigest
                    ? prior.Revision : checked(prior.Revision + 1);
            ModelDecayAlert? alert = null;
            if (PredictionMonitoringReducer.IsDecayTransition(prior?.Snapshot, state))
            {
                var id = AlertIdentity(key.Model, key.Target, revision);
                snapshot = snapshot with { LastAlertId = id };
                alert = new ModelDecayAlert(id, snapshot);
            }
            await SqliteMonitoringStore.UpsertHealthAsync(connection, transaction, new HealthRow(snapshot, revision, value.VoteDigest), ct).ConfigureAwait(false);
            if (alert is not null)
            {
                await SqliteMonitoringStore.InsertAlertAsync(connection, transaction, alert, revision, presented: false, ct).ConfigureAwait(false);
                alerts.Add(alert);
            }
            snapshots.Add(snapshot);
        }
        return new HealthEvaluation(previousRows.Select(r => r.Snapshot).ToList(), snapshots, alerts);
    }
}
