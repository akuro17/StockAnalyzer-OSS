using System;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Models.ReinforcementLearning;

/// <summary>Reporting contract only. These constants are deliberately outside the numerical policy contract.</summary>
public static class RlConstraintContract
{
    public const string SpecificationId = "SA-RL-CONSTRAINT-REPORT-R04-v1";
    public const string MetadataKey = "constraint_diagnostics_specification_id";
    public const string SelectionMetric = "mean_undiscounted_return";
    public const string InvestmentEfficacy = "NotEstablished";
}

public enum RlEntryConstraintStatus { NotApplicable, Unconstrained, CapConstrained, NoAdmittedBuyBecauseOfCap }

/// <summary>One eligible Buy. Executable quantity is confirmed full-fill admission, zero on rejection, unknown while pending.</summary>
public sealed record RlEntryExecution(int Decision, DateTime DecisionTime, decimal DecisionClose, decimal DecisionEquity,
    decimal SizeRatio, decimal StopDistance, decimal DecisionBudget, decimal CashBeforeEntry, decimal RequestedQuantity,
    decimal? ExecutableQuantity, decimal FilledQuantity, decimal? FillPrice, decimal FilledValue, decimal HeldMargin,
    decimal Commission, long? OrderId, RlActionOutcome Outcome, RejectedReason? RejectionReason);

public sealed record RlEntryConstraintSummary(int EligibleBuys, int ZeroQuantity, int QuantityCapExceeded,
    int InsufficientFunds, int ActualEntries, RlEntryConstraintStatus Status);
public sealed record RlExitCostSensitivity(decimal TerminalClose, decimal? SyntheticExitPrice, decimal AdditionalCommission,
    decimal AdjustedEquity, decimal AdjustedReturn);
public sealed record RlConstraintEpisode(string SpecificationId, RlEntryConstraintSummary Entries,
    ImmutableArray<RlEntryExecution> Attempts, decimal ResidualQuantity, decimal CloseMarkMaximumDrawdown,
    decimal MarkEquity, RlExitCostSensitivity ExitCostSensitivity);

public sealed record RlPartitionCoverage(string Partition, int PotentialCompleteBlocks, int SelectedBlocks,
    int ExcludedBlocks, int UnusedCompleteBlocks, int UnusedRemainderBars, int EvaluatedBlocks, int EvaluatedDecisions);
public sealed record RlEvaluationCoverage(DateTime SupportStart, DateTime SupportEnd, int SupportBars,
    DateTime PossibleDecisionStart, DateTime PossibleDecisionEnd, int PossibleDecisionBars,
    ImmutableArray<RlPartitionCoverage> Partitions);
public sealed record RlPairedPolicyDelta(string BlockId, string Partition, decimal ActorReturn, decimal BaselineReturn, decimal Delta);
public sealed record RlPolicyConstraintSummary(string Partition, string PolicyId, int EvaluatedEpisodes, RlEntryConstraintSummary Entries);
public sealed record RlTerminalMarkSummary(string BlockId, string Partition, decimal ResidualQuantity, decimal MarkReturn,
    decimal CloseMarkMaximumDrawdown, decimal AdjustedReturn);
public sealed record RlConstraintReport(string SpecificationId, string SourceRevision, string ContractHash,
    RlEvaluationCoverage Coverage, RlEntryConstraintSummary Validation, RlEntryConstraintSummary Oos,
    ImmutableArray<RlPairedPolicyDelta> PairedDeltas, ImmutableArray<RlPolicyConstraintSummary> BaselinePolicies,
    ImmutableArray<RlTerminalMarkSummary> TerminalMarks,
    string SelectionMetric, decimal LearningGamma, int HorizonBars,
    string PricePit, string EngineeringEvidence, RlTemporalBoundStatus TemporalDependencyEvidence, string InvestmentEfficacy);

public sealed record RlEntryControl(decimal SizeRatio, decimal StopDistance, bool RetainedFromActualEntry);
