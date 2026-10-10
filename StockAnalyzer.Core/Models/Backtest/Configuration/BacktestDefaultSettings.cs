using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;

namespace StockAnalyzer.Core.Models.Backtest.Configuration;

/// <summary>
/// User-defined default values (Settings > Backtest) for the Backtest window's Configuration groups
/// A (target data and period), B (capital and cost model), C (margin model) and D (report aggregation).
/// Persisted separately from the window's own saved configuration and report settings so that neither
/// Save Settings nor this Settings page can overwrite the other.
/// Validation delegates to the existing domain validators; no bound is restated here.
/// </summary>
public sealed class BacktestDefaultSettings
{
    /// <summary>Bump when a field is added, removed, or reinterpreted.</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>Defaults to 0 so a file without "schemaVersion" is treated as unrecognized (same convention as <see cref="BacktestReportDefaults"/>).</summary>
    public int SchemaVersion { get; init; }

    // Group A: Target Data and Period defaults
    public TimeFrame DefaultTimeFrame { get; init; } = TimeFrame.D1;
    public BacktestEndDateMode EndDateMode { get; init; } = BacktestEndDateMode.Today;
    public DateTime? FixedEndDateUtc { get; init; }
    public BacktestStartDateMode StartDateMode { get; init; } = BacktestStartDateMode.RelativeYears;
    public int StartDateRelativeYears { get; init; } = 1;
    public DateTime? FixedStartDateUtc { get; init; }

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

    // Group D: report aggregation.
    public decimal AnnualRiskFreeRate { get; init; }
    public decimal AnnualMAR { get; init; }
    public Dictionary<TimeFrame, int> AnnualPeriodsByFrame { get; init; } = new();
    public int BootstrapSeed { get; init; }
    public int BootstrapIterations { get; init; }

    /// <summary>Factory defaults: TimeFrame.D1, Today, 1 year lookback, <see cref="BacktestDefaults"/> for B/C/bootstrap, <see cref="BacktestReportDefaults.BuiltIn"/> for report values.</summary>
    public static BacktestDefaultSettings BuiltIn
    {
        get
        {
            BacktestReportDefaults report = BacktestReportDefaults.BuiltIn;
            return new BacktestDefaultSettings
            {
                SchemaVersion = CurrentSchemaVersion,
                DefaultTimeFrame = TimeFrame.D1,
                EndDateMode = BacktestEndDateMode.Today,
                FixedEndDateUtc = null,
                StartDateMode = BacktestStartDateMode.RelativeYears,
                StartDateRelativeYears = 1,
                FixedStartDateUtc = null,
                InitialCapital = BacktestDefaults.InitialCapital,
                CommissionFlat = BacktestDefaults.CommissionFlat,
                CommissionPerUnit = BacktestDefaults.CommissionPerUnit,
                SlippageRatio = BacktestDefaults.SlippageRatio,
                SizingModel = BacktestDefaults.SizingModel,
                SizingParameter = BacktestDefaults.SizingParameter,
                InitialMarginRatio = BacktestDefaults.InitialMarginRatio,
                MaintenanceMarginRatio = BacktestDefaults.MaintenanceMarginRatio,
                LiquidationPenaltyRatio = BacktestDefaults.LiquidationPenaltyRatio,
                AnnualRiskFreeRate = report.AnnualRiskFreeRate,
                AnnualMAR = report.AnnualMAR,
                AnnualPeriodsByFrame = report.AnnualPeriodsByFrame,
                BootstrapSeed = BacktestDefaults.BootstrapSeed,
                BootstrapIterations = BacktestDefaults.BootstrapIterations,
            };
        }
    }

    /// <summary>
    /// Resolves canonical evaluation window and timeframe according to Group A default settings.
    /// Strictly enforces UTC midnight normalization and chronological invariants.
    /// </summary>
    public (DateTime StartUtc, DateTime EndUtc, TimeFrame Frame) ResolveTargetDataAndPeriod(DateTime? nowUtc = null)
    {
        DateTime referenceDate = (nowUtc ?? DateTime.UtcNow).Date;
        if (referenceDate.Kind != DateTimeKind.Utc)
        {
            referenceDate = DateTime.SpecifyKind(referenceDate, DateTimeKind.Utc);
        }

        DateTime endUtc = EndDateMode switch
        {
            BacktestEndDateMode.FixedDate when FixedEndDateUtc.HasValue =>
                DateTime.SpecifyKind(FixedEndDateUtc.Value.Date, DateTimeKind.Utc),
            _ => referenceDate
        };

        DateTime startUtc;
        if (StartDateMode == BacktestStartDateMode.FixedDate && FixedStartDateUtc.HasValue)
        {
            DateTime candidate = DateTime.SpecifyKind(FixedStartDateUtc.Value.Date, DateTimeKind.Utc);
            startUtc = candidate < endUtc ? candidate : endUtc.AddYears(-1);
        }
        else
        {
            int lookbackYears = Math.Clamp(StartDateRelativeYears, 1, 50);
            startUtc = endUtc.AddYears(-lookbackYears);
        }

        TimeFrame frame = Enum.IsDefined(DefaultTimeFrame) ? DefaultTimeFrame : TimeFrame.D1;
        return (startUtc, endUtc, frame);
    }

    /// <summary>Annualization periods of <paramref name="frame"/>; 0 when the timeframe has no entry.</summary>
    public int AnnualPeriodsOf(TimeFrame frame) => AnnualPeriodsByFrame.TryGetValue(frame, out int periods) ? periods : 0;

    /// <summary>Whether every setting (schema version excluded) equals that of <paramref name="other"/>; the single place that enumerates the settings for comparison.</summary>
    public bool ValueEquals(BacktestDefaultSettings other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (AnnualPeriodsByFrame.Count != other.AnnualPeriodsByFrame.Count)
        {
            return false;
        }

        foreach (KeyValuePair<TimeFrame, int> entry in AnnualPeriodsByFrame)
        {
            if (!other.AnnualPeriodsByFrame.TryGetValue(entry.Key, out int periods) || periods != entry.Value)
            {
                return false;
            }
        }

        return DefaultTimeFrame == other.DefaultTimeFrame
            && EndDateMode == other.EndDateMode
            && Nullable.Equals(FixedEndDateUtc, other.FixedEndDateUtc)
            && StartDateMode == other.StartDateMode
            && StartDateRelativeYears == other.StartDateRelativeYears
            && Nullable.Equals(FixedStartDateUtc, other.FixedStartDateUtc)
            && InitialCapital == other.InitialCapital
            && CommissionFlat == other.CommissionFlat
            && CommissionPerUnit == other.CommissionPerUnit
            && SlippageRatio == other.SlippageRatio
            && SizingModel == other.SizingModel
            && SizingParameter == other.SizingParameter
            && InitialMarginRatio == other.InitialMarginRatio
            && MaintenanceMarginRatio == other.MaintenanceMarginRatio
            && LiquidationPenaltyRatio == other.LiquidationPenaltyRatio
            && AnnualRiskFreeRate == other.AnnualRiskFreeRate
            && AnnualMAR == other.AnnualMAR
            && BootstrapSeed == other.BootstrapSeed
            && BootstrapIterations == other.BootstrapIterations;
    }

    /// <summary>The report-defaults view of this object (the shape the Backtest window's report settings use).</summary>
    public BacktestReportDefaults ToReportDefaults() => BacktestReportDefaults.SnapshotValidated(new BacktestReportDefaults
    {
        SchemaVersion = BacktestReportDefaults.CurrentSchemaVersion,
        AnnualRiskFreeRate = AnnualRiskFreeRate,
        AnnualMAR = AnnualMAR,
        AnnualPeriodsByFrame = AnnualPeriodsByFrame,
    });

    /// <summary>Key of the single annualization-periods item in <see cref="FindErrors"/> (it spans several timeframes).</summary>
    public const string AnnualPeriodsErrorKey = nameof(AnnualPeriodsByFrame);

    /// <summary>
    /// Reports every rejected value at once, keyed by the property name (<see cref="AnnualPeriodsErrorKey"/> for the periods),
    /// so a UI can show each reason next to its own item. Uses the same domain validators as
    /// <see cref="SnapshotValidated"/> (each field checked in isolation, no bound restated); an empty result means
    /// <see cref="SnapshotValidated"/> succeeds. A cross-field error (maintenance vs initial margin) is reported on
    /// <see cref="MaintenanceMarginRatio"/> only when both margin values are individually valid.
    /// </summary>
    public static IReadOnlyDictionary<string, string> FindErrors(BacktestDefaultSettings source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var errors = new Dictionary<string, string>();

        void Check(string key, Action validate)
        {
            try
            {
                validate();
            }
            catch (ArgumentException ex)
            {
                errors.TryAdd(key, ex.Message);
            }
        }

        if (!Enum.IsDefined(source.DefaultTimeFrame))
        {
            errors.TryAdd(nameof(DefaultTimeFrame), "DefaultTimeFrame is not a valid TimeFrame value.");
        }

        if (source.EndDateMode == BacktestEndDateMode.FixedDate && !source.FixedEndDateUtc.HasValue)
        {
            errors.TryAdd(nameof(FixedEndDateUtc), "Fixed end date is required when EndDateMode is FixedDate.");
        }

        if (source.StartDateMode == BacktestStartDateMode.RelativeYears)
        {
            if (source.StartDateRelativeYears < 1 || source.StartDateRelativeYears > 50)
            {
                errors.TryAdd(nameof(StartDateRelativeYears), "StartDateRelativeYears must be between 1 and 50.");
            }
        }
        else if (source.StartDateMode == BacktestStartDateMode.FixedDate)
        {
            if (!source.FixedStartDateUtc.HasValue)
            {
                errors.TryAdd(nameof(FixedStartDateUtc), "Fixed start date is required when StartDateMode is FixedDate.");
            }
        }

        if (source.EndDateMode == BacktestEndDateMode.FixedDate
            && source.StartDateMode == BacktestStartDateMode.FixedDate
            && source.FixedEndDateUtc.HasValue
            && source.FixedStartDateUtc.HasValue
            && source.FixedStartDateUtc.Value.Date >= source.FixedEndDateUtc.Value.Date)
        {
            errors.TryAdd(nameof(FixedStartDateUtc), "Start date must precede end date.");
        }

        Check(nameof(InitialCapital), () => _ = new BacktestConfiguration { InitialCapital = source.InitialCapital });
        Check(nameof(CommissionFlat), () => _ = new BacktestConfiguration { CommissionFlat = source.CommissionFlat });
        Check(nameof(CommissionPerUnit), () => _ = new BacktestConfiguration { CommissionPerUnit = source.CommissionPerUnit });
        Check(nameof(SlippageRatio), () => _ = new BacktestConfiguration { SlippageRatio = source.SlippageRatio });
        Check(nameof(InitialMarginRatio), () => _ = new BacktestConfiguration { InitialMarginRatio = source.InitialMarginRatio });
        Check(nameof(MaintenanceMarginRatio), () => _ = new BacktestConfiguration { MaintenanceMarginRatio = source.MaintenanceMarginRatio });
        Check(nameof(LiquidationPenaltyRatio), () => _ = new BacktestConfiguration { LiquidationPenaltyRatio = source.LiquidationPenaltyRatio });

        // Sizing: every other field held at its factory value so only the sizing pair can fail.
        Check(nameof(SizingParameter), () => new BacktestConfiguration
        {
            InitialCapital = BacktestDefaults.InitialCapital,
            SizingModel = source.SizingModel,
            SizingParameter = source.SizingParameter,
        }.Validate());

        if (!errors.ContainsKey(nameof(InitialMarginRatio)) && !errors.ContainsKey(nameof(MaintenanceMarginRatio)))
        {
            Check(nameof(MaintenanceMarginRatio), () => new BacktestConfiguration
            {
                InitialCapital = BacktestDefaults.InitialCapital,
                SizingModel = BacktestDefaults.SizingModel,
                SizingParameter = BacktestDefaults.SizingParameter,
                InitialMarginRatio = source.InitialMarginRatio,
                MaintenanceMarginRatio = source.MaintenanceMarginRatio,
            }.Validate());
        }

        DateTime placeholderUtc = DateTime.UtcNow;
        Check(nameof(BootstrapIterations), () => _ = new BacktestReportOptions(TimeFrame.D1, 0, placeholderUtc, placeholderUtc)
        {
            BootstrapIterations = source.BootstrapIterations,
        });

        BacktestReportDefaults builtIn = BacktestReportDefaults.BuiltIn;
        Check(nameof(AnnualRiskFreeRate), () => BacktestReportDefaults.SnapshotValidated(new BacktestReportDefaults
        {
            SchemaVersion = BacktestReportDefaults.CurrentSchemaVersion,
            AnnualRiskFreeRate = source.AnnualRiskFreeRate,
            AnnualPeriodsByFrame = builtIn.AnnualPeriodsByFrame,
        }));
        Check(nameof(AnnualMAR), () => BacktestReportDefaults.SnapshotValidated(new BacktestReportDefaults
        {
            SchemaVersion = BacktestReportDefaults.CurrentSchemaVersion,
            AnnualMAR = source.AnnualMAR,
            AnnualPeriodsByFrame = builtIn.AnnualPeriodsByFrame,
        }));
        Check(AnnualPeriodsErrorKey, () => BacktestReportDefaults.SnapshotValidated(new BacktestReportDefaults
        {
            SchemaVersion = BacktestReportDefaults.CurrentSchemaVersion,
            AnnualPeriodsByFrame = source.AnnualPeriodsByFrame,
        }));

        return errors;
    }

    /// <summary>
    /// Returns an independent, validated copy. Throws <see cref="ArgumentException"/> (including
    /// <see cref="ArgumentOutOfRangeException"/>) when any value is rejected by
    /// <see cref="BacktestConfiguration"/>, <see cref="BacktestReportOptions"/> or <see cref="BacktestReportDefaults"/>.
    /// </summary>
    public static BacktestDefaultSettings SnapshotValidated(BacktestDefaultSettings source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.SchemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentException($"SchemaVersion must equal {CurrentSchemaVersion}.", nameof(source));
        }

        if (!Enum.IsDefined(source.DefaultTimeFrame))
        {
            throw new ArgumentOutOfRangeException(nameof(source.DefaultTimeFrame), "DefaultTimeFrame is not a valid TimeFrame value.");
        }

        if (source.EndDateMode == BacktestEndDateMode.FixedDate && !source.FixedEndDateUtc.HasValue)
        {
            throw new ArgumentException("FixedEndDateUtc is required when EndDateMode is FixedDate.", nameof(source.FixedEndDateUtc));
        }

        if (source.StartDateMode == BacktestStartDateMode.RelativeYears)
        {
            if (source.StartDateRelativeYears < 1 || source.StartDateRelativeYears > 50)
            {
                throw new ArgumentOutOfRangeException(nameof(source.StartDateRelativeYears), "StartDateRelativeYears must be between 1 and 50.");
            }
        }
        else if (source.StartDateMode == BacktestStartDateMode.FixedDate)
        {
            if (!source.FixedStartDateUtc.HasValue)
            {
                throw new ArgumentException("FixedStartDateUtc is required when StartDateMode is FixedDate.", nameof(source.FixedStartDateUtc));
            }
        }

        if (source.EndDateMode == BacktestEndDateMode.FixedDate
            && source.StartDateMode == BacktestStartDateMode.FixedDate
            && source.FixedEndDateUtc.HasValue
            && source.FixedStartDateUtc.HasValue
            && source.FixedStartDateUtc.Value.Date >= source.FixedEndDateUtc.Value.Date)
        {
            throw new ArgumentException("FixedStartDateUtc must precede FixedEndDateUtc.", nameof(source.FixedStartDateUtc));
        }

        new BacktestConfiguration
        {
            InitialCapital = source.InitialCapital,
            CommissionFlat = source.CommissionFlat,
            CommissionPerUnit = source.CommissionPerUnit,
            SlippageRatio = source.SlippageRatio,
            SizingModel = source.SizingModel,
            SizingParameter = source.SizingParameter,
            InitialMarginRatio = source.InitialMarginRatio,
            MaintenanceMarginRatio = source.MaintenanceMarginRatio,
            LiquidationPenaltyRatio = source.LiquidationPenaltyRatio,
        }.Validate();

        // Bootstrap fields are validated by BacktestReportOptions' own init accessors; the constructor arguments
        // are placeholders that satisfy its unrelated (frame/history/period) checks and are never used.
        DateTime placeholderUtc = DateTime.UtcNow;
        _ = new BacktestReportOptions(TimeFrame.D1, 0, placeholderUtc, placeholderUtc)
        {
            BootstrapSeed = source.BootstrapSeed,
            BootstrapIterations = source.BootstrapIterations,
        };

        BacktestReportDefaults report = BacktestReportDefaults.SnapshotValidated(new BacktestReportDefaults
        {
            SchemaVersion = BacktestReportDefaults.CurrentSchemaVersion,
            AnnualRiskFreeRate = source.AnnualRiskFreeRate,
            AnnualMAR = source.AnnualMAR,
            AnnualPeriodsByFrame = source.AnnualPeriodsByFrame,
        });

        DateTime? fixedEndUtc = source.FixedEndDateUtc.HasValue
            ? DateTime.SpecifyKind(source.FixedEndDateUtc.Value.Date, DateTimeKind.Utc)
            : null;
        DateTime? fixedStartUtc = source.FixedStartDateUtc.HasValue
            ? DateTime.SpecifyKind(source.FixedStartDateUtc.Value.Date, DateTimeKind.Utc)
            : null;

        return new BacktestDefaultSettings
        {
            SchemaVersion = CurrentSchemaVersion,
            DefaultTimeFrame = source.DefaultTimeFrame,
            EndDateMode = source.EndDateMode,
            FixedEndDateUtc = fixedEndUtc,
            StartDateMode = source.StartDateMode,
            StartDateRelativeYears = source.StartDateRelativeYears,
            FixedStartDateUtc = fixedStartUtc,
            InitialCapital = source.InitialCapital,
            CommissionFlat = source.CommissionFlat,
            CommissionPerUnit = source.CommissionPerUnit,
            SlippageRatio = source.SlippageRatio,
            SizingModel = source.SizingModel,
            SizingParameter = source.SizingParameter,
            InitialMarginRatio = source.InitialMarginRatio,
            MaintenanceMarginRatio = source.MaintenanceMarginRatio,
            LiquidationPenaltyRatio = source.LiquidationPenaltyRatio,
            AnnualRiskFreeRate = report.AnnualRiskFreeRate,
            AnnualMAR = report.AnnualMAR,
            AnnualPeriodsByFrame = report.AnnualPeriodsByFrame,
            BootstrapSeed = source.BootstrapSeed,
            BootstrapIterations = source.BootstrapIterations,
        };
    }
}
