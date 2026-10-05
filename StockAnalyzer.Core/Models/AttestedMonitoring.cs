using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Models;

/// <summary>What a producer states about one bar. Unknown is the absence of a statement; only ExplicitlyRevoked withdraws.</summary>
public enum BarFinality { Unknown = 0, Final = 1, ExplicitlyRevoked = 2 }

/// <summary>The producer's per-bar evidence. All instants are UTC; the application never derives them from an exchange calendar.</summary>
public sealed record BarEvidence(DateTimeOffset AnchorUtc, DateTimeOffset PeriodStartUtc, DateTimeOffset PeriodEndUtc,
    DateTimeOffset AvailableAtUtc, BarFinality Finality);

/// <summary>An explicit statement that a bar was deleted or revoked and is no longer present in the source rows.</summary>
public sealed record BarTombstone(DateTimeOffset AnchorUtc, DateTimeOffset AvailableAtUtc, string? Reason = null);

/// <summary>
/// One immutable producer observation. The authority is (ProviderId, InstrumentId, Timeframe, RevisionEpoch); within one
/// epoch ProviderSequence must increase, and a higher epoch is the only allowed sequence reset. Evidence is aligned with
/// the bars (same length, same anchors); deleted bars appear only in Tombstones.
/// </summary>
public sealed record AttestedMonitoringSnapshot(string ProviderId, string InstrumentId, TimeframeType Timeframe,
    long RevisionEpoch, ulong ProviderSequence, string SourceRevision, DateTimeOffset ObservedUtc,
    ImmutableArray<CandleData> Candles, ImmutableArray<BarEvidence> Evidence, ImmutableArray<BarTombstone> Tombstones);

/// <summary>Why a forecast is Pending although it once matured. Closed set; stored on the Pending outcome revision.</summary>
public static class PendingReasons
{
    public const string ProviderFinalityRevoked = "ProviderFinalityRevoked";
    public const string EvidenceConflict = "EvidenceConflict";

    public static bool IsDefined(string? reason) => reason is ProviderFinalityRevoked or EvidenceConflict;
}

/// <summary>A separate capability of a data source: producer-attested snapshots. Absent capability or file = null.</summary>
public interface IAttestedMonitoringDataSource
{
    /// <summary>The attested snapshot of an instrument, or null when this source has no attestation for it. A corrupt attestation throws.</summary>
    Task<AttestedMonitoringSnapshot?> LoadAttestedObservationAsync(string instrumentId, TimeFrame timeframe,
        CancellationToken ct = default);
}

/// <summary>A producer stream whose current sequence carries two different contents: an unresolved data-integrity fault.</summary>
public sealed record AttestedStreamFault(string ProviderId, string InstrumentId, TimeframeType Timeframe);

/// <summary>Read-only integrity diagnostics of the attested evidence; never a performance state.</summary>
public interface IAttestedEvidenceDiagnostics
{
    Task<ImmutableArray<AttestedStreamFault>> GetConflictedStreamsAsync(CancellationToken ct = default);
}

/// <summary>The diagnostics of a backend without attested evidence (the JSON backend): no stream is ever in conflict.</summary>
public sealed class NoAttestedEvidenceDiagnostics : IAttestedEvidenceDiagnostics
{
    public Task<ImmutableArray<AttestedStreamFault>> GetConflictedStreamsAsync(CancellationToken ct = default) =>
        Task.FromResult(ImmutableArray<AttestedStreamFault>.Empty);
}

/// <summary>A separate capability of the monitoring store: apply one producer snapshot in sequence order.</summary>
public interface IAttestedPredictionMonitoring
{
    /// <summary>Applies the snapshot in ONE transaction. Null throws ArgumentNullException, a blank instrument ArgumentException, corrupt evidence InvalidDataException before any write.</summary>
    Task RefreshAttestedAsync(AttestedMonitoringSnapshot snapshot, CancellationToken ct = default);
}
