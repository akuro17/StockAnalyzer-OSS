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
    public PredictionLogService(IModelGenerationRegistry registry, TimeProvider? clock = null, string? root = null)
    {
        _registry = registry; _clock = clock ?? TimeProvider.System;
        _root = root ?? Path.Combine(PathDiscovery.ResolveDataPath(null, "Data"), "PredictionMonitoring");
    }
    public event EventHandler? Changed;
    public event EventHandler<ModelDecayAlert>? Decayed;
    private string RecordsPath => Path.Combine(_root, "predictions");
    private string StatePath => Path.Combine(_root, "health.json");
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static bool ValidHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
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
        if (result.IsFallback || result.IsRegression || result.ModelId is not { } modelId || inputs.Count == 0
            || result.OutputContract is not { Semantic: PredictionOutputSemantic.ClassProbabilities } output
            || output.Timeframe != timeframe) return null;
        var captured = inputs.ToArray();
        ValidateChronology(captured);
        if (Utc(captured[^1].Timestamp) > _clock.GetUtcNow())
            throw new InvalidDataException("Prediction anchor is in the future.");
        if (string.IsNullOrWhiteSpace(symbol)) throw new ArgumentException("Symbol is required.", nameof(symbol));
        MonitoredPrediction prediction = await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var lease = _registry.Acquire(modelId) ?? throw new InvalidDataException("Prediction generation is unavailable.");
            var manifest = _registry.GetManifest(modelId) ?? throw new InvalidDataException("Prediction manifest is unavailable.");
            using var session = new InferenceSession(lease.ModelPath);
            var metadata = session.ModelMetadata.CustomMetadataMap;
            if (PredictionModelMetadata.ReadOutputContract(metadata) != output
                || !decimal.TryParse(metadata.GetValueOrDefault("neutral_threshold"), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var threshold) || threshold < 0
                || result.Label is not ("Up" or "Down" or "Neutral"))
                throw new InvalidDataException("Prediction monitoring target contract is invalid.");
            var feature = FixedScaler.ComputeContractHash(metadata);
            var target = Hash(FormattableString.Invariant($"{output.Semantic}|{output.TargetFormula}|{output.Timeframe}|{output.HorizonBars}|{threshold}"));
            var anchor = Utc(captured[^1].Timestamp);
            // A corrected source at the same anchor does not invent another forecast vote.
            var id = Hash(FormattableString.Invariant($"{modelId}|{symbol}|{timeframe}|{anchor:O}|{feature}|{target}"));
            return new MonitoredPrediction(id, modelId, manifest.ModelHash, symbol, timeframe, anchor,
                output.HorizonBars, threshold, result.Label, target, feature, SourceRevision(captured), _clock.GetUtcNow());
        }, ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(RecordsPath);
            var path = RecordPath(prediction.PredictionId);
            if (!File.Exists(path))
                await AtomicJsonFile.SaveAsync(path, new PredictionAuditRecord(PredictionMonitoringPolicy.SchemaVersion,
                    prediction, ImmutableArray.Create(new PredictionOutcomeRevision(0, prediction.InputRevision,
                        PredictionOutcomeState.Pending, prediction.RecordedUtc))), TrainingConfigJson.Options).ConfigureAwait(false);
            else _ = await ReadAsync(path).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
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
        if (record.SchemaVersion != PredictionMonitoringPolicy.SchemaVersion || record.Prediction is null
            || !ValidHash(record.Prediction.PredictionId) || !ValidHash(record.Prediction.InputRevision)
            || !ValidHash(record.Prediction.TargetContractHash) || !ValidHash(record.Prediction.FeatureContractHash)
            || !ValidHash(record.Prediction.ModelHash) || record.Prediction.HorizonBars <= 0
            || !Enum.IsDefined(record.Prediction.Timeframe) || record.Prediction.NeutralThreshold < 0
            || record.Prediction.PredictedLabel is not ("Up" or "Down" or "Neutral")
            || record.Prediction.AnchorUtc.Offset != TimeSpan.Zero || record.Prediction.RecordedUtc.Offset != TimeSpan.Zero
            || Path.GetFileNameWithoutExtension(path) != record.Prediction.PredictionId || record.Outcomes.IsDefaultOrEmpty)
            throw new InvalidDataException("Prediction audit contract is invalid.");
        for (int i = 0; i < record.Outcomes.Length; i++)
        {
            var o = record.Outcomes[i];
            if (o is null || o.Revision != i || !ValidHash(o.SourceRevision) || !Enum.IsDefined(o.State)
                || o.ObservedUtc.Offset != TimeSpan.Zero || o.ObservedUtc == default
                || o.State == PredictionOutcomeState.Matured && (o.ActualLabel is not ("Up" or "Down" or "Neutral")
                    || o.LabelEndUtc is not { } end || end <= record.Prediction.AnchorUtc || end.Offset != TimeSpan.Zero)
                || o.State == PredictionOutcomeState.Unavailable && string.IsNullOrWhiteSpace(o.Error))
                throw new InvalidDataException("Prediction outcome revision is invalid.");
        }
        return record;
    }

    private IEnumerable<string> Records() => Directory.Exists(RecordsPath)
        ? Directory.EnumerateFiles(RecordsPath, "*.json", SearchOption.TopDirectoryOnly) : Array.Empty<string>();

    private static void ValidateChronology(IReadOnlyList<CandleData> candles)
    {
        for (int i = 0; i < candles.Count; i++)
            if (candles[i].Close <= 0 || (i > 0 && candles[i].Timestamp <= candles[i - 1].Timestamp)
                || candles[i].Timestamp.Kind == DateTimeKind.Local)
                throw new InvalidDataException("Monitoring candles must have positive closes and chronological UTC timestamps.");
    }

    public static string ActualLabel(decimal anchorClose, decimal futureClose, decimal threshold)
    {
        if (anchorClose <= 0 || futureClose <= 0 || threshold < 0) throw new ArgumentOutOfRangeException(nameof(anchorClose));
        var value = (futureClose - anchorClose) / anchorClose;
        return value > threshold ? "Up" : value < -threshold ? "Down" : "Neutral";
    }

    public async Task RefreshAsync(string symbol, TimeframeType timeframe, MonitoringObservation observations,
        CancellationToken ct = default)
    {
        if (!ValidHash(observations.SourceRevision) || observations.ObservedUtc.Offset != TimeSpan.Zero
            || observations.ObservedUtc > _clock.GetUtcNow() || observations.Candles.IsDefault || observations.FinalBars.IsDefault
            || observations.Candles.Length != observations.FinalBars.Length)
            throw new InvalidDataException("Monitoring observation source is invalid.");
        ValidateChronology(observations.Candles);
        var alerts = new List<ModelDecayAlert>();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var path in Records())
            {
                ct.ThrowIfCancellationRequested();
                var record = (await ReadAsync(path).ConfigureAwait(false))!;
                var p = record.Prediction;
                if (p.Symbol != symbol || p.Timeframe != timeframe || record.Outcomes[^1].State == PredictionOutcomeState.Unavailable) continue;
                int anchor = -1;
                for (int i = 0; i < observations.Candles.Length; i++)
                    if (Utc(observations.Candles[i].Timestamp) == p.AnchorUtc) { anchor = i; break; }
                if (anchor < 0 || p.HorizonBars >= observations.Candles.Length - anchor) continue;
                int end = anchor + p.HorizonBars;
                bool final = true;
                for (int i = anchor; i <= end; i++) final &= observations.FinalBars[i];
                if (!final || Utc(observations.Candles[end].Timestamp) > observations.ObservedUtc) continue;
                var targetBars = observations.Candles.AsSpan(anchor, p.HorizonBars + 1).ToArray();
                var source = SourceRevision(targetBars);
                var label = ActualLabel(targetBars[0].Close, targetBars[^1].Close, p.NeutralThreshold);
                var previous = record.Outcomes[^1];
                if (observations.ObservedUtc < previous.ObservedUtc) continue;
                if (previous.State == PredictionOutcomeState.Matured && previous.SourceRevision == source) continue;
                var outcome = new PredictionOutcomeRevision(record.Outcomes.Length, source, PredictionOutcomeState.Matured,
                    observations.ObservedUtc, Utc(targetBars[^1].Timestamp), label, DatasetRevision: observations.SourceRevision);
                await AtomicJsonFile.SaveAsync(path, record with { Outcomes = record.Outcomes.Add(outcome) },
                    TrainingConfigJson.Options).ConfigureAwait(false);
            }
            alerts = await EvaluateAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
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
            await AtomicJsonFile.SaveAsync(path, record with { Outcomes = record.Outcomes.Add(new(
                record.Outcomes.Length, record.Outcomes[^1].SourceRevision, PredictionOutcomeState.Unavailable,
                _clock.GetUtcNow(), Error: terminalError)) }, TrainingConfigJson.Options).ConfigureAwait(false);
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
        var groups = new Dictionary<(string Model, string Target), (int Count, int Correct)>();
        foreach (var path in Records())
        {
            ct.ThrowIfCancellationRequested();
            var record = (await ReadAsync(path).ConfigureAwait(false))!;
            var p = record.Prediction; var o = record.Outcomes[^1]; var key = (p.ModelId, p.TargetContractHash);
            groups.TryAdd(key, (0, 0));
            if (o.State != PredictionOutcomeState.Matured || o.LabelEndUtc <= now.AddDays(-PredictionMonitoringPolicy.LookbackCalendarDays)
                || o.LabelEndUtc > now) continue;
            var count = groups[key];
            groups[key] = (checked(count.Count + 1), checked(count.Correct + (p.PredictedLabel == o.ActualLabel ? 1 : 0)));
        }
        var snapshots = new List<ModelMonitoringSnapshot>();
        var alerts = new List<ModelDecayAlert>();
        foreach (var (key, value) in groups)
        {
            var accuracy = PredictionMonitoringPolicy.ComputeAccuracy(value.Count, value.Correct);
            var state = PredictionMonitoringPolicy.ComputeHealthState(value.Count, value.Correct);
            var prior = previous.FirstOrDefault(p => p.ModelId == key.Model && p.TargetContractHash == key.Target);
            var snapshot = new ModelMonitoringSnapshot(key.Model, key.Target, value.Count, value.Correct,
                accuracy, state, now, prior?.LastAlertId);
            if (prior?.State == ModelHealthState.Healthy && state == ModelHealthState.Decayed)
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
        if (HealthChanged(evaluation.Previous, evaluation.Snapshots)) Changed?.Invoke(this, EventArgs.Empty);
        foreach (var alert in evaluation.Alerts) Decayed?.Invoke(this, alert);
        return evaluation.Snapshots.Where(s => modelId is null || s.ModelId == modelId).ToImmutableArray();
    }

    private static bool HealthChanged(List<ModelMonitoringSnapshot> previous, List<ModelMonitoringSnapshot> current)
    {
        if (previous.Count != current.Count) return true;
        var prior = previous.ToDictionary(s => (s.ModelId, s.TargetContractHash));
        foreach (var s in current)
            if (!prior.TryGetValue((s.ModelId, s.TargetContractHash), out var old)
                || old with { EvaluatedUtc = s.EvaluatedUtc } != s) return true;
        return false;
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
        if (snapshots.Any(s => s is null)
            || snapshots.Select(s => (s.ModelId, s.TargetContractHash)).Distinct().Count() != snapshots.Count)
            throw new InvalidDataException("Model health groups are duplicated.");
        foreach (var s in snapshots)
        {
            if (s is null || !ValidHash(s.ModelId) || !ValidHash(s.TargetContractHash)
                || s.MaturedCount < 0 || s.CorrectCount < 0 || s.CorrectCount > s.MaturedCount
                || !Enum.IsDefined(s.State) || s.EvaluatedUtc == default || s.EvaluatedUtc.Offset != TimeSpan.Zero
                || s.Accuracy != (s.MaturedCount == 0 ? null : (decimal?)s.CorrectCount / s.MaturedCount))
                throw new InvalidDataException("Model health state is invalid.");
            // A syntactically valid State that disagrees with f(N,K) is never a usable alert predecessor.
            if (s.State != PredictionMonitoringPolicy.ComputeHealthState(s.MaturedCount, s.CorrectCount))
                throw new InvalidDataException("Model health state does not match its counts.");
        }
        return snapshots;
    }
}
