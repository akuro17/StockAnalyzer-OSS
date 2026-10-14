using System;
using System.Collections.Immutable;

namespace StockAnalyzer.Core.Models.ReinforcementLearning;

/// <summary>How the bars of an external replay window relate to the data the agent was trained on.</summary>
public enum RlReplayScope
{
    /// <summary>The window comes from the agent's own source snapshot (every replay of the training windows).</summary>
    TrainingSource = 0,
    SameInstrumentOverlapsTraining = 1,
    SameInstrumentAfterTraining = 2,
    SameInstrumentBeforeTraining = 3,
    OtherInstrument = 4,
}

/// <summary>
/// The parts of the agent's source period (3:1:1 split of its bars) that an external replay window overlaps. Train is the part its scaler and its
/// training episodes came from, Validation the part that chose its checkpoint, FinalEvaluation the part reserved for the final measurement.
/// </summary>
[Flags]
public enum RlSourceParts
{
    None = 0,
    Train = 1,
    Validation = 2,
    FinalEvaluation = 4,
}

public enum RlContinuityKind
{
    /// <summary>The largest number of calendar days between two consecutive bars.</summary>
    CalendarGap,
    /// <summary>The largest absolute close-to-close change as a fraction of the previous close (0.5 is a halving or a 50% rise).</summary>
    CloseChange,
}

/// <summary>
/// A value of the bars an external replay has shown that is larger than anything in the agent's own training bars.
/// <see cref="Observed"/> and <see cref="TrainedMaximum"/> are calendar days for a gap and a fraction for a change; <see cref="Date"/> is the later bar.
/// </summary>
public sealed record RlContinuityFinding(RlContinuityKind Kind, decimal Observed, decimal TrainedMaximum, DateOnly Date);

/// <summary>One input channel whose raw values in the replay window leave the range the agent's scaler was fitted on.</summary>
public sealed record RlScalerRangeFinding(string Channel, double TrainedMin, double TrainedMax, double WindowMin, double WindowMax);

public enum RlExternalReplayRejection
{
    NotEnoughHistory,
    NotEnoughBarsBefore,
    NotEnoughBarsAfter,
    NoLocalData,
    ContinuousNotSupported,
    PolicyNotAvailable,
    SourceUnavailable,
}

/// <summary>An expected, user-correctable reason why a ticker and date cannot start an external replay episode.</summary>
public sealed class RlExternalReplayException : Exception
{
    public RlExternalReplayRejection Reason { get; }
    /// <summary>For <see cref="RlExternalReplayRejection.NotEnoughBarsBefore"/>: the first usable anchor date.</summary>
    public DateOnly? EarliestAnchor { get; }
    /// <summary>For <see cref="RlExternalReplayRejection.NotEnoughBarsAfter"/>: the last usable anchor date.</summary>
    public DateOnly? LatestAnchor { get; }

    public RlExternalReplayException(RlExternalReplayRejection reason, string message, DateOnly? earliestAnchor = null,
        DateOnly? latestAnchor = null, Exception? inner = null) : base(message, inner)
    {
        Reason = reason;
        EarliestAnchor = earliestAnchor;
        LatestAnchor = latestAnchor;
    }
}

/// <param name="AnchorDate">The date of the 64th bar of the 192-bar episode (the last bar the agent sees at decision 0).</param>
/// <param name="DecisionCount">The decision position: the number of decisions already taken, 0 to <see cref="RlContract.EpisodeHorizon"/> - 1.</param>
public sealed record RlExternalReplayRequest(string ModelId, string Symbol, DateOnly AnchorDate, int DecisionCount, string PolicyId);

/// <param name="RequestedAnchorDate">The date the caller asked for.</param>
/// <param name="ResolvedAnchorDate">The trading day actually used (the same date when it was a trading day of the ticker).</param>
public sealed record RlExternalReplayResult(RlQSnapshot Snapshot, DateOnly RequestedAnchorDate, DateOnly ResolvedAnchorDate);

/// <summary>The first and last anchor dates a ticker's stored bars can start an episode on.</summary>
public sealed record RlAnchorRange(DateOnly EarliestAnchor, DateOnly LatestAnchor);

/// <summary>The identity and period of the source snapshot a generation was trained on.</summary>
public sealed record RlSourceInfo(string Symbol, DateOnly PeriodStartInclusive, DateOnly PeriodEndExclusive, DateOnly FirstBar, DateOnly LastBar);
