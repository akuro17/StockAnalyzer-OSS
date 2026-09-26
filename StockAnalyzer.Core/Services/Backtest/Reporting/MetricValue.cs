using System;
using System.Text.Json.Serialization;

namespace StockAnalyzer.Core.Services.Backtest.Reporting;

/// <summary>
/// Unified output slot for every <see cref="BacktestReport"/> metric (spec §5.1). Rule DT-4: a non-Valid
/// <see cref="MetricValue"/> must never carry NaN/Infinity/0 as a stand-in <see cref="Value"/> — the
/// constructor enforces "Value is non-null iff Status == Valid" so that invariant cannot be violated by
/// any calculator, including the <see cref="MetricStatus.PositiveInfinity"/> case (ProfitFactor with a
/// zero denominator and a positive numerator): that case is mathematically infinite, not representable as
/// a finite decimal, so per DT-4 it is represented purely via <see cref="Status"/> with Value == null.
/// </summary>
public readonly record struct MetricValue
{
    public decimal? Value { get; }
    public MetricStatus Status { get; }
    public MetricUnit Unit { get; }
    public MetricReason Reason { get; }

    /// <summary>
    /// [JsonConstructor] is required here: MetricValue is a struct whose properties have no setters
    /// (by design, to keep Rule DT-4 enforceable only through this constructor) — without it,
    /// System.Text.Json falls back to the struct's implicit parameterless constructor and silently
    /// produces a zeroed-out default(MetricValue) instead of calling this one (verified empirically via
    /// BacktestReportExporterTests's export/reload round trip).
    /// </summary>
    [JsonConstructor]
    public MetricValue(decimal? value, MetricStatus status, MetricUnit unit, MetricReason reason)
    {
        bool isValid = status == MetricStatus.Valid;
        if (isValid && value is null)
        {
            throw new ArgumentException("MetricStatus.Valid requires a non-null Value.", nameof(value));
        }
        if (!isValid && value is not null)
        {
            throw new ArgumentException($"A non-Valid MetricValue ({status}) must have Value == null (Rule DT-4).", nameof(value));
        }

        Value = value;
        Status = status;
        Unit = unit;
        Reason = reason;
    }

    public static MetricValue Valid(decimal value, MetricUnit unit) =>
        new(value, MetricStatus.Valid, unit, MetricReason.None);

    public static MetricValue NonValid(MetricStatus status, MetricUnit unit, MetricReason reason)
    {
        if (status == MetricStatus.Valid)
        {
            throw new ArgumentException("Use MetricValue.Valid for MetricStatus.Valid.", nameof(status));
        }
        return new MetricValue(null, status, unit, reason);
    }
}

public enum MetricStatus
{
    Valid,
    InsufficientData,
    NotApplicable,
    Undefined,
    PositiveInfinity,
    NumericFailure
}

/// <summary>
/// ReturnRatio/DrawdownRatio/WinRateRatio are stored as raw ratios (0.08 = 8%); the ×100 percentage
/// conversion is a UI display concern applied exactly once and does not belong in this Core layer
/// (see the project's "separate UI from calculation logic" convention). PercentPoints is the one
/// deliberate exception (UlcerIndex only, per spec §5.4): its own formula already scales by 100 before
/// squaring, so its native unit is percentage points, not a ratio.
/// </summary>
public enum MetricUnit
{
    ReturnRatio,
    DrawdownRatio,
    WinRateRatio,
    Dimensionless,
    Currency,
    Bars,
    PercentPoints
}

public enum MetricReason
{
    None,
    EmptyInput,
    SingleElement,
    NoClosedTrades,
    ZeroDivisor,
    NegativeEquityInPeriod,
    ZeroPeriod,
    NegativeFinalEquity,
    RiskDataMissing,
    AllBreakeven,
    DownsideZero,
    SampleTooSmall,
    ArithmeticOverflow,
    NonFiniteResult,
    UnexpectedZeroDivisor,
    SamplingUnverified,
    SamplingRejected,
    EvaluationCoverageUnverified
}
