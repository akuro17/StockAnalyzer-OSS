using System.Collections.Immutable;
using System.Security.Cryptography;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;

namespace StockAnalyzer.Core.Services.Backtest.Evaluation;

public enum SamplingStatus
{
    Verified = 0,
    Unverified = 1,
    Rejected = 2,
}

public enum SamplingReason
{
    None = 0,
    NoEvidence = 1,
    UntrustedEvidence = 2,
    IncompleteEvidence = 3,
    InvalidEvidence = 4,
    MissingOrUnexpectedBar = 5,
}

public enum TimestampConvention
{
    PeriodStart = 0,
    PeriodEnd = 1,
}

/// <summary>Provider-produced expected timestamp series. Validation is intentionally performed by the owned evaluation boundary.</summary>
public sealed class SamplingEvidence
{
    public string ProviderId { get; }
    public string CalendarId { get; }
    public string CalendarVersion { get; }
    public string ExchangeId { get; }
    public string TimeZoneId { get; }
    public string Symbol { get; }
    public TimeFrame Frame { get; }
    public TimestampConvention TimestampConvention { get; }
    public DateTime RequestedStartUtc { get; }
    public DateTime RequestedEndUtc { get; }
    public bool Complete { get; }
    public ImmutableArray<DateTime> ExpectedTimestamps { get; }

    public SamplingEvidence(
        string providerId,
        string calendarId,
        string calendarVersion,
        string exchangeId,
        string timeZoneId,
        string symbol,
        TimeFrame frame,
        TimestampConvention timestampConvention,
        DateTime requestedStartUtc,
        DateTime requestedEndUtc,
        bool complete,
        ImmutableArray<DateTime> expectedTimestamps)
    {
        ProviderId = providerId;
        CalendarId = calendarId;
        CalendarVersion = calendarVersion;
        ExchangeId = exchangeId;
        TimeZoneId = timeZoneId;
        Symbol = symbol;
        Frame = frame;
        TimestampConvention = timestampConvention;
        RequestedStartUtc = requestedStartUtc;
        RequestedEndUtc = requestedEndUtc;
        Complete = complete;
        ExpectedTimestamps = expectedTimestamps;
    }
}

/// <summary>Trust boundary for evidence supplied by a configured provider. Metadata equality alone is not approval.</summary>
public interface ISamplingEvidenceTrustPolicy
{
    bool IsApproved(SamplingEvidence evidence);
}

public sealed class NoSamplingEvidenceTrustPolicy : ISamplingEvidenceTrustPolicy
{
    public bool IsApproved(SamplingEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return false;
    }
}

public sealed class SamplingQualification
{
    public SamplingStatus Status { get; }
    public SamplingReason Reason { get; }
    public string? ProviderId { get; }
    public string? CalendarId { get; }
    public string? CalendarVersion { get; }
    public string? ExchangeId { get; }
    public string? TimeZoneId { get; }
    public TimestampConvention? TimestampConvention { get; }
    public TimeFrame Frame { get; }
    public int? ExpectedCount { get; }
    public int ObservedCount { get; }
    public string? ExpectedTimestampsDigest { get; }

    internal SamplingQualification(
        SamplingStatus status,
        SamplingReason reason,
        TimeFrame frame,
        int observedCount,
        SamplingEvidence? evidence,
        string? expectedTimestampsDigest)
    {
        Status = status;
        Reason = reason;
        Frame = frame;
        ObservedCount = observedCount;
        ProviderId = evidence?.ProviderId;
        CalendarId = evidence?.CalendarId;
        CalendarVersion = evidence?.CalendarVersion;
        ExchangeId = evidence?.ExchangeId;
        TimeZoneId = evidence?.TimeZoneId;
        TimestampConvention = evidence is null || !Enum.IsDefined(evidence.TimestampConvention)
            ? null
            : evidence.TimestampConvention;
        ExpectedCount = evidence is null || evidence.ExpectedTimestamps.IsDefault
            ? null
            : evidence.ExpectedTimestamps.Length;
        ExpectedTimestampsDigest = expectedTimestampsDigest;
    }
}

internal static class SamplingEvidenceQualifier
{
    private const string ExpectedBarsDomain = "StockAnalyzer.Backtest.ExpectedBars";

    public static SamplingQualification Qualify(
        BacktestInput input,
        SamplingEvidence? evidence,
        ISamplingEvidenceTrustPolicy trustPolicy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(trustPolicy);
        cancellationToken.ThrowIfCancellationRequested();

        int start = Math.Min(input.HistoryStartIndex, input.Bars.Length);
        int observedCount = input.Bars.Length - start;
        if (evidence is null)
        {
            return new SamplingQualification(
                SamplingStatus.Unverified,
                SamplingReason.NoEvidence,
                input.Frame,
                observedCount,
                null,
                null);
        }

        bool structurallyValid = IsStructurallyValid(input, evidence, cancellationToken);
        string? digest = structurallyValid ? TryComputeDigest(evidence.ExpectedTimestamps, cancellationToken) : null;
        if (!trustPolicy.IsApproved(evidence))
        {
            return new SamplingQualification(
                SamplingStatus.Unverified,
                SamplingReason.UntrustedEvidence,
                input.Frame,
                observedCount,
                evidence,
                digest);
        }

        if (!structurallyValid)
        {
            return new SamplingQualification(
                SamplingStatus.Rejected,
                SamplingReason.InvalidEvidence,
                input.Frame,
                observedCount,
                evidence,
                null);
        }

        if (!evidence.Complete)
        {
            return new SamplingQualification(
                SamplingStatus.Unverified,
                SamplingReason.IncompleteEvidence,
                input.Frame,
                observedCount,
                evidence,
                digest);
        }

        bool matches = evidence.ExpectedTimestamps.Length == observedCount;
        for (int i = 0; matches && i < observedCount; i++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, i);
            matches = evidence.ExpectedTimestamps[i] == input.Bars[start + i].Timestamp;
        }

        return new SamplingQualification(
            matches ? SamplingStatus.Verified : SamplingStatus.Rejected,
            matches ? SamplingReason.None : SamplingReason.MissingOrUnexpectedBar,
            input.Frame,
            observedCount,
            evidence,
            digest);
    }

    private static bool IsStructurallyValid(BacktestInput input, SamplingEvidence evidence, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(evidence.ProviderId) ||
            string.IsNullOrWhiteSpace(evidence.CalendarId) ||
            string.IsNullOrWhiteSpace(evidence.CalendarVersion) ||
            string.IsNullOrWhiteSpace(evidence.ExchangeId) ||
            string.IsNullOrWhiteSpace(evidence.TimeZoneId) ||
            string.IsNullOrWhiteSpace(evidence.Symbol) ||
            !Enum.IsDefined(evidence.Frame) ||
            !Enum.IsDefined(evidence.TimestampConvention) ||
            evidence.RequestedStartUtc.Kind != DateTimeKind.Utc ||
            evidence.RequestedEndUtc.Kind != DateTimeKind.Utc ||
            evidence.RequestedStartUtc > evidence.RequestedEndUtc ||
            !string.Equals(evidence.Symbol, input.Symbol, StringComparison.Ordinal) ||
            evidence.Frame != input.Frame ||
            evidence.RequestedStartUtc != input.EvaluationStartUtc ||
            evidence.RequestedEndUtc != input.EvaluationEndUtc ||
            evidence.ExpectedTimestamps.IsDefault)
        {
            return false;
        }

        for (int i = 0; i < evidence.ExpectedTimestamps.Length; i++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, i);
            DateTime timestamp = evidence.ExpectedTimestamps[i];
            if (timestamp.Kind != DateTimeKind.Utc ||
                timestamp < evidence.RequestedStartUtc ||
                timestamp > evidence.RequestedEndUtc ||
                (i > 0 && timestamp <= evidence.ExpectedTimestamps[i - 1]))
            {
                return false;
            }
        }
        return true;
    }

    private static string? TryComputeDigest(ImmutableArray<DateTime> timestamps, CancellationToken cancellationToken)
    {
        if (timestamps.IsDefault) return null;
        try
        {
            using var writer = new CanonicalWriter();
            writer.String(ExpectedBarsDomain);
            writer.Count(timestamps.Length);
            for (int i = 0; i < timestamps.Length; i++)
            {
                MetricCalculation.CheckCancellation(cancellationToken, i);
                DateTime timestamp = timestamps[i];
                writer.Timestamp(timestamp);
            }
            return Convert.ToHexString(SHA256.HashData(writer.ToArray()));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
