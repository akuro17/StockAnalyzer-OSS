using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;

namespace StockAnalyzer.Core.Services.Backtest.Evaluation;

/// <summary>
/// Read model of a persisted evaluation-evidence export (owner decision G4: internal typed archive only). It restores
/// the audit facts written by <see cref="BacktestEvaluationExportEnvelope"/> with their exact types; it is NOT an owned
/// execution artifact, carries no engine result, and no public import path or live-artifact constructor exists.
/// Property names deliberately match the export wrapper so the existing wire format is read unchanged.
/// </summary>
internal sealed class BacktestEvaluationArchive
{
    public int SchemaVersion { get; init; }
    public string? ModelId { get; init; }
    public ArchivedArtifact? Artifact { get; init; }
}

internal sealed class ArchivedArtifact
{
    public int AuditSchemaVersion { get; init; }
    public RunStatus RunStatus { get; init; }
    public int RiskExecutionMode { get; init; }
    public BacktestReport? Report { get; init; }
    public BacktestReportRunFingerprint? RunFingerprint { get; init; }
    public ArchivedMetadata? Metadata { get; init; }
    public ArchivedBootstrap? BootstrapDiagnostics { get; init; }
    public ArchivedSampling? Sampling { get; init; }
    public ArchivedDrawdownResult? RatioDrawdown { get; init; }
    public ArchivedDrawdownResult? AmountDrawdown { get; init; }
    public ArchivedIdentity? ReportIdentity { get; init; }
}

internal sealed class ArchivedIdentity
{
    public bool IsAvailable { get; init; }
    public string? Sha256 { get; init; }
    public int SchemaVersion { get; init; }
    public string? UnavailableReason { get; init; }
}

internal sealed class ArchivedInputDataReference
{
    public string? SourceId { get; init; }
    public string? Version { get; init; }
    public string? UnavailableReason { get; init; }
}

internal sealed class ArchivedMetadata
{
    public string? Symbol { get; init; }
    public int InputDataVersion { get; init; }
    public TimeFrame Frame { get; init; }
    public DateTime RequestedStartUtc { get; init; }
    public DateTime RequestedEndUtc { get; init; }
    public DateTime? LastProcessedTimestamp { get; init; }
    public int HistoryStartIndex { get; init; }
    public int TradingStartIndex { get; init; }
    public int SampleCount { get; init; }
    public int AnnualPeriods { get; init; }
    public decimal AnnualRiskFreeRate { get; init; }
    public decimal AnnualMAR { get; init; }
    public int BootstrapSeed { get; init; }
    public int BootstrapIterations { get; init; }
    public int FormulaVersion { get; init; }
    public string? AssemblyIdentity { get; init; }
    public string? RuntimeIdentity { get; init; }
    public ArchivedInputDataReference? InputDataReference { get; init; }
}

internal sealed class ArchivedBootstrap
{
    public int MethodVersion { get; init; }
    public int? EffectiveBlockLength { get; init; }
    public int? ValidReplicates { get; init; }
    public int? ExecutedReplicates { get; init; }
    public bool DegenerateConstant { get; init; }
    public string? Reason { get; init; }
}

internal sealed class ArchivedSampling
{
    public SamplingStatus Status { get; init; }
    public SamplingReason Reason { get; init; }
    public string? ProviderId { get; init; }
    public string? CalendarId { get; init; }
    public string? CalendarVersion { get; init; }
    public string? ExchangeId { get; init; }
    public string? TimeZoneId { get; init; }
    public TimestampConvention? TimestampConvention { get; init; }
    public TimeFrame Frame { get; init; }
    public int? ExpectedCount { get; init; }
    public int ObservedCount { get; init; }
    public string? ExpectedTimestampsDigest { get; init; }
}

internal sealed class ArchivedDuration
{
    public TimeSpan? Value { get; init; }
    public string? Reason { get; init; }
}

internal sealed class ArchivedDrawdownEpisode
{
    public int PeakIndex { get; init; }
    public int TroughIndex { get; init; }
    public int? RecoveryIndex { get; init; }
    public DateTime PeakUtc { get; init; }
    public DateTime TroughUtc { get; init; }
    public DateTime? RecoveryUtc { get; init; }
    public decimal PeakEquity { get; init; }
    public decimal TroughEquity { get; init; }
    public decimal Depth { get; init; }
    public MetricUnit Unit { get; init; }
    public int DeclineBars { get; init; }
    public int? RecoveryBars { get; init; }
    public int? UnderwaterBars { get; init; }
    public ArchivedDuration? DeclineDuration { get; init; }
    public ArchivedDuration? RecoveryDuration { get; init; }
    public ArchivedDuration? UnderwaterDuration { get; init; }
}

internal sealed class ArchivedDrawdownResult
{
    public DrawdownEpisodeStatus Status { get; init; }
    public string? Reason { get; init; }
    public ArchivedDrawdownEpisode? Episode { get; init; }
}

/// <summary>
/// Typed reader for <see cref="BacktestEvaluationArchive"/>; a malformed, unsupported or internally inconsistent document throws
/// <see cref="InvalidDataException"/>. Verification/test use only (owner decision G4): it checks structure and invariants, it does not
/// recompute the report identity, so a passing read is not proof of origin or of content integrity.
/// </summary>
internal static class BacktestEvaluationArchiveReader
{
    private const int Sha256HexLength = SHA256.HashSizeInBytes * 2;

    public static BacktestEvaluationArchive Read(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        BacktestEvaluationArchive? archive;
        try
        {
            archive = JsonSerializer.Deserialize<BacktestEvaluationArchive>(json);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new InvalidDataException("The evaluation evidence document could not be read as the typed archive.", ex);
        }

        if (archive?.Artifact is not { } artifact)
        {
            throw new InvalidDataException("The evaluation evidence document has no artifact.");
        }
        if (archive.SchemaVersion != BacktestEvaluationExportEnvelope.CurrentSchemaVersion ||
            archive.ModelId != BacktestEvaluationExportEnvelope.LegacyModelId)
        {
            throw new InvalidDataException("Unsupported evaluation evidence schema or model.");
        }
        if (artifact.AuditSchemaVersion != BacktestEvaluationArtifact.CurrentAuditSchemaVersion ||
            artifact.RiskExecutionMode != BacktestEvaluationArtifact.LegacyRiskExecutionMode)
        {
            throw new InvalidDataException("Unsupported audit schema or risk execution mode.");
        }
        if (artifact.Metadata is not { InputDataReference: not null } metadata ||
            artifact.BootstrapDiagnostics is not { } bootstrap || artifact.Sampling is not { } sampling ||
            artifact.RatioDrawdown is not { } ratio || artifact.AmountDrawdown is not { } amount ||
            artifact.ReportIdentity is not { } reportIdentity || artifact.RunFingerprint is not { } runFingerprint)
        {
            throw new InvalidDataException("A required evaluation evidence section is missing.");
        }

        if (!Enum.IsDefined(artifact.RunStatus) || !Enum.IsDefined(metadata.Frame) ||
            !Enum.IsDefined(sampling.Status) || !Enum.IsDefined(sampling.Reason) || !Enum.IsDefined(sampling.Frame) ||
            (sampling.TimestampConvention is { } convention && !Enum.IsDefined(convention)))
        {
            throw new InvalidDataException("An enumeration value is not defined.");
        }

        ValidateMetadata(metadata);
        ValidateBootstrap(bootstrap);
        ValidateSampling(sampling);
        ValidateIdentity(runFingerprint.IsAvailable, runFingerprint.Sha256, runFingerprint.UnavailableReason, "Run fingerprint");
        ValidateIdentity(reportIdentity.IsAvailable, reportIdentity.Sha256, reportIdentity.UnavailableReason, "Report identity");
        if (!runFingerprint.IsAvailable && reportIdentity.IsAvailable)
        {
            throw new InvalidDataException("A report identity cannot be available when the run identity is unavailable.");
        }

        bool completed = artifact.RunStatus == RunStatus.Completed;
        ValidateDrawdown(ratio, MetricUnit.DrawdownRatio, completed, "Ratio drawdown");
        ValidateDrawdown(amount, MetricUnit.Currency, completed, "Amount drawdown");

        if (artifact.Report is { } report)
        {
            if (!completed)
            {
                throw new InvalidDataException("A partial run must not carry a report.");
            }
            try
            {
                BacktestReportValidator.Validate(report);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException("The embedded report is invalid.", ex);
            }
            if (report.RunFingerprint != runFingerprint)
            {
                throw new InvalidDataException("The report run fingerprint differs from the artifact run fingerprint.");
            }
        }
        else if (completed)
        {
            throw new InvalidDataException("A completed evaluation must contain a report.");
        }
        return archive;
    }

    private static InvalidDataException Invalid(string message) => new(message);

    private static bool IsUpperHexSha256(string? value)
    {
        if (value is null || value.Length != Sha256HexLength) return false;
        foreach (char c in value)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'A' and <= 'F'))) return false;
        }
        return true;
    }

    private static void ValidateIdentity(bool isAvailable, string? sha256, string? unavailableReason, string name)
    {
        if (isAvailable)
        {
            if (!IsUpperHexSha256(sha256) || unavailableReason is not null)
            {
                throw Invalid($"{name} is available but its digest is not a 64-character upper-case hexadecimal SHA-256 or a reason is present.");
            }
        }
        else if (sha256 is not null || string.IsNullOrWhiteSpace(unavailableReason))
        {
            throw Invalid($"{name} is unavailable but carries a digest or no reason.");
        }
    }

    private static void ValidateMetadata(ArchivedMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata.Symbol) || metadata.AssemblyIdentity is null || metadata.RuntimeIdentity is null)
        {
            throw Invalid("Metadata identity strings are missing.");
        }
        if (metadata.RequestedStartUtc.Kind != DateTimeKind.Utc || metadata.RequestedEndUtc.Kind != DateTimeKind.Utc ||
            (metadata.LastProcessedTimestamp is { } last && last.Kind != DateTimeKind.Utc))
        {
            throw Invalid("Timestamps must be UTC.");
        }
        if (metadata.RequestedStartUtc > metadata.RequestedEndUtc)
        {
            throw Invalid("The requested period is reversed.");
        }
        if (metadata.HistoryStartIndex < 0 || metadata.TradingStartIndex < metadata.HistoryStartIndex ||
            metadata.SampleCount < 0 || metadata.AnnualPeriods < 1 || metadata.BootstrapIterations < 0 || metadata.FormulaVersion < 1)
        {
            throw Invalid("Metadata counts, indices or versions are out of range.");
        }
    }

    private static void ValidateBootstrap(ArchivedBootstrap bootstrap)
    {
        if (bootstrap.MethodVersion < 1 ||
            bootstrap.EffectiveBlockLength is < 1 || bootstrap.ValidReplicates is < 0 || bootstrap.ExecutedReplicates is < 0)
        {
            throw Invalid("Bootstrap diagnostics are out of range.");
        }
    }

    private static void ValidateSampling(ArchivedSampling sampling)
    {
        if (sampling.ObservedCount < 0 || sampling.ExpectedCount is < 0)
        {
            throw Invalid("Sampling counts must be non-negative.");
        }
        if (sampling.ExpectedTimestampsDigest is not null && !IsUpperHexSha256(sampling.ExpectedTimestampsDigest))
        {
            throw Invalid("The expected-timestamps digest is not a 64-character upper-case hexadecimal SHA-256.");
        }
        if ((sampling.Status == SamplingStatus.Verified) != (sampling.Reason == SamplingReason.None))
        {
            throw Invalid("Sampling status Verified must coincide with reason None.");
        }
    }

    private static void ValidateDrawdown(ArchivedDrawdownResult result, MetricUnit expectedUnit, bool completed, string name)
    {
        if (!Enum.IsDefined(result.Status))
        {
            throw Invalid($"{name} status is not defined.");
        }
        if ((result.Status == DrawdownEpisodeStatus.NotComputedPartial) == completed)
        {
            throw Invalid($"{name} status NotComputedPartial must coincide with a non-completed run.");
        }
        if ((result.Status == DrawdownEpisodeStatus.Available) != (result.Episode is not null))
        {
            throw Invalid($"{name} status Available must coincide with the presence of an episode.");
        }
        if (result.Status == DrawdownEpisodeStatus.Available ? result.Reason is not null : string.IsNullOrWhiteSpace(result.Reason))
        {
            throw Invalid($"{name} reason must be absent for Available and present otherwise.");
        }
        if (result.Episode is { } episode)
        {
            ValidateEpisode(episode, expectedUnit, name);
        }
    }

    private static void ValidateEpisode(ArchivedDrawdownEpisode e, MetricUnit expectedUnit, string name)
    {
        if (e.Unit != expectedUnit)
        {
            throw Invalid($"{name} episode unit does not match its series.");
        }
        if (e.DeclineDuration is null || e.RecoveryDuration is null || e.UnderwaterDuration is null)
        {
            throw Invalid($"{name} episode is missing a duration.");
        }
        if (e.PeakUtc.Kind != DateTimeKind.Utc || e.TroughUtc.Kind != DateTimeKind.Utc ||
            (e.RecoveryUtc is { } recoveryUtc && recoveryUtc.Kind != DateTimeKind.Utc))
        {
            throw Invalid($"{name} episode timestamps must be UTC.");
        }
        if (e.PeakIndex < 0 || e.TroughIndex < e.PeakIndex || (e.RecoveryIndex is { } r && r <= e.TroughIndex))
        {
            throw Invalid($"{name} episode indices are out of order.");
        }
        if (e.TroughUtc < e.PeakUtc || (e.RecoveryUtc is { } recovered && recovered < e.TroughUtc))
        {
            throw Invalid($"{name} episode timestamps are out of order.");
        }
        bool recoveredEpisode = e.RecoveryIndex.HasValue;
        if (recoveredEpisode != e.RecoveryUtc.HasValue || recoveredEpisode != e.RecoveryBars.HasValue || recoveredEpisode != e.UnderwaterBars.HasValue)
        {
            throw Invalid($"{name} episode recovery fields must be all present or all absent.");
        }
        if (e.DeclineBars != e.TroughIndex - e.PeakIndex ||
            e.RecoveryBars != e.RecoveryIndex - e.TroughIndex ||
            e.UnderwaterBars != e.RecoveryIndex - e.PeakIndex)
        {
            throw Invalid($"{name} episode bar counts do not match its indices.");
        }
        if (e.Depth < 0m)
        {
            throw Invalid($"{name} episode depth must not be negative.");
        }
        foreach (ArchivedDuration duration in new[] { e.DeclineDuration, e.RecoveryDuration, e.UnderwaterDuration })
        {
            if ((duration.Value is not null && duration.Reason is not null) || duration.Value is { Ticks: < 0 })
            {
                throw Invalid($"{name} episode duration is inconsistent.");
            }
        }
        if (e.DeclineDuration.Value is null && e.DeclineDuration.Reason is null)
        {
            throw Invalid($"{name} episode decline duration must carry a value or a reason.");
        }
        if (!recoveredEpisode && (e.RecoveryDuration.Value is not null || e.UnderwaterDuration.Value is not null ||
                                  e.RecoveryDuration.Reason is not null || e.UnderwaterDuration.Reason is not null))
        {
            throw Invalid($"{name} unrecovered episode must have empty recovery durations.");
        }
    }
}
