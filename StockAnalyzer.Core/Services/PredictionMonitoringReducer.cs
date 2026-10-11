using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// The single owner of the monitoring domain rules that do not depend on where the audit is stored: forecast preparation,
/// observation/audit/health validation, the maturity decision, and vote aggregation. Both storage backends (JSON files,
/// SQLite) call these functions, so the label, window and health rules exist exactly once. Nothing here performs I/O
/// other than reading the model for <see cref="PrepareAsync"/>.
/// </summary>
internal static class PredictionMonitoringReducer
{
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    public static bool ValidHash(string? value) => ContentHash.IsLowerHex64(value);

    public static void ValidateChronology(IReadOnlyList<CandleData> candles)
    {
        for (int i = 0; i < candles.Count; i++)
            if (candles[i].Close <= 0 || (i > 0 && candles[i].Timestamp <= candles[i - 1].Timestamp)
                || candles[i].Timestamp.Kind == DateTimeKind.Local)
                throw new InvalidDataException("Monitoring candles must have positive closes and chronological UTC timestamps.");
    }

    /// <summary>Source/finality-mask/chronology checks of a refresh, before any search or write.</summary>
    public static void ValidateObservation(MonitoringObservation observations, DateTimeOffset now)
    {
        if (!ValidHash(observations.SourceRevision) || observations.ObservedUtc.Offset != TimeSpan.Zero
            || observations.ObservedUtc > now || observations.Candles.IsDefault || observations.FinalBars.IsDefault
            || observations.Candles.Length != observations.FinalBars.Length)
            throw new InvalidDataException("Monitoring observation source is invalid.");
        ValidateChronology(observations.Candles);
    }

    /// <summary>The structural contract of one stored forecast with its outcome revisions.</summary>
    public static void ValidateAudit(PredictionAuditRecord? record, string predictionId)
    {
        if (record is null) throw new InvalidDataException("Prediction audit is empty.");
        if (record.SchemaVersion != PredictionMonitoringPolicy.SchemaVersion || record.Prediction is null
            || !ValidHash(record.Prediction.PredictionId) || !ValidHash(record.Prediction.InputRevision)
            || !ValidHash(record.Prediction.TargetContractHash) || !ValidHash(record.Prediction.FeatureContractHash)
            || !ValidHash(record.Prediction.ModelHash) || record.Prediction.HorizonBars <= 0
            || !Enum.IsDefined(record.Prediction.Timeframe) || record.Prediction.NeutralThreshold < 0
            || record.Prediction.PredictedLabel is not ("Up" or "Down" or "Neutral")
            || record.Prediction.AnchorUtc.Offset != TimeSpan.Zero || record.Prediction.RecordedUtc.Offset != TimeSpan.Zero
            || predictionId != record.Prediction.PredictionId || record.Outcomes.IsDefaultOrEmpty)
            throw new InvalidDataException("Prediction audit contract is invalid.");
        for (int i = 0; i < record.Outcomes.Length; i++)
        {
            var o = record.Outcomes[i];
            if (o is null || o.Revision != i || !ValidHash(o.SourceRevision) || !Enum.IsDefined(o.State)
                || o.ObservedUtc.Offset != TimeSpan.Zero || o.ObservedUtc == default
                || o.State == PredictionOutcomeState.Matured && (o.ActualLabel is not ("Up" or "Down" or "Neutral")
                    || o.LabelEndUtc is not { } end || end <= record.Prediction.AnchorUtc || end.Offset != TimeSpan.Zero)
                || o.State == PredictionOutcomeState.Unavailable && string.IsNullOrWhiteSpace(o.Error)
                || o.PendingReason is not null && (o.State != PredictionOutcomeState.Pending || !PendingReasons.IsDefined(o.PendingReason)))
                throw new InvalidDataException("Prediction outcome revision is invalid.");
        }
    }

    /// <summary>Unique groups, valid keys/counts/accuracy, and a State that equals f(N,K); never a usable predecessor otherwise.</summary>
    public static void ValidateHealth(List<ModelMonitoringSnapshot> snapshots)
    {
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
    }

    /// <summary>
    /// Builds the immutable forecast to record, or null when the result is not a monitorable classification. Reads the
    /// model once to bind the target/feature contract hashes.
    /// </summary>
    public static async Task<MonitoredPrediction?> PrepareAsync(IModelGenerationRegistry registry, TimeProvider clock,
        PredictionResult result, string symbol, TimeframeType timeframe, IReadOnlyList<CandleData> inputs, CancellationToken ct)
    {
        if (result.IsFallback || result.IsRegression || result.ModelId is not { } modelId || inputs.Count == 0
            || result.OutputContract is not { Semantic: PredictionOutputSemantic.ClassProbabilities } output
            || output.Timeframe != timeframe) return null;
        var captured = inputs.ToArray();
        ValidateChronology(captured);
        if (PredictionLogService.Utc(captured[^1].Timestamp) > clock.GetUtcNow())
            throw new InvalidDataException("Prediction anchor is in the future.");
        if (string.IsNullOrWhiteSpace(symbol)) throw new ArgumentException("Symbol is required.", nameof(symbol));
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var lease = registry.Acquire(modelId) ?? throw new InvalidDataException("Prediction generation is unavailable.");
            var manifest = registry.GetManifest(modelId) ?? throw new InvalidDataException("Prediction manifest is unavailable.");
            using var session = new InferenceSession(lease.ModelPath);
            var metadata = session.ModelMetadata.CustomMetadataMap;
            if (PredictionModelMetadata.ReadOutputContract(metadata) != output
                || !decimal.TryParse(metadata.GetValueOrDefault(PredictionModelMetadata.NeutralThresholdKey), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var threshold) || threshold < 0
                || result.Label is not ("Up" or "Down" or "Neutral"))
                throw new InvalidDataException("Prediction monitoring target contract is invalid.");
            var feature = FixedScaler.ComputeContractHash(metadata);
            var target = Hash(FormattableString.Invariant($"{output.Semantic}|{output.TargetFormula}|{output.Timeframe}|{output.HorizonBars}|{threshold}"));
            var anchor = PredictionLogService.Utc(captured[^1].Timestamp);
            // A corrected source at the same anchor does not invent another forecast vote.
            var id = Hash(FormattableString.Invariant($"{modelId}|{symbol}|{timeframe}|{anchor:O}|{feature}|{target}"));
            return (MonitoredPrediction?)new MonitoredPrediction(id, modelId, manifest.ModelHash, symbol, timeframe, anchor,
                output.HorizonBars, threshold, result.Label, target, feature, PredictionLogService.SourceRevision(captured), clock.GetUtcNow());
        }, ct).ConfigureAwait(false);
    }

    public static PredictionOutcomeRevision InitialOutcome(MonitoredPrediction prediction) =>
        new(0, prediction.InputRevision, PredictionOutcomeState.Pending, prediction.RecordedUtc);

    /// <summary>
    /// The new Matured revision a refresh appends for a forecast whose latest outcome is <paramref name="latest"/>, or null
    /// when the observation cannot (yet) change it: anchor absent, target beyond the observation, any bar of the window not
    /// final, target in the future, older observation, or identical already-matured content. Unknown never revokes.
    /// </summary>
    public static PredictionOutcomeRevision? TryMature(MonitoredPrediction p, PredictionOutcomeRevision latest,
        MonitoringObservation observations, MonitoringAnchorIndex rowByAnchor)
    {
        int anchor = rowByAnchor.Find(p.AnchorUtc);
        if (anchor < 0 || p.HorizonBars >= observations.Candles.Length - anchor) return null;
        int end = anchor + p.HorizonBars;
        bool final = true;
        for (int i = anchor; i <= end; i++) final &= observations.FinalBars[i];
        if (!final || PredictionLogService.Utc(observations.Candles[end].Timestamp) > observations.ObservedUtc) return null;
        var targetBars = observations.Candles.AsSpan(anchor, p.HorizonBars + 1).ToArray();
        var source = PredictionLogService.SourceRevision(targetBars);
        var label = PredictionLogService.ActualLabel(targetBars[0].Close, targetBars[^1].Close, p.NeutralThreshold);
        if (observations.ObservedUtc < latest.ObservedUtc) return null;
        if (latest.State == PredictionOutcomeState.Matured && latest.SourceRevision == source) return null;
        return new PredictionOutcomeRevision(checked(latest.Revision + 1), source, PredictionOutcomeState.Matured,
            observations.ObservedUtc, PredictionLogService.Utc(targetBars[^1].Timestamp), label, DatasetRevision: observations.SourceRevision);
    }

    public static PredictionOutcomeRevision Unavailable(PredictionOutcomeRevision latest, DateTimeOffset now, string terminalError) =>
        new(checked(latest.Revision + 1), latest.SourceRevision, PredictionOutcomeState.Unavailable, now, Error: terminalError);

    /// <summary>True when a Matured outcome counts as a vote at <paramref name="now"/>: now-30d &lt; LabelEnd &lt;= now.</summary>
    public static bool IsVote(PredictionOutcomeRevision latest, DateTimeOffset now) =>
        latest.State == PredictionOutcomeState.Matured
        && !(latest.LabelEndUtc <= now.AddDays(-PredictionMonitoringPolicy.LookbackCalendarDays) || latest.LabelEndUtc > now);

    /// <summary>The Healthy to Decayed transition is the only alerting edge.</summary>
    public static bool IsDecayTransition(ModelMonitoringSnapshot? prior, ModelHealthState state) =>
        prior?.State == ModelHealthState.Healthy && state == ModelHealthState.Decayed;

    public sealed record GroupVotes(int Count, int Correct, string VoteDigest);

    /// <summary>Counts each group's votes at one instant; every forecast registers its group, even with no vote.</summary>
    public sealed class VoteAggregator(DateTimeOffset now, bool withVoteDigest)
    {
        private readonly Dictionary<(string Model, string Target), (int Count, int Correct)> _groups = new();
        private readonly Dictionary<(string Model, string Target), List<string>>? _votes = withVoteDigest ? new() : null;

        /// <summary>Registers a group that may hold no vote (a forecast exists, none is a vote yet).</summary>
        public void Register(string model, string target) => _groups.TryAdd((model, target), (0, 0));

        public void Add(MonitoredPrediction p, PredictionOutcomeRevision latest)
        {
            var key = (p.ModelId, p.TargetContractHash);
            _groups.TryAdd(key, (0, 0));
            if (!IsVote(latest, now)) return;
            var count = _groups[key];
            _groups[key] = (checked(count.Count + 1), checked(count.Correct + (p.PredictedLabel == latest.ActualLabel ? 1 : 0)));
            if (_votes is null) return;
            if (!_votes.TryGetValue(key, out var list)) _votes[key] = list = new();
            list.Add(p.PredictionId + ":" + latest.Revision.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>Groups in first-seen order; the digest covers the (forecast, latest revision) pairs that vote.</summary>
        public IEnumerable<((string Model, string Target) Key, GroupVotes Votes)> Groups()
        {
            foreach (var (key, value) in _groups)
            {
                string digest = string.Empty;
                if (_votes is not null)
                {
                    var ordered = _votes.TryGetValue(key, out var list) ? list.OrderBy(v => v, StringComparer.Ordinal) : Enumerable.Empty<string>();
                    digest = Hash(string.Join("\n", ordered));
                }
                yield return (key, new GroupVotes(value.Count, value.Correct, digest));
            }
        }
    }

    /// <summary>Whether anything other than the evaluation instant differs (membership, N, K, Accuracy, State, LastAlertId).</summary>
    public static bool HealthChanged(List<ModelMonitoringSnapshot> previous, List<ModelMonitoringSnapshot> current)
    {
        if (previous.Count != current.Count) return true;
        var prior = previous.ToDictionary(s => (s.ModelId, s.TargetContractHash));
        foreach (var s in current)
            if (!prior.TryGetValue((s.ModelId, s.TargetContractHash), out var old)
                || old with { EvaluatedUtc = s.EvaluatedUtc } != s) return true;
        return false;
    }
}
