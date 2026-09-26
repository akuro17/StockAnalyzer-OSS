using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services.Backtest.Reporting;

/// <summary>
/// Persistable default values for the <see cref="BacktestReportOptions"/> fields that have no
/// natural source elsewhere (spec §5.3's own named examples: D1=252, W1=52). Plain, directly
/// JSON-serializable shape (no separate persistence-DTO indirection needed, unlike e.g.
/// NotesSettingsManager, since every field here already matches its on-disk representation 1:1).
/// </summary>
public sealed class BacktestReportDefaults
{
    /// <summary>The schema version this type currently writes/expects. Bump when a field is added, removed, or reinterpreted.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Defaults to 0 (not <see cref="CurrentSchemaVersion"/>) so a pre-versioning on-disk file, whose
    /// JSON has no "schemaVersion" property at all, deserializes to 0 rather than silently matching the
    /// current version by coincidence. <see cref="BacktestReportSettingsManager.LoadAsync"/> treats any
    /// value other than <see cref="CurrentSchemaVersion"/> (0 included) as unrecognized.
    /// </summary>
    public int SchemaVersion { get; init; }

    public decimal AnnualRiskFreeRate { get; init; }
    public decimal AnnualMAR { get; init; }
    public Dictionary<TimeFrame, int> AnnualPeriodsByFrame { get; init; } = new();

    public static BacktestReportDefaults BuiltIn => new()
    {
        SchemaVersion = CurrentSchemaVersion,
        AnnualRiskFreeRate = 0m,
        AnnualMAR = 0m,
        AnnualPeriodsByFrame = new Dictionary<TimeFrame, int>
        {
            [TimeFrame.D1] = 252,
            [TimeFrame.W1] = 52,
            [TimeFrame.MN1] = 12,
        },
    };

    public static BacktestReportDefaults SnapshotValidated(BacktestReportDefaults source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.SchemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentException($"SchemaVersion must equal {CurrentSchemaVersion}.", nameof(source));
        }
        if (source.AnnualRiskFreeRate <= -1m)
        {
            throw new ArgumentOutOfRangeException(nameof(source), source.AnnualRiskFreeRate, "AnnualRiskFreeRate must be > -1.");
        }
        if (source.AnnualMAR <= -1m)
        {
            throw new ArgumentOutOfRangeException(nameof(source), source.AnnualMAR, "AnnualMAR must be > -1.");
        }
        if (source.AnnualPeriodsByFrame is null)
        {
            throw new ArgumentException("AnnualPeriodsByFrame must not be null.", nameof(source));
        }

        var periods = new Dictionary<TimeFrame, int>(source.AnnualPeriodsByFrame.Count);
        foreach (KeyValuePair<TimeFrame, int> entry in source.AnnualPeriodsByFrame)
        {
            if (!Enum.IsDefined(typeof(TimeFrame), entry.Key))
            {
                throw new ArgumentException($"AnnualPeriodsByFrame contains undefined TimeFrame '{entry.Key}'.", nameof(source));
            }
            if (entry.Value is < 1 or > 366)
            {
                throw new ArgumentOutOfRangeException(nameof(source), entry.Value, $"AnnualPeriodsByFrame[{entry.Key}] must be in [1, 366].");
            }
            periods.Add(entry.Key, entry.Value);
        }

        return new BacktestReportDefaults
        {
            SchemaVersion = CurrentSchemaVersion,
            AnnualRiskFreeRate = source.AnnualRiskFreeRate,
            AnnualMAR = source.AnnualMAR,
            AnnualPeriodsByFrame = periods,
        };
    }
}
