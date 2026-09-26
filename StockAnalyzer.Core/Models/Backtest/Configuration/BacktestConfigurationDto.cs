using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;

namespace StockAnalyzer.Core.Models.Backtest.Configuration;

/// <summary>
/// One indicator selected on BacktestWindow's Tab 2 (spec §5.3/§9.6), persisted alongside the run
/// configuration (spec §5.5). Deliberately separate from the chart-display-oriented
/// <see cref="CoreIndicatorSettings"/> (color/style/overlay-panel fields do not apply to a backtest
/// indicator request) — the only fields that matter for a backtest are the ones
/// <see cref="Services.Backtest.Engine.StrategyIndicatorRequest"/> itself needs.
/// </summary>
public sealed class BacktestSelectedIndicatorDto
{
    /// <summary>Caller-chosen lookup key, forwarded verbatim to <see cref="Services.Backtest.Engine.StrategyIndicatorRequest.Key"/>.</summary>
    public string Key { get; init; } = string.Empty;

    public IndicatorType Type { get; init; }

    /// <summary>Per-indicator timeframe override (spec §9.6 "timeframe selector"); null means "same as the run's Frame".</summary>
    public TimeFrame? Frame { get; init; }

    public CoreIndicatorParameterBase? Parameters { get; init; }
}

/// <summary>
/// One side (Left or Right) of a persisted <see cref="BacktestConditionEntryDto"/> comparison — mirrors
/// <see cref="Engine.BacktestConditionSide"/>'s shape verbatim, kept as a separate DTO type (same reason
/// <see cref="BacktestSelectedIndicatorDto"/> is separate from <see cref="Engine.StrategyIndicatorRequest"/>)
/// so a future runtime-contract change to <see cref="Engine.BacktestConditionSide"/> does not silently
/// change this persisted JSON shape. See Y:\Temp\sa_implementation_plan_BacktestComparisonSignals.md
/// section 4.1/Task 4.
/// </summary>
public sealed class BacktestConditionSideDto
{
    public IndicatorType IndicatorType { get; init; }

    public CoreIndicatorParameterBase? Parameters { get; init; }

    public string OutputName { get; init; } = IndicatorResult.MainSeriesName;

    public int Offset { get; init; }

    /// <summary>Null means "same as the run's Frame" (same convention as <see cref="BacktestSelectedIndicatorDto.Frame"/>).</summary>
    public TimeFrame? Frame { get; init; }

    /// <summary>Mirrors <see cref="Engine.BacktestConditionSide.PriceSource"/> verbatim (same separate-DTO
    /// reasoning as this type's other fields). See
    /// Y:\Temp\sa_improvement_plan_BacktestPriceSourceConditionFix.md.</summary>
    public PriceType? PriceSource { get; init; }
}

/// <summary>
/// One persisted comparison condition (mirrors <see cref="Engine.BacktestConditionEntry"/> verbatim,
/// same separate-DTO reasoning as <see cref="BacktestConditionSideDto"/>). Consumed by
/// <see cref="BacktestConfigurationDto.ConditionEntries"/> (Task 4).
/// </summary>
public sealed class BacktestConditionEntryDto
{
    public BacktestConditionSideDto Left { get; init; } = new();

    public ComparisonOperator Operator { get; init; } = ComparisonOperator.GreaterThan;

    public RightHandTargetMode TargetMode { get; init; } = RightHandTargetMode.NumericValue;

    public decimal RightNumericValue { get; init; }

    /// <summary>Set when <see cref="TargetMode"/> is <see cref="RightHandTargetMode.Indicator"/>.</summary>
    public BacktestConditionSideDto? Right { get; init; }

    /// <summary>Combines this entry's result with the NEXT entry in the sequence.</summary>
    public LogicalOperator LogicalOperator { get; init; } = LogicalOperator.And;

    public BacktestConditionRole Role { get; init; } = BacktestConditionRole.Both;

    /// <summary>Mirrors <see cref="Engine.BacktestConditionEntry.Position"/> verbatim (same separate-DTO
    /// reasoning as this type's other fields). See
    /// Y:\Temp\sa_implementation_plan_BacktestPositionDirectionReversal.md section 2.1.</summary>
    public TradeSide Position { get; init; } = TradeSide.Long;
}

/// <summary>
/// Mirrors <see cref="Engine.BacktestRiskManagementSettings"/> verbatim (same separate-DTO reasoning as
/// <see cref="BacktestConditionSideDto"/>). See Y:\Temp\sa_implementation_plan_BacktestComparisonSignals.md
/// section 4.5/Task 8b.
/// </summary>
public sealed class BacktestRiskManagementSettingsDto
{
    public decimal? StopLossPercent { get; init; }

    public decimal? TakeProfitPercent { get; init; }
}

/// <summary>
/// Persisted BacktestWindow Tab-1 configuration (spec §5.5, SchemaVersion 1). Intentionally does
/// NOT repeat <c>AnnualRiskFreeRate</c>/<c>AnnualMAR</c>/<c>AnnualPeriodsByFrame</c> — those already
/// have their own dedicated persistence in <see cref="Services.Backtest.Reporting.BacktestReportDefaults"/>
/// / <see cref="Services.Backtest.Reporting.IBacktestReportSettingsManager"/> (spec §0 addendum), and
/// duplicating them here would violate the project's "no duplicate shared definitions" rule.
/// <see cref="BootstrapSeed"/>/<see cref="BootstrapIterations"/> have no such existing persistence
/// (they are plain init-defaulted properties on <see cref="Services.Backtest.Reporting.BacktestReportOptions"/>),
/// so they are owned here instead.
/// </summary>
public sealed class BacktestConfigurationDto
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Defaults to 0, not <see cref="CurrentSchemaVersion"/>, so a hand-edited or pre-versioning file
    /// with no "schemaVersion" property deserializes to 0 rather than silently matching the current
    /// version by coincidence (same convention as <see cref="Services.Backtest.Reporting.BacktestReportDefaults.SchemaVersion"/>).
    /// </summary>
    public int SchemaVersion { get; init; }

    // Group A: target data and evaluation period.
    public ExecutionModel ExecutionModel { get; init; } = ExecutionModel.Legacy;
    public string Symbol { get; init; } = string.Empty;
    public TimeFrame Frame { get; init; }
    public DateTime EvaluationStartUtc { get; init; }
    public DateTime EvaluationEndUtc { get; init; }

    // Group B: capital and cost model.
    public decimal InitialCapital { get; init; }
    public decimal CommissionFlat { get; init; }
    public decimal CommissionPerUnit { get; init; }
    public decimal SlippageRatio { get; init; }
    public PositionSizingModel SizingModel { get; init; }
    public decimal SizingParameter { get; init; }

    // Group C: margin model.
    public decimal InitialMarginRatio { get; init; }
    public decimal MaintenanceMarginRatio { get; init; }
    public decimal LiquidationPenaltyRatio { get; init; }

    // Group D (partial - see class remarks for the rest): bootstrap parameters for the
    // autocorrelation-adjusted Sortino metric (Services.Backtest.Reporting.BacktestReportOptions).
    public int BootstrapSeed { get; init; } = BacktestDefaults.BootstrapSeed;
    public int BootstrapIterations { get; init; } = BacktestDefaults.BootstrapIterations;

    // Tab 2: selected indicators.
    public List<BacktestSelectedIndicatorDto> SelectedIndicators { get; init; } = new();

    /// <summary>
    /// Task 4 safe-extension (trailing, optional, defaults to an empty list): comparison conditions
    /// driving <see cref="Engine.ConditionBasedBacktestStrategy"/> signal generation (Task 3). A
    /// pre-existing saved file with no "conditionEntries" property deserializes this as an empty list
    /// (System.Text.Json's default-value-on-missing-property behavior), identical to how no conditions
    /// were ever configured — zero behavior change for any file saved before this feature shipped.
    /// </summary>
    public List<BacktestConditionEntryDto> ConditionEntries { get; init; } = new();

    /// <summary>
    /// Task 8b safe-extension (trailing, optional, defaults to null): entry-price-relative Stop-Loss/
    /// Take-Profit settings driving <see cref="Engine.ConditionBasedBacktestStrategy"/>'s risk-management
    /// breach check. A pre-existing saved file with no "riskManagement" property deserializes this as
    /// null, identical to a run with the feature disabled — zero behavior change for any file saved
    /// before this feature shipped.
    /// </summary>
    public BacktestRiskManagementSettingsDto? RiskManagement { get; init; }
}
