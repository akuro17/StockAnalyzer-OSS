using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Chooses how a prediction result becomes a <see cref="MonitoredPrediction"/>: an ensemble result (<see cref="PredictionResult.Ensemble"/>)
/// is prepared here, every other result by <see cref="PredictionMonitoringReducer.PrepareAsync"/> exactly as before. Both storage backends call
/// <see cref="CreateAsync"/>. The ensemble forecast is one forecast of the group <see cref="EnsembleMonitoringIdentity.MonitoringId"/>; the member
/// generations receive no vote from it.
/// </summary>
internal static class MonitoredPredictionFactory
{
    public static Task<MonitoredPrediction?> CreateAsync(IModelGenerationRegistry registry, TimeProvider clock,
        PredictionResult result, string symbol, TimeframeType timeframe, IReadOnlyList<CandleData> inputs, CancellationToken ct) =>
        result.Ensemble is not null
            ? CreateEnsembleAsync(registry, clock, result, symbol, timeframe, inputs, ct)
            : PredictionMonitoringReducer.PrepareAsync(registry, clock, result, symbol, timeframe, inputs, ct);

    /// <summary>
    /// The immutable forecast of an ensemble result, or null when the result is not a monitorable ensemble classification (fallback, regression,
    /// a single-model result, no inputs, another output contract or time frame, or the <see cref="PredictionResult.UnknownLabel"/> label). Reads each member's
    /// model once to bind the target/feature contract hashes. Invalid inputs, an unavailable member or a contract that the members do not share throw.
    /// </summary>
    public static async Task<MonitoredPrediction?> CreateEnsembleAsync(IModelGenerationRegistry registry, TimeProvider clock,
        PredictionResult result, string symbol, TimeframeType timeframe, IReadOnlyList<CandleData> inputs, CancellationToken ct)
    {
        if (result.IsFallback || result.IsRegression || result.Ensemble is not { } spec || result.ModelId is not null
            || inputs.Count == 0
            || result.OutputContract is not { Semantic: PredictionOutputSemantic.ClassProbabilities } output
            || output.Timeframe != timeframe
            || result.Label == PredictionResult.UnknownLabel) return null;
        var captured = PredictionMonitoringReducer.CanonicalizeInputs(inputs, timeframe);
        PredictionMonitoringReducer.ValidateRecordInput(clock, captured, symbol);
        spec.Validate();
        ct.ThrowIfCancellationRequested();
        return await Task.Run(() =>
        {
            var contracts = new List<PredictionOutputContract>(spec.Members.Count);
            var thresholds = new List<decimal>(spec.Members.Count);
            var modelHashes = new List<string>(spec.Members.Count);
            var features = new List<string>(spec.Members.Count);
            foreach (var member in spec.Members)
            {
                ct.ThrowIfCancellationRequested();
                var manifest = registry.GetManifest(member.ModelId)
                    ?? throw new InvalidDataException("Ensemble member manifest is unavailable.");
                var meta = PredictionMonitoringReducer.GetOrReadModelMetadata(registry, member.ModelId);
                contracts.Add(meta.OutputContract);
                thresholds.Add(meta.NeutralThreshold);
                modelHashes.Add(manifest.ModelHash);
                features.Add(meta.FeatureContractHash);
            }

            var merged = EnsemblePredictionService.MergeOutputContract(contracts);
            if (merged != output || thresholds.Any(threshold => threshold != thresholds[0])
                || !PredictionMonitoringReducer.IsMonitorableLabel(result.Label))
                throw new InvalidDataException("Ensemble monitoring target contract is invalid.");

            var subject = spec.MonitoringId();
            var feature = PredictionMonitoringReducer.Hash(string.Join(EnsembleMonitoringIdentity.MonitoringHashSeparator, features));
            var target = PredictionMonitoringReducer.TargetContractHash(merged, thresholds[0]);
            var anchor = PredictionLogService.Utc(captured[^1].Timestamp);
            var id = PredictionMonitoringReducer.ForecastId(subject, symbol, timeframe, anchor, feature, target);
            return (MonitoredPrediction?)new MonitoredPrediction(id, subject,
                PredictionMonitoringReducer.Hash(string.Join(EnsembleMonitoringIdentity.MonitoringHashSeparator, modelHashes)), symbol, timeframe, anchor,
                merged.HorizonBars, thresholds[0], result.Label, target, feature, PredictionLogService.SourceRevision(captured),
                clock.GetUtcNow());
        }, ct).ConfigureAwait(false);
    }
}
