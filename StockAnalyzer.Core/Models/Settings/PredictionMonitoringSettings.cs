using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Settings;

/// <summary>Storage backend of the prediction monitoring audit, selected once at startup.</summary>
public enum PredictionMonitoringBackendKind
{
    /// <summary>Legacy per-forecast JSON files (the default).</summary>
    Json,
    /// <summary>Dedicated transactional monitoring.db; reached by an explicit quiescent import and never left again automatically.</summary>
    Sqlite,
}

/// <summary>
/// Explicit enrollment of one forecast symbol in one producer-attested stream. The instrument is never inferred from the symbol
/// (no case folding, no suffix mapping): the producer's InstrumentId is stated here.
/// </summary>
public sealed class AttestedSourceEnrollment
{
    /// <summary>The forecast Symbol exactly as the forecasts are recorded.</summary>
    public string Symbol { get; set; } = string.Empty;
    public TimeframeType Timeframe { get; set; } = TimeframeType.Daily;
    public string ProviderId { get; set; } = string.Empty;
    /// <summary>The producer's instrument id, also the sidecar file name stem (<InstrumentId>.attest.json).</summary>
    public string InstrumentId { get; set; } = string.Empty;
}

/// <summary>
/// Bound from the "PredictionMonitoring" configuration section. Defaults keep the legacy JSON backend. Once a monitoring.db
/// import has completed its marker selects SQLite regardless of <see cref="Backend"/> (there is no automatic fallback).
/// Holds operational values only: the accepted bands live in <see cref="PredictionMonitoringLimits"/> and are checked by
/// <see cref="PredictionMonitoringSettingsValidator"/> at startup.
/// </summary>
public sealed class PredictionMonitoringSettings
{
    public const string SectionName = "PredictionMonitoring";

    /// <summary>The runtime folder of the monitoring feature under Data (audit files, monitoring.db, attestation working copies).</summary>
    public const string DataFolderName = "PredictionMonitoring";

    /// <summary>Requested backend. <see cref="PredictionMonitoringBackendKind.Sqlite"/> imports the V1 JSON files at the next start when no complete marker exists.</summary>
    public PredictionMonitoringBackendKind Backend { get; set; } = PredictionMonitoringBackendKind.Json;

    /// <summary>How long a SQLite writer waits for the database lock before a typed busy fault (engineering choice for bounded contention).</summary>
    public int BusyTimeoutSeconds { get; set; } = 5;

    /// <summary>Retry cadence of pending notification delivery while the application runs.</summary>
    public int OutboxPollIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Producer-attested evidence is OFF while this list is empty (the default): no sidecar is read and no table is created.
    /// It requires the SQLite backend.
    /// </summary>
    public List<AttestedSourceEnrollment> AttestedSources { get; set; } = [];

    /// <summary>Upper bound for one attestation sidecar (a resource guard, not a domain rule).</summary>
    public long AttestedManifestMaxBytes { get; set; } = 67108864;

    /// <summary>
    /// Age after which a private attestation working copy is a crash leftover and is removed (a copy is deleted right after its read;
    /// only a crash between copy and delete leaves one). Chosen well above the time one load can take.
    /// </summary>
    public int AttestedWorkingCopyRetentionMinutes { get; set; } = 60;

    /// <summary>Pause between polls of the weekly retraining scheduler (and the retry of a failed state load).</summary>
    public int SchedulerPollIntervalSeconds { get; set; } = 60;

    /// <summary>Pause between nonblocking attempts to take the short scheduler root lease when a terminal result must be persisted.</summary>
    public int LeaseRetryIntervalMilliseconds { get; set; } = 250;

    /// <summary>Cadence of the application-owned current-health evaluation loop (time moves the 30-day window even without new data).</summary>
    public int HealthEvaluationIntervalSeconds { get; set; } = 60;

    public TimeSpan BusyTimeout => TimeSpan.FromSeconds(BusyTimeoutSeconds);
    public TimeSpan OutboxPollInterval => TimeSpan.FromSeconds(OutboxPollIntervalSeconds);
    public TimeSpan AttestedWorkingCopyRetention => TimeSpan.FromMinutes(AttestedWorkingCopyRetentionMinutes);
    public TimeSpan SchedulerPollInterval => TimeSpan.FromSeconds(SchedulerPollIntervalSeconds);
    public TimeSpan LeaseRetryInterval => TimeSpan.FromMilliseconds(LeaseRetryIntervalMilliseconds);
    public TimeSpan HealthEvaluationInterval => TimeSpan.FromSeconds(HealthEvaluationIntervalSeconds);
}
