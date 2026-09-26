using System.Collections.Immutable;

namespace StockAnalyzer.Core.Models.Backtest.Engine.StrictEvidence;

public enum StrictEvidenceVerdict { False = 0, True = 1, Unknown = 2 }

public enum StrictMarketEventKind { Execution = 0, CloseAuction = 1, Valuation = 2 }

public enum StrictOrderIntent { EnterLong = 0, EnterShort = 1, ExitPosition = 2, LiquidatePosition = 3 }

public enum StrictOrderStatus { Pending = 0, Filled = 1, Cancelled = 2, Expired = 3, Rejected = 4 }

public enum StrictRunStatus
{
    Completed = 0,
    Cancelled = 1,
    Ambiguous = 2,
    EvidenceMissing = 3,
    Failed = 4,
    Insolvent = 5,
    Unsupported = 6,
}

public enum StrictRunPhase
{
    Created = 0,
    Validating = 1,
    Running = 2,
    Completed = 3,
    Cancelled = 4,
    Ambiguous = 5,
    EvidenceMissing = 6,
    Failed = 7,
    Insolvent = 8,
    Unsupported = 9,
}

/// <summary>
/// Numeric order is the canonical issue order: input, causality, ambiguity, missing evidence,
/// unsupported behavior, numeric/strategy failure, then an invalid order produced by a strategy.
/// </summary>
public enum StrictRunIssueCode
{
    InvalidInput = 0,
    CausalityViolation = 100,
    AmbiguousExecutionOrder = 200,
    NoMarketData = 300,
    MissingExecutionEvidence = 301,
    MissingLiquidityEvidence = 302,
    UnresolvedEvidenceId = 303,
    InsufficientDecisionEvidence = 304,
    UnsupportedCorporateAction = 400,
    UnsupportedCashTransfer = 401,
    UnsupportedBorrowing = 402,
    UnsupportedPriceAdjustment = 403,
    UnsupportedPointInTimeRevision = 404,
    NumericFailure = 500,
    StrategyFailure = 501,
    InvalidOrder = 600,
}

public sealed record StrictRunIssue(
    StrictRunIssueCode Code,
    string Message,
    ImmutableArray<string> RelatedIds);

public readonly record struct StrictSourceVersionRef(string SourceId, string DataVersion);

/// <summary>Immutable event identity used to prove order activation and trigger precedence.</summary>
public sealed record StrictCausalEventRef(
    string EventId,
    string SourceId,
    string DataVersion,
    DateTime EventTimeUtc,
    DateTime AvailableTimeUtc,
    DateTime EffectiveAvailabilityUtc,
    long? SourceSequence);

/// <summary>
/// Explicit provider capability and evidence-resolution boundary. It does not claim that a live
/// adapter is conforming; the caller must supply the approved contract and its resolvable IDs.
/// </summary>
public sealed class StrictEvidenceProviderContract
{
    public string ProviderId { get; }
    public string ContractVersion { get; }
    public bool SupportsSourceSequence { get; }
    public bool SupportsPointInTimeRevisions { get; }
    public bool SupportsCloseAuctionEvidence { get; }
    public int MaxEventCount { get; }
    public ImmutableHashSet<string> ResolvableEvidenceIds { get; }

    public StrictEvidenceProviderContract(
        string providerId,
        string contractVersion,
        bool supportsSourceSequence,
        bool supportsPointInTimeRevisions,
        bool supportsCloseAuctionEvidence,
        int maxEventCount,
        IEnumerable<string> resolvableEvidenceIds)
    {
        if (string.IsNullOrWhiteSpace(providerId)) throw new ArgumentException("ProviderId must not be blank.", nameof(providerId));
        if (string.IsNullOrWhiteSpace(contractVersion)) throw new ArgumentException("ContractVersion must not be blank.", nameof(contractVersion));
        if (maxEventCount <= 0) throw new ArgumentOutOfRangeException(nameof(maxEventCount), maxEventCount, "MaxEventCount must be > 0.");
        ArgumentNullException.ThrowIfNull(resolvableEvidenceIds);

        ImmutableHashSet<string> evidenceIds = resolvableEvidenceIds.ToImmutableHashSet(StringComparer.Ordinal);
        if (evidenceIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("ResolvableEvidenceIds must not contain blank values.", nameof(resolvableEvidenceIds));
        }

        ProviderId = providerId;
        ContractVersion = contractVersion;
        SupportsSourceSequence = supportsSourceSequence;
        SupportsPointInTimeRevisions = supportsPointInTimeRevisions;
        SupportsCloseAuctionEvidence = supportsCloseAuctionEvidence;
        MaxEventCount = maxEventCount;
        ResolvableEvidenceIds = evidenceIds;
    }
}

/// <summary>One immutable event supplied by an explicit evidence provider.</summary>
public sealed record StrictMarketEvent(
    string SourceId,
    string DataVersion,
    string EventId,
    DateTime EventTimeUtc,
    DateTime AvailableTimeUtc,
    long? SourceSequence,
    decimal Price,
    long Quantity,
    string CoverageId,
    string? RevisionOf,
    DateTime? KnowledgeTimeUtc,
    StrictMarketEventKind Kind,
    OrderSide? ExecutableOrderSide,
    string? LiquidityEvidenceId,
    bool HasCompleteCoverage);

public sealed class StrictBacktestConfiguration
{
    public decimal InitialCapital { get; init; }
    public decimal FlatCommission { get; init; }
    public decimal PerUnitCommission { get; init; }
    public decimal InitialMarginRatio { get; init; } = BacktestDefaults.InitialMarginRatio;
    public decimal MaintenanceMarginRatio { get; init; } = BacktestDefaults.MaintenanceMarginRatio;

    public void Validate()
    {
        if (InitialCapital <= 0m) throw new ArgumentOutOfRangeException(nameof(InitialCapital), InitialCapital, "InitialCapital must be > 0.");
        if (FlatCommission < 0m) throw new ArgumentOutOfRangeException(nameof(FlatCommission), FlatCommission, "FlatCommission must be >= 0.");
        if (PerUnitCommission < 0m) throw new ArgumentOutOfRangeException(nameof(PerUnitCommission), PerUnitCommission, "PerUnitCommission must be >= 0.");
        if (InitialMarginRatio <= 0m || InitialMarginRatio > 1m) throw new ArgumentOutOfRangeException(nameof(InitialMarginRatio), InitialMarginRatio, "InitialMarginRatio must satisfy 0 < r <= 1.");
        if (MaintenanceMarginRatio <= 0m || MaintenanceMarginRatio >= InitialMarginRatio)
        {
            throw new ArgumentOutOfRangeException(nameof(MaintenanceMarginRatio), MaintenanceMarginRatio, "MaintenanceMarginRatio must satisfy 0 < MMR < IMR.");
        }
    }
}

public readonly record struct StrictAccountState(
    decimal Cash,
    decimal HeldMargin,
    long PositionQuantity,
    decimal EntryPrice)
{
    public static StrictAccountState Initial(decimal initialCapital) => new(initialCapital, 0m, 0L, 0m);
    public bool IsFlat => PositionQuantity == 0;
}

public sealed record StrictOrderRequest(
    string ClientOrderId,
    StrictOrderIntent Intent,
    OrderType Type,
    long Quantity,
    decimal? LimitPrice,
    decimal? StopPrice,
    DateTime DecisionTimeUtc,
    DateTime CreationTimeUtc,
    DateTime EligibleFromUtc,
    ImmutableArray<string> DecisionDataEventIds,
    decimal? StopLossRatio = null,
    decimal? TakeProfitRatio = null);

public sealed record StrictOrderSnapshot(
    long OrderId,
    string ClientOrderId,
    StrictOrderIntent Intent,
    OrderSide Side,
    OrderType Type,
    long Quantity,
    decimal? LimitPrice,
    decimal? StopPrice,
    DateTime InformationTimeUtc,
    DateTime DecisionTimeUtc,
    DateTime CreationTimeUtc,
    DateTime EligibleFromUtc,
    ImmutableArray<string> DecisionDataEventIds,
    StrictCausalEventRef? ActivationEvidence,
    StrictCausalEventRef? TriggerEvidence,
    decimal? StopLossRatio,
    decimal? TakeProfitRatio,
    StrictOrderStatus Status,
    bool StopActivated,
    long? OcoGroupId,
    long? ParentFillId);

public readonly record struct StrictConditionProof(
    StrictEvidenceVerdict Verdict,
    string EvidenceId,
    string Explanation);

public readonly record struct StrictFillConditions(
    StrictConditionProof Active,
    StrictConditionProof Reachable,
    StrictConditionProof LiquidityAvailable,
    StrictConditionProof CapitalAvailable,
    StrictConditionProof MarginSatisfied)
{
    public bool HasFalse => Active.Verdict == StrictEvidenceVerdict.False ||
        Reachable.Verdict == StrictEvidenceVerdict.False ||
        LiquidityAvailable.Verdict == StrictEvidenceVerdict.False ||
        CapitalAvailable.Verdict == StrictEvidenceVerdict.False ||
        MarginSatisfied.Verdict == StrictEvidenceVerdict.False;

    public bool HasUnknown => Active.Verdict == StrictEvidenceVerdict.Unknown ||
        Reachable.Verdict == StrictEvidenceVerdict.Unknown ||
        LiquidityAvailable.Verdict == StrictEvidenceVerdict.Unknown ||
        CapitalAvailable.Verdict == StrictEvidenceVerdict.Unknown ||
        MarginSatisfied.Verdict == StrictEvidenceVerdict.Unknown;

    public bool IsFillable => !HasFalse && !HasUnknown;
}

public sealed record StrictFillAudit(
    int SchemaVersion,
    string ProviderContractVersion,
    ImmutableArray<StrictSourceVersionRef> ObservedDataVersions,
    ImmutableArray<string> DecisionDataRefs,
    ImmutableArray<StrictCausalEventRef> CausalEvidence,
    DateTime InformationTimeUtc,
    DateTime DecisionTimeUtc,
    DateTime CreationTimeUtc,
    DateTime EligibleFromUtc,
    decimal AvailableCashBefore,
    decimal HeldMarginBefore,
    long PositionBefore,
    decimal EntryPriceBefore,
    StrictOrderSnapshot OrderContext,
    StrictFillConditions Conditions,
    string ExecutionEventId,
    string LiquidityEvidenceId,
    long AllocatedQuantity,
    long RemainingQuantityAfter);

public sealed record StrictFill(
    long FillId,
    long OrderId,
    DateTime FillTimeUtc,
    long? SourceSequence,
    OrderSide Side,
    decimal Price,
    long Quantity,
    decimal Commission,
    StrictFillAudit WhyWasThisFillPossible,
    StrictAccountState StateAfter);

/// <summary>
/// Immutable prefix over one run-owned event array. Creating a later view never changes an earlier
/// view and does not copy the visible event history.
/// </summary>
public sealed class StrictMarketEventView : IReadOnlyList<StrictMarketEvent>
{
    private readonly ImmutableArray<StrictMarketEvent> _events;

    public int Count { get; }
    public int Length => Count;
    public bool IsEmpty => Count == 0;

    internal StrictMarketEventView(ImmutableArray<StrictMarketEvent> events, int count)
    {
        if (events.IsDefault) throw new ArgumentException("Events must be initialized.", nameof(events));
        if ((uint)count > (uint)events.Length) throw new ArgumentOutOfRangeException(nameof(count));
        _events = events;
        Count = count;
    }

    public StrictMarketEvent this[int index]
        => (uint)index < (uint)Count
            ? _events[index]
            : throw new ArgumentOutOfRangeException(nameof(index));

    public IEnumerator<StrictMarketEvent> GetEnumerator()
    {
        for (int index = 0; index < Count; index++)
        {
            yield return _events[index];
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed record StrictStrategyContext(
    DateTime SimulationTimeUtc,
    StrictMarketEventView VisibleEvents,
    StrictAccountState AccountState);

public sealed class StrictBacktestResult
{
    public StrictRunStatus Status { get; }
    public StrictAccountState FinalState { get; }
    public ImmutableArray<StrictOrderSnapshot> Orders { get; }
    public ImmutableArray<StrictFill> Fills { get; }
    public ImmutableArray<StrictRunIssue> Issues { get; }
    public ImmutableArray<StrictRunPhase> StateTransitions { get; }
    public string StrategyName { get; }

    public StrictBacktestResult(
        StrictRunStatus status,
        StrictAccountState finalState,
        ImmutableArray<StrictOrderSnapshot> orders,
        ImmutableArray<StrictFill> fills,
        ImmutableArray<StrictRunIssue> issues,
        ImmutableArray<StrictRunPhase> stateTransitions,
        string strategyName)
    {
        Status = status;
        FinalState = finalState;
        Orders = orders.IsDefault ? ImmutableArray<StrictOrderSnapshot>.Empty : orders;
        Fills = fills.IsDefault ? ImmutableArray<StrictFill>.Empty : fills;
        Issues = issues.IsDefault
            ? ImmutableArray<StrictRunIssue>.Empty
            : issues.OrderBy(issue => issue.Code).ThenBy(issue => issue.Message, StringComparer.Ordinal).ToImmutableArray();
        StateTransitions = stateTransitions.IsDefault ? ImmutableArray<StrictRunPhase>.Empty : stateTransitions;
        StrategyName = strategyName ?? throw new ArgumentNullException(nameof(strategyName));
    }
}
