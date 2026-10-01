using System;
using System.Collections.Immutable;

namespace StockAnalyzer.Core.Models;

public enum PredictionOutcomeState { Pending, Matured, Unavailable }
public enum ModelHealthState { InsufficientData, Healthy, Decayed }

public sealed record MonitoredPrediction(string PredictionId, string ModelId, string ModelHash,
    string Symbol, TimeframeType Timeframe, DateTimeOffset AnchorUtc, int HorizonBars,
    decimal NeutralThreshold, string PredictedLabel, string TargetContractHash,
    string FeatureContractHash, string InputRevision, DateTimeOffset RecordedUtc);

public sealed record PredictionOutcomeRevision(long Revision, string SourceRevision,
    PredictionOutcomeState State, DateTimeOffset ObservedUtc, DateTimeOffset? LabelEndUtc = null,
    string? ActualLabel = null, string? Error = null, string? DatasetRevision = null);

public sealed record PredictionAuditRecord(int SchemaVersion, MonitoredPrediction Prediction,
    ImmutableArray<PredictionOutcomeRevision> Outcomes);

public sealed record ModelMonitoringSnapshot(string ModelId, string TargetContractHash,
    int MaturedCount, int CorrectCount, decimal? Accuracy, ModelHealthState State,
    DateTimeOffset EvaluatedUtc, string? LastAlertId = null);

public sealed record ModelDecayAlert(string AlertId, ModelMonitoringSnapshot Snapshot);

/// <summary>Finality is supplied by an adopted observation adapter, never inferred by the monitor.</summary>
public sealed record MonitoringObservation(string SourceRevision, DateTimeOffset ObservedUtc,
    ImmutableArray<CandleData> Candles, ImmutableArray<bool> FinalBars);
