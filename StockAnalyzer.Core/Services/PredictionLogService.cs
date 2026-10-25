using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

public interface IPredictionLogService
{
    event EventHandler? Changed;
    event EventHandler<ModelDecayAlert>? Decayed;
    Task<string?> RecordAsync(PredictionResult result, string symbol, TimeframeType timeframe,
        IReadOnlyList<CandleData> inputs, CancellationToken ct = default);
    Task RefreshAsync(string symbol, TimeframeType timeframe, MonitoringObservation observations,
        CancellationToken ct = default);
    Task MarkUnavailableAsync(string predictionId, string terminalError, CancellationToken ct = default);
    Task<ImmutableArray<ModelMonitoringSnapshot>> GetSnapshotsAsync(string modelId, CancellationToken ct = default);
    Task<PredictionAuditRecord?> ReadAuditAsync(string predictionId, CancellationToken ct = default);
}

/// <summary>Clock-aware model health, separate from the cached-read <see cref="IPredictionLogService.GetSnapshotsAsync"/>.</summary>
public interface ICurrentPredictionHealthService
{
    /// <summary>
    /// Evaluates every model group at one UTC instant captured after the monitor gate is acquired, persists the result
    /// and returns the groups of <paramref name="modelId"/> (all groups when null). A non-SHA256 id throws before any I/O.
    /// </summary>
    Task<ImmutableArray<ModelMonitoringSnapshot>> EvaluateCurrentAsync(string? modelId = null, CancellationToken ct = default);
}

/// <summary>One durable forecast per generation/sample; audit revisions replace outcomes, never add votes.</summary>
public sealed class PredictionLogService : IPredictionLogService, ICurrentPredictionHealthService
{
    private readonly IModelGenerationRegistry _registry;
    private readonly TimeProvider _clock;
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _logger;
    public PredictionLogService(IModelGenerationRegistry registry, TimeProvider? clock = null, string? root = null, ILogger? logger = null)
    {
        _registry = registry; _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _root = root ?? Path.Combine(PathDiscovery.ResolveDataPath(null, "Data"), Models.Settings.PredictionMonitoringSettings.DataFolderName);
    }
    public event EventHandler? Changed;
    public event EventHandler<ModelDecayAlert>? Decayed;
    private string RecordsPath => Path.Combine(_root, "predictions");
    private string StatePath => Path.Combine(_root, "health.json");
    private static bool ValidHash(string? value) => PredictionMonitoringReducer.ValidHash(value);
    private string RecordPath(string id) => ValidHash(id) ? Path.Combine(RecordsPath, id + ".json")
        : throw new ArgumentException("Prediction identity must be a SHA256.", nameof(id));
    public static DateTimeOffset Utc(DateTime timestamp) => new(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc));

    public static string SourceRevision(IReadOnlyList<CandleData> candles)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var c in candles)
        {
            var text = FormattableString.Invariant($"{Utc(c.Timestamp):O}|{c.Open}|{c.High}|{c.Low}|{c.Close}|{c.Volume}\n");
            hash.AppendData(Encoding.UTF8.GetBytes(text));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public async Task<string?> RecordAsync(PredictionResult result, string symbol, TimeframeType timeframe,
        IReadOnlyList<CandleData> inputs, CancellationToken ct = default)
    {
        var prediction = await MonitoredPredictionFactory.CreateAsync(_registry, _clock, result, symbol, timeframe, inputs, ct)
            .ConfigureAwait(false);
        if (prediction is null) return null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(RecordsPath);
            var path = RecordPath(prediction.PredictionId);
            if (!File.Exists(path))
                await AtomicJsonFile.SaveAsync(path, new PredictionAuditRecord(PredictionMonitoringPolicy.SchemaVersion,
                    prediction, ImmutableArray.Create(PredictionMonitoringReducer.InitialOutcome(prediction))),
                    TrainingConfigJson.Options).ConfigureAwait(false);
            else _ = await ReadAsync(path).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        _logger.LogDebug("Recorded forecast {PredictionId} for {Symbol} {Timeframe}.", prediction.PredictionId, symbol, timeframe);
        Changed?.Invoke(this, EventArgs.Empty);
        return prediction.PredictionId;
    }

    public async Task<PredictionAuditRecord?> ReadAuditAsync(string predictionId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await ReadAsync(RecordPath(predictionId)).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private static async Task<PredictionAuditRecord?> ReadAsync(string path)
    {
        if (!File.Exists(path)) return null;
        var record = await AtomicJsonFile.LoadAsync<PredictionAuditRecord>(path, TrainingConfigJson.Options).ConfigureAwait(false)
            ?? throw new InvalidDataException("Prediction audit is empty.");
        PredictionMonitoringReducer.ValidateAudit(record, Path.GetFileNameWithoutExtension(path));
        return record;
    }

    private IEnumerable<string> Records() => Directory.Exists(RecordsPath)
        ? Directory.EnumerateFiles(RecordsPath, "*.json", SearchOption.TopDirectoryOnly) : Array.Empty<string>();

    public static string ActualLabel(decimal anchorClose, decimal futureClose, decimal threshold)
    {
        if (anchorClose <= 0 || futureClose <= 0 || threshold < 0) throw new ArgumentOutOfRangeException(nameof(anchorClose));
        var value = (futureClose - anchorClose) / anchorClose;
        return value > threshold ? "Up" : value < -threshold ? "Down" : "Neutral";
    }

    public async Task RefreshAsync(string symbol, TimeframeType timeframe, MonitoringObservation observations,
        CancellationToken ct = default)
    {
        PredictionMonitoringReducer.ValidateObservation(observations, _clock.GetUtcNow());
        var rowByAnchor = MonitoringAnchorIndex.Create(observations.Candles);
        var alerts = new List<ModelDecayAlert>();
        int revisions = 0;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var path in Records())
            {
                ct.ThrowIfCancellationRequested();
                var record = (await ReadAsync(path).ConfigureAwait(false))!;
                var p = record.Prediction;
                if (!PredictionMonitoringReducer.HasCanonicalAnchor(p)) continue;
                if (p.Symbol != symbol || p.Timeframe != timeframe || record.Outcomes[^1].State == PredictionOutcomeState.Unavailable) continue;
                if (PredictionMonitoringReducer.TryMature(p, record.Outcomes[^1], observations, rowByAnchor) is not { } outcome) continue;
                await AtomicJsonFile.SaveAsync(path, record with { Outcomes = record.Outcomes.Add(outcome) },
                    TrainingConfigJson.Options).ConfigureAwait(false);
                revisions++;
            }
            alerts = await EvaluateAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        _logger.LogDebug("Refreshed {Symbol} {Timeframe}: {Revisions} outcome revisions appended, {Alerts} decay alerts.", symbol, timeframe, revisions, alerts.Count);
        Changed?.Invoke(this, EventArgs.Empty);
        foreach (var alert in alerts) Decayed?.Invoke(this, alert);
    }

    public async Task MarkUnavailableAsync(string predictionId, string terminalError, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(terminalError)) throw new ArgumentException("A terminal source error is required.", nameof(terminalError));
        List<ModelDecayAlert> alerts;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = RecordPath(predictionId);
            var record = await ReadAsync(path).ConfigureAwait(false) ?? throw new InvalidDataException("Prediction is absent.");
            if (record.Outcomes[^1].State == PredictionOutcomeState.Unavailable) return;
            await AtomicJsonFile.SaveAsync(path, record with { Outcomes = record.Outcomes.Add(PredictionMonitoringReducer.Unavailable(
                record.Outcomes[^1], _clock.GetUtcNow(), terminalError)) }, TrainingConfigJson.Options).ConfigureAwait(false);
            _logger.LogWarning("Forecast {PredictionId} is permanently unavailable: {Error}.", predictionId, terminalError);
            alerts = await EvaluateAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
        foreach (var alert in alerts) Decayed?.Invoke(this, alert);
    }

    private async Task<List<ModelDecayAlert>> EvaluateAsync(CancellationToken ct)
        => (await EvaluateAsync(_clock.GetUtcNow(), persistEmptyFirstState: true, ct).ConfigureAwait(false)).Alerts;

    private sealed record HealthEvaluation(List<ModelMonitoringSnapshot> Previous,
        List<ModelMonitoringSnapshot> Snapshots, List<ModelDecayAlert> Alerts);

    private async Task<HealthEvaluation> EvaluateAsync(DateTimeOffset now, bool persistEmptyFirstState, CancellationToken ct)
    {
        var previous = await ReadHealthAsync().ConfigureAwait(false);
        var votes = new PredictionMonitoringReducer.VoteAggregator(now, withVoteDigest: false);
        foreach (var path in Records())
        {
            ct.ThrowIfCancellationRequested();
            var record = (await ReadAsync(path).ConfigureAwait(false))!;
            if (!PredictionMonitoringReducer.HasCanonicalAnchor(record.Prediction)) continue;
            votes.Add(record.Prediction, record.Outcomes[^1]);
        }
        // Groups were validated as unique by ReadHealthAsync, so the exact-key lookup equals the former linear first-match.
        var priorByGroup = previous.ToDictionary(s => (s.ModelId, s.TargetContractHash));
        var snapshots = new List<ModelMonitoringSnapshot>();
        var alerts = new List<ModelDecayAlert>();
        foreach (var (key, value) in votes.Groups())
        {
            var accuracy = PredictionMonitoringPolicy.ComputeAccuracy(value.Count, value.Correct);
            var state = PredictionMonitoringPolicy.ComputeHealthState(value.Count, value.Correct);
            priorByGroup.TryGetValue((key.Model, key.Target), out var prior);
            var snapshot = new ModelMonitoringSnapshot(key.Model, key.Target, value.Count, value.Correct,
                accuracy, state, now, prior?.LastAlertId);
            if (PredictionMonitoringReducer.IsDecayTransition(prior, state))
            {
                var id = Guid.NewGuid().ToString("N");
                snapshot = snapshot with { LastAlertId = id };
                alerts.Add(new(id, snapshot));
            }
            snapshots.Add(snapshot);
        }
        // The periodic evaluation must not create the monitoring root for an installation without any forecast.
        if (!persistEmptyFirstState && snapshots.Count == 0 && !File.Exists(StatePath))
            return new HealthEvaluation(previous, snapshots, alerts);
        Directory.CreateDirectory(_root);
        if (alerts.Count > 0)
        {
            var directory = Path.Combine(_root, "alerts");
            Directory.CreateDirectory(directory);
            foreach (var alert in alerts)
                await AtomicJsonFile.SaveAsync(Path.Combine(directory, alert.AlertId + ".json"), alert,
                    TrainingConfigJson.Options).ConfigureAwait(false);
        }
        await AtomicJsonFile.SaveAsync(StatePath, snapshots, TrainingConfigJson.Options).ConfigureAwait(false);
        return new HealthEvaluation(previous, snapshots, alerts);
    }

    public async Task<ImmutableArray<ModelMonitoringSnapshot>> EvaluateCurrentAsync(string? modelId = null,
        CancellationToken ct = default)
    {
        if (modelId is not null && !ValidHash(modelId))
            throw new ArgumentException("Model identity must be a SHA256.", nameof(modelId));
        HealthEvaluation evaluation;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            // One instant for the whole evaluation, captured after any queueing behind other gate holders.
            var now = _clock.GetUtcNow();
            if (now == default || now.Offset != TimeSpan.Zero)
                throw new InvalidDataException("Evaluation time must be a non-default UTC instant.");
            evaluation = await EvaluateAsync(now, persistEmptyFirstState: false, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        // EvaluatedUtc advances on every evaluation, so it alone must not raise Changed (Changed -> UI read -> evaluation).
        if (PredictionMonitoringReducer.HealthChanged(evaluation.Previous, evaluation.Snapshots)) Changed?.Invoke(this, EventArgs.Empty);
        foreach (var alert in evaluation.Alerts) Decayed?.Invoke(this, alert);
        return evaluation.Snapshots.Where(s => modelId is null || s.ModelId == modelId).ToImmutableArray();
    }

    public async Task<ImmutableArray<ModelMonitoringSnapshot>> GetSnapshotsAsync(string modelId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(StatePath)) return ImmutableArray<ModelMonitoringSnapshot>.Empty;
            var snapshots = await ReadHealthAsync().ConfigureAwait(false);
            return snapshots.Where(s => s.ModelId == modelId).ToImmutableArray();
        }
        finally { _gate.Release(); }
    }

    private async Task<List<ModelMonitoringSnapshot>> ReadHealthAsync()
    {
        if (!File.Exists(StatePath)) return new();
        var snapshots = await AtomicJsonFile.LoadAsync<List<ModelMonitoringSnapshot>>(StatePath, TrainingConfigJson.Options)
            .ConfigureAwait(false) ?? throw new InvalidDataException("Model health state is empty.");
        PredictionMonitoringReducer.ValidateHealth(snapshots);
        return snapshots;
    }
}
