using System;
using System.Text.Json.Serialization;

namespace StockAnalyzer.Core.Models.Backtest.Engine;

/// <summary>Shared defaults for Legacy and Strict backtest execution models.</summary>
public static class BacktestDefaults
{
    public const decimal InitialMarginRatio = 0.30m;
    public const decimal MaintenanceMarginRatio = 0.20m;

    // Factory defaults for Configuration groups B (capital/cost), C (margin) and the bootstrap part of D.
    // Single source for the Backtest window's built-in defaults and Settings > Backtest's "reset to factory".
    public const decimal InitialCapital = 1_000_000m;
    public const decimal CommissionFlat = 0m;
    public const decimal CommissionPerUnit = 0m;
    public const decimal SlippageRatio = 0m;
    public const PositionSizingModel SizingModel = PositionSizingModel.FixedQuantity;
    public const decimal SizingParameter = 1m;

    /// <summary>0.5%: a forced liquidation executes as a market order under adverse conditions, so a 0 default would model an unrealistically frictionless margin call.</summary>
    public const decimal LiquidationPenaltyRatio = 0.005m;
    public const int BootstrapSeed = 42;
    public const int BootstrapIterations = 2000;
}

/// <summary>
/// Representable decimal bounds for ratio fields shared by domain validation and configuration UIs.
/// The positive minimum and below-unit maximum preserve the domain's open intervals without
/// excluding any value representable by <see cref="decimal"/>.
/// </summary>
public static class BacktestConfigurationBounds
{
    public const decimal InclusiveRatioMinimum = 0m;
    public const decimal PositiveRatioMinimum = 0.0000000000000000000000000001m;
    public const decimal InclusiveRatioMaximum = 1m;
    public const decimal BelowUnitRatioMaximum = 0.9999999999999999999999999999m;
}

/// <summary>
/// Immutable backtest run configuration. Margin fields (InitialMarginRatio, MaintenanceMarginRatio,
/// LiquidationPenaltyRatio) implement the in-session amendment recorded in
/// Y:\Temp\sa_ai_context_BacktestEngine_P1.md section 1.5.1 — they replace the original spec's
/// full-cash-outlay accounting with a shared Long/Short margin-account model.
/// Single-property bounds are enforced immediately in each init accessor. Cross-property bounds
/// (margin ratio ordering, sizing-parameter-vs-model consistency) cannot be safely checked in an
/// individual init accessor because object-initializer assignment order is not guaranteed to match
/// declaration order — call <see cref="Validate"/> once construction is complete (BacktestInput's
/// validation pipeline does this).
/// </summary>
public sealed class BacktestConfiguration
{
    private const string InitialCapitalBoundMessage = "InitialCapital must be > 0.";

    private readonly decimal _initialCapital;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ExecutionModel ExecutionModel { get; init; } = ExecutionModel.Legacy;

    public decimal InitialCapital
    {
        get => _initialCapital;
        init => _initialCapital = value > 0m
            ? value
            : throw new ArgumentOutOfRangeException(nameof(InitialCapital), value, InitialCapitalBoundMessage);
    }

    private readonly decimal _commissionFlat;
    public decimal CommissionFlat
    {
        get => _commissionFlat;
        init => _commissionFlat = value >= 0m
            ? value
            : throw new ArgumentOutOfRangeException(nameof(CommissionFlat), value, "CommissionFlat must be >= 0.");
    }

    private readonly decimal _commissionPerUnit;
    public decimal CommissionPerUnit
    {
        get => _commissionPerUnit;
        init => _commissionPerUnit = value >= 0m
            ? value
            : throw new ArgumentOutOfRangeException(nameof(CommissionPerUnit), value, "CommissionPerUnit must be >= 0.");
    }

    private readonly decimal _slippageRatio;
    public decimal SlippageRatio
    {
        get => _slippageRatio;
        init => _slippageRatio = value >= BacktestConfigurationBounds.InclusiveRatioMinimum
            && value <= BacktestConfigurationBounds.BelowUnitRatioMaximum
            ? value
            : throw new ArgumentOutOfRangeException(nameof(SlippageRatio), value, "SlippageRatio must satisfy 0 <= s < 1.");
    }

    private readonly int _tradingDaysPerYear = 252;
    public int TradingDaysPerYear
    {
        get => _tradingDaysPerYear;
        init => _tradingDaysPerYear = value is >= 1 and <= 365
            ? value
            : throw new ArgumentOutOfRangeException(nameof(TradingDaysPerYear), value, "TradingDaysPerYear must be in [1, 365].");
    }

    public PositionSizingModel SizingModel { get; init; }

    public decimal SizingParameter { get; init; }

    private readonly decimal _initialMarginRatio = BacktestDefaults.InitialMarginRatio;
    public decimal InitialMarginRatio
    {
        get => _initialMarginRatio;
        init => _initialMarginRatio = value >= BacktestConfigurationBounds.PositiveRatioMinimum
            && value <= BacktestConfigurationBounds.InclusiveRatioMaximum
            ? value
            : throw new ArgumentOutOfRangeException(nameof(InitialMarginRatio), value, "InitialMarginRatio must satisfy 0 < r <= 1.");
    }

    private readonly decimal _maintenanceMarginRatio = BacktestDefaults.MaintenanceMarginRatio;
    public decimal MaintenanceMarginRatio
    {
        get => _maintenanceMarginRatio;
        init => _maintenanceMarginRatio = value >= BacktestConfigurationBounds.PositiveRatioMinimum
            ? value
            : throw new ArgumentOutOfRangeException(nameof(MaintenanceMarginRatio), value, "MaintenanceMarginRatio must be > 0.");
    }

    /// <summary>
    /// Default of 0.005 (0.5%) reflects that a forced liquidation always executes as a market
    /// order under adverse (liquidity-exhaustion) conditions, so a 0 default would silently model
    /// an unrealistically frictionless margin call. User-confirmed amendment recorded in
    /// Y:\Temp\sa_ai_context_BacktestEngine_P1.md section 1.5.1.
    /// </summary>
    private readonly decimal _liquidationPenaltyRatio = BacktestDefaults.LiquidationPenaltyRatio;
    public decimal LiquidationPenaltyRatio
    {
        get => _liquidationPenaltyRatio;
        init => _liquidationPenaltyRatio = value >= BacktestConfigurationBounds.InclusiveRatioMinimum
            && value <= BacktestConfigurationBounds.BelowUnitRatioMaximum
            ? value
            : throw new ArgumentOutOfRangeException(nameof(LiquidationPenaltyRatio), value, "LiquidationPenaltyRatio must satisfy 0 <= p < 1.");
    }

    private readonly decimal _shortBorrowAnnualRate;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public decimal ShortBorrowAnnualRate
    {
        get => _shortBorrowAnnualRate;
        init => _shortBorrowAnnualRate = value >= BacktestConfigurationBounds.InclusiveRatioMinimum
            && value <= BacktestConfigurationBounds.InclusiveRatioMaximum
            ? value
            : throw new ArgumentOutOfRangeException(nameof(ShortBorrowAnnualRate), value, "ShortBorrowAnnualRate must satisfy 0 <= r <= 1.");
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public VolumeConstraintMode VolumeConstraintMode { get; init; } = VolumeConstraintMode.RejectIfExceeds;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public CorporateActionHandlingMode CorporateActionHandlingMode { get; init; } = CorporateActionHandlingMode.WarnOnly;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public BorrowFeeBasisMode BorrowFeeBasisMode { get; init; } = BorrowFeeBasisMode.EntryPriceFixed;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PricePathConflictMode PricePathConflictMode { get; init; } = PricePathConflictMode.Conservative;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public MarginLiquidationDeficitMode MarginLiquidationDeficitMode { get; init; } = MarginLiquidationDeficitMode.CapAtZero;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PriceLimitFillMode PriceLimitFillMode { get; init; } = PriceLimitFillMode.AlwaysFill;

    private readonly decimal? _maxVolumeParticipationRatio;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public decimal? MaxVolumeParticipationRatio
    {
        get => _maxVolumeParticipationRatio;
        init => _maxVolumeParticipationRatio = value is null || (value > BacktestConfigurationBounds.InclusiveRatioMinimum && value <= BacktestConfigurationBounds.InclusiveRatioMaximum)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(MaxVolumeParticipationRatio), value, "MaxVolumeParticipationRatio must be in (0, 1].");
    }

    [JsonIgnore]
    public IntrabarPathMode IntrabarPathMode { get; init; } = IntrabarPathMode.DeterministicCandleShape;

    /// <summary>
    /// Cross-property checks that require both sides to already hold their final value.
    /// Must be called after construction (BacktestInput's validation pipeline calls this).
    /// </summary>
    public void Validate()
    {
        if (!Enum.IsDefined(ExecutionModel))
        {
            throw new ArgumentOutOfRangeException(nameof(ExecutionModel), ExecutionModel, "Unknown ExecutionModel.");
        }

        if (!Enum.IsDefined(VolumeConstraintMode))
        {
            throw new ArgumentOutOfRangeException(nameof(VolumeConstraintMode), VolumeConstraintMode, "Unknown VolumeConstraintMode.");
        }

        if (!Enum.IsDefined(CorporateActionHandlingMode))
        {
            throw new ArgumentOutOfRangeException(nameof(CorporateActionHandlingMode), CorporateActionHandlingMode, "Unknown CorporateActionHandlingMode.");
        }

        if (!Enum.IsDefined(BorrowFeeBasisMode))
        {
            throw new ArgumentOutOfRangeException(nameof(BorrowFeeBasisMode), BorrowFeeBasisMode, "Unknown BorrowFeeBasisMode.");
        }

        if (!Enum.IsDefined(PricePathConflictMode))
        {
            throw new ArgumentOutOfRangeException(nameof(PricePathConflictMode), PricePathConflictMode, "Unknown PricePathConflictMode.");
        }

        if (!Enum.IsDefined(MarginLiquidationDeficitMode))
        {
            throw new ArgumentOutOfRangeException(nameof(MarginLiquidationDeficitMode), MarginLiquidationDeficitMode, "Unknown MarginLiquidationDeficitMode.");
        }

        if (!Enum.IsDefined(PriceLimitFillMode))
        {
            throw new ArgumentOutOfRangeException(nameof(PriceLimitFillMode), PriceLimitFillMode, "Unknown PriceLimitFillMode.");
        }

        if (!Enum.IsDefined(IntrabarPathMode))
        {
            throw new ArgumentOutOfRangeException(nameof(IntrabarPathMode), IntrabarPathMode, "Unknown IntrabarPathMode.");
        }

        if (MaxVolumeParticipationRatio is { } ratio && (ratio <= BacktestConfigurationBounds.InclusiveRatioMinimum || ratio > BacktestConfigurationBounds.InclusiveRatioMaximum))
        {
            throw new ArgumentOutOfRangeException(nameof(MaxVolumeParticipationRatio), ratio, "MaxVolumeParticipationRatio must be in (0, 1].");
        }

        if (ShortBorrowAnnualRate is < BacktestConfigurationBounds.InclusiveRatioMinimum or > BacktestConfigurationBounds.InclusiveRatioMaximum)
        {
            throw new ArgumentOutOfRangeException(nameof(ShortBorrowAnnualRate), ShortBorrowAnnualRate, "ShortBorrowAnnualRate must satisfy 0 <= r <= 1.");
        }

        if (MaintenanceMarginRatio >= InitialMarginRatio)
        {
            throw new ArgumentOutOfRangeException(nameof(MaintenanceMarginRatio), MaintenanceMarginRatio,
                "MaintenanceMarginRatio must be strictly less than InitialMarginRatio.");
        }

        switch (SizingModel)
        {
            case PositionSizingModel.FixedQuantity:
                if (SizingParameter < 1m || SizingParameter != Math.Truncate(SizingParameter))
                {
                    throw new ArgumentOutOfRangeException(nameof(SizingParameter), SizingParameter,
                        "FixedQuantity SizingParameter must be an integer >= 1.");
                }
                break;
            case PositionSizingModel.PercentOfEquity:
                if (SizingParameter <= 0m || SizingParameter > 1m)
                {
                    throw new ArgumentOutOfRangeException(nameof(SizingParameter), SizingParameter,
                        "PercentOfEquity SizingParameter must satisfy 0 < P <= 1.");
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(SizingModel), SizingModel, "Unknown PositionSizingModel.");
        }

        // The init accessor only runs when InitialCapital is assigned, so an omitted (default 0) value bypasses it.
        // Checked last so every previously-validated failure keeps its exception and order.
        if (InitialCapital <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(InitialCapital), InitialCapital, InitialCapitalBoundMessage);
        }
    }
}
