using System;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json.Serialization;

namespace StockAnalyzer.Core.Models.ReinforcementLearning;

/// <summary>A qualification revision, separate from the unchanged numerical T15/T16 contracts.</summary>
public static class RlTemporalContract
{
    public const int SchemaVersion = 1;
    public const string SpecificationId = "SA-RL-PREFROZEN-EXCHANGE-DATE-R01-v1";
    public const string EvidenceFile = "temporal.json";
    public const string DirectionEvidenceFile = "direction_temporal.json";
    public const string ProducerRevision = "core-ipc-observed-support-v1";
    public const string ExternalSelectionStatus = "Unverified";
    public const string ReportOnlyInformationUse = "report-only-oos;excluded-from-fit-and-selection-by-reviewed-producer";
    public const string LegacyDisclosure = "Legacy research generation: dependency temporal qualification is unverified.";
}

public enum RlTemporalBoundStatus { Unverified, ExactDerived, ConservativeUpperBound }
public enum RlTemporalRejection { MissingEvidence, UnsupportedVersion, IdentityMismatch, InconsistentUsage, FutureInformation, UnknownProducer }

public sealed class RlTemporalException : IOException
{
    public RlTemporalRejection Reason { get; }
    public DateOnly? InformationUpperExclusive { get; }
    public DateOnly? DecisionExchangeDate { get; }
    public RlTemporalException(RlTemporalRejection reason, string message,
        DateOnly? informationUpperExclusive = null, DateOnly? decisionExchangeDate = null) : base(message)
    { Reason = reason; InformationUpperExclusive = informationUpperExclusive; DecisionExchangeDate = decisionExchangeDate; }
}

/// <summary>Observed result/feature support, including reset observations and transition result bars.</summary>
public sealed record RlInformationUsage(string Role, int Seed, int Checkpoint, string BlockId,
    int LastObservedIndex, int Transitions, int Resets);
public sealed record RlInformationBound(RlTemporalBoundStatus Status, int LastUsedIndex, DateOnly UpperExclusive, string Derivation);

/// <summary>Declared information cutoff, not historical price availability or external researcher selection proof.</summary>
public sealed record RlTemporalEvidence(int SchemaVersion, string SpecificationId, string ProducerRevision,
    string Symbol, string SourceRevision, string TrainerSha256,
    ImmutableSortedDictionary<string, string> ArtifactHashes,
    RlInformationBound Training, RlInformationBound Scaler, RlInformationBound Selection,
    DateOnly InformationUpperExclusive, string ExternalSelectionStatus, string PricePitStatus)
{
    /// <summary>Report-only OOS is known producer use, excluded from weights/selection by the reviewed producer.</summary>
    public string OtherInformationUse { get; init; } = RlTemporalContract.ReportOnlyInformationUse;
    public string DateBasis { get; init; } = RlSourceContract.DateLabelSemantics;
    [JsonIgnore] public RlTemporalBoundStatus Status =>
        Training?.Status == RlTemporalBoundStatus.Unverified || Scaler?.Status == RlTemporalBoundStatus.Unverified || Selection?.Status == RlTemporalBoundStatus.Unverified
            ? RlTemporalBoundStatus.Unverified
            : Training?.Status == RlTemporalBoundStatus.ConservativeUpperBound || Scaler?.Status == RlTemporalBoundStatus.ConservativeUpperBound
                || Selection?.Status == RlTemporalBoundStatus.ConservativeUpperBound ? RlTemporalBoundStatus.ConservativeUpperBound : RlTemporalBoundStatus.ExactDerived;
}
