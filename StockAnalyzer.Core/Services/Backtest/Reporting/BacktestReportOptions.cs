using System;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Reporting;

/// <summary>
/// Report-time inputs that <see cref="Models.Backtest.Engine.BacktestResult"/> does not itself carry
/// (Frame, per-Frame annualization periods, annual risk-free/MAR rates, the warmup-bar cutoff, and the
/// evaluation-period boundary). DERIVED CONVENTION, confirmed with the user 2026-09-17 (Y:\Temp\
/// sa_implementation_plan_BacktestReportCalculator.md Gate A/B): the source spec's illustrative
/// "Generate(BacktestResult)" call cannot literally work because none of these fields exist anywhere on
/// the P1 result/configuration schema. Rather than extending the P1 sealed <c>BacktestResult</c> type
/// (larger blast radius, outside the P2 new-files-only scope), the caller supplies them here — they
/// already have all of these values from the <c>BacktestInput</c> they built to run the engine.
/// </summary>
public sealed class BacktestReportOptions
{
    public TimeFrame Frame { get; }

    private readonly int _annualPeriods;
    /// <summary>
    /// Explicit per-Frame annualization period count (spec §5.3: D1=252, W1=52). Domain [1,366], the
    /// same validation-bound style already used by <see cref="Models.Backtest.Engine.BacktestConfiguration.TradingDaysPerYear"/>.
    /// Never auto-derived from another Frame's value (e.g. W1 must not be computed as TradingDays/5).
    /// </summary>
    public int AnnualPeriods
    {
        get => _annualPeriods;
        init => _annualPeriods = value is >= 1 and <= 366
            ? value
            : throw new ArgumentOutOfRangeException(nameof(AnnualPeriods), value, "AnnualPeriods must be in [1, 366].");
    }

    private readonly decimal _annualRiskFreeRate;
    /// <summary>Simple annual rate; negative values are allowed (spec §5.3 requires &gt; -1, permitting negative values).</summary>
    public decimal AnnualRiskFreeRate
    {
        get => _annualRiskFreeRate;
        init => _annualRiskFreeRate = value > -1m
            ? value
            : throw new ArgumentOutOfRangeException(nameof(AnnualRiskFreeRate), value, "AnnualRiskFreeRate must be > -1.");
    }

    private readonly decimal _annualMar;
    /// <summary>Simple annual Minimum Acceptable Return; negative values are allowed (spec §5.3).</summary>
    public decimal AnnualMAR
    {
        get => _annualMar;
        init => _annualMar = value > -1m
            ? value
            : throw new ArgumentOutOfRangeException(nameof(AnnualMAR), value, "AnnualMAR must be > -1.");
    }

    /// <summary>
    /// Same value as the <c>BacktestInput.HistoryStartIndex</c> used to produce the <c>BacktestResult</c>
    /// being reported on. Bars before this index are excluded from every equity/return-based statistic
    /// (the current engine/reporting convention includes the bar at <c>HistoryStartIndex</c>) —
    /// <c>BacktestResult.EquityPoints</c>
    /// itself is not pre-filtered; it records every bar of the run (verified against
    /// <c>BacktestEngine.Run</c>'s bar loop, which appends an <c>EquityPoint</c> for every <c>i = 0..N-1</c>).
    /// </summary>
    public int HistoryStartIndex { get; }

    /// <summary>CAGR's period-length anchor (Gate B): <c>Y = (EndUtc.Ticks - StartUtc.Ticks) / TicksPerDay / 365.2425</c>.</summary>
    public DateTime EvaluationStartUtc { get; }

    /// <summary>See <see cref="EvaluationStartUtc"/>.</summary>
    public DateTime EvaluationEndUtc { get; }

    /// <summary>
    /// Seed for the <see cref="System.Random"/> driving <see cref="RiskAdjustedMetricsCalculator.ComputeAnnualizedSortinoAutocorrelationAdjusted"/>'s
    /// circular block bootstrap. Default 42 (user-confirmed, see Y:\Temp\
    /// sa_implementation_plan_SortinoAutocorrelationAdjustedBootstrap.md): any value is a valid seed, so no
    /// validation is applied. A seeded <see cref="System.Random"/> is documented by .NET to produce an
    /// identical sequence for an identical seed, making this metric deterministic/reproducible like every
    /// other field in this report.
    /// </summary>
    public int BootstrapSeed { get; init; } = 42;

    private readonly int _bootstrapIterations = 2000;
    /// <summary>
    /// Bootstrap resample count for the same method as <see cref="BootstrapSeed"/>. Default 2000
    /// (user-confirmed). Domain <c>[1000, int.MaxValue]</c>: below ~1000 replicates, a percentile bootstrap
    /// confidence interval is not considered reliable (Efron &amp; Tibshirani, <i>An Introduction to the
    /// Bootstrap</i>, 1993).
    /// </summary>
    public int BootstrapIterations
    {
        get => _bootstrapIterations;
        init => _bootstrapIterations = value >= 1000
            ? value
            : throw new ArgumentOutOfRangeException(nameof(BootstrapIterations), value, "BootstrapIterations must be >= 1000.");
    }

    /// <summary>
    /// Optional (owner decision G5): the run's identity frozen BEFORE the run from the very input/configuration/strategy the engine received
    /// (<see cref="BacktestRunFingerprintBuilder.Freeze"/>). The generator seals it with the result - which checks the configuration and strategy name only and
    /// cannot verify the source bars, so the owned evaluation service, not this option, is what prevents a foreign result - and
    /// reports it as <see cref="BacktestReport.RunFingerprint"/>. Null: the report carries no fingerprint item.
    /// </summary>
    public BacktestRunFingerprintBuilder? RunFingerprintBuilder { get; init; }

    public BacktestReportOptions(
        TimeFrame frame,
        int historyStartIndex,
        DateTime evaluationStartUtc,
        DateTime evaluationEndUtc)
    {
        if (!Enum.IsDefined(typeof(TimeFrame), frame))
        {
            throw new ArgumentException($"Frame value {frame} is not a defined TimeFrame.", nameof(frame));
        }
        if (historyStartIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(historyStartIndex), historyStartIndex, "HistoryStartIndex must be >= 0.");
        }
        if (evaluationStartUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("EvaluationStartUtc.Kind must be DateTimeKind.Utc.", nameof(evaluationStartUtc));
        }
        if (evaluationEndUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("EvaluationEndUtc.Kind must be DateTimeKind.Utc.", nameof(evaluationEndUtc));
        }

        Frame = frame;
        HistoryStartIndex = historyStartIndex;
        EvaluationStartUtc = evaluationStartUtc;
        EvaluationEndUtc = evaluationEndUtc;
    }

    internal void ValidateForGeneration()
    {
        if (!Enum.IsDefined(Frame))
        {
            throw new ArgumentException($"Frame value {Frame} is not a defined TimeFrame.", nameof(Frame));
        }
        if (HistoryStartIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(HistoryStartIndex), HistoryStartIndex, "HistoryStartIndex must be >= 0.");
        }
        if (AnnualPeriods is < 1 or > 366)
        {
            throw new ArgumentOutOfRangeException(nameof(AnnualPeriods), AnnualPeriods, "AnnualPeriods must be in [1, 366].");
        }
        if (AnnualRiskFreeRate <= -1m)
        {
            throw new ArgumentOutOfRangeException(nameof(AnnualRiskFreeRate), AnnualRiskFreeRate, "AnnualRiskFreeRate must be > -1.");
        }
        if (AnnualMAR <= -1m)
        {
            throw new ArgumentOutOfRangeException(nameof(AnnualMAR), AnnualMAR, "AnnualMAR must be > -1.");
        }
        if (BootstrapIterations < 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(BootstrapIterations), BootstrapIterations, "BootstrapIterations must be >= 1000.");
        }
        if (EvaluationStartUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("EvaluationStartUtc.Kind must be DateTimeKind.Utc.", nameof(EvaluationStartUtc));
        }
        if (EvaluationEndUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("EvaluationEndUtc.Kind must be DateTimeKind.Utc.", nameof(EvaluationEndUtc));
        }
    }

    internal BacktestReportOptions WithRunFingerprintBuilder(BacktestRunFingerprintBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return new BacktestReportOptions(Frame, HistoryStartIndex, EvaluationStartUtc, EvaluationEndUtc)
        {
            AnnualPeriods = AnnualPeriods,
            AnnualRiskFreeRate = AnnualRiskFreeRate,
            AnnualMAR = AnnualMAR,
            BootstrapSeed = BootstrapSeed,
            BootstrapIterations = BootstrapIterations,
            RunFingerprintBuilder = builder,
        };
    }
}
