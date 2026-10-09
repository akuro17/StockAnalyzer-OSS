using System;
using System.Collections.Generic;
using System.Globalization;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services.ReinforcementLearning;

namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// Bound from the "RlPortfolio" configuration section. Holds operational values only; the accepted bands live in
/// <see cref="RlPortfolioLimits"/> and are checked by <see cref="RlPortfolioSettingsValidator"/>.
/// </summary>
public sealed class RlPortfolioSettings
{
    public const string SectionName = "RlPortfolio";
    /// <summary>Default share of the symbols (percent, rounded up, at least two symbols) that is held out of training.</summary>
    public const int DefaultHoldoutPercent = 25;

    /// <summary>Largest universe a run accepts.</summary>
    public int MaxSymbols { get; set; } = RlPortfolioLimits.StandardMaximumSymbols;
    public int HoldoutPercent { get; set; } = DefaultHoldoutPercent;
    /// <summary>Largest share of the account (percent) one asset may take at a rebalance; 100 means no concentration cap.</summary>
    public int MaxWeightPercent { get; set; } = RlPortfolioLimits.MaximumWeightPercentCeiling;

    /// <summary>One block, the smallest partition: also the default minimum of every partition, so a default run behaves exactly as before.</summary>
    public const int DefaultMinimumBars = RlContract.SupportLength;

    /// <summary>Partition method of a short-history run; <see cref="RlPortfolioPartitionMode.Ratio"/> (3:1:1 of the calendar) is the default.</summary>
    public RlPortfolioPartitionMode PartitionMode { get; set; } = RlPortfolioPartitionMode.Ratio;
    /// <summary>Fewest bars the train partition holds, rounded up to whole blocks. Short-history evaluation only; Standard keeps its pinned minimum.</summary>
    public int MinimumTrainBars { get; set; } = DefaultMinimumBars;
    /// <summary>Fewest bars the validation partition holds, rounded up to whole blocks. Short-history evaluation only.</summary>
    public int MinimumValidationBars { get; set; } = DefaultMinimumBars;
    /// <summary>Fewest bars the test (out-of-sample) partition holds, rounded up to whole blocks. Short-history evaluation only.</summary>
    public int MinimumTestBars { get; set; } = DefaultMinimumBars;

    /// <summary>
    /// The settings of one run whose dialog changed the holdout share or the weight cap: a copy with the given values, or this instance when nothing is overridden.
    /// The shared (configured) settings are never modified.
    /// </summary>
    public RlPortfolioSettings WithRunOverrides(int? holdoutPercent, int? maxWeightPercent)
        => holdoutPercent is null && maxWeightPercent is null ? this : new RlPortfolioSettings
        {
            MaxSymbols = MaxSymbols, HoldoutPercent = holdoutPercent ?? HoldoutPercent, MaxWeightPercent = maxWeightPercent ?? MaxWeightPercent,
            PartitionMode = PartitionMode, MinimumTrainBars = MinimumTrainBars, MinimumValidationBars = MinimumValidationBars, MinimumTestBars = MinimumTestBars,
        };
}

/// <summary>
/// System contract of the portfolio settings (numeric classification, see SA_ARCHITECTURE_RULES). The hard maximum of the universe is not a
/// literal: it is the largest symbol count whose rollout buffer and observation frame fit the captured resource limits
/// (<see cref="RlPortfolioResources"/>), so it moves together with the rule it protects.
/// </summary>
public static class RlPortfolioLimits
{
    /// <summary>
    /// Standard upper end of the universe. Measured at PF1 (5,200 bars per symbol, see the step log): 50 symbols give a 16,104-value
    /// observation (63 KiB), a 37.5 MiB rollout buffer (29% of the 128 MiB buffer limit), 21 MiB of retained panel memory and a 1.4 s load;
    /// all three grow linearly, and the hard maximum (177 at the default limits) is where the buffer limit is reached.
    /// </summary>
    public const int StandardMaximumSymbols = 50;

    /// <summary>Share of the symbols held out of training; one percent to one half keeps at least as many trained as held-out symbols.</summary>
    public static readonly SettingBand HoldoutPercent = new(1, 50, 10, 40);

    /// <summary>One asset can never exceed the whole account (long-only, no leverage), so 100 percent is both the hard ceiling and the "no cap" default.</summary>
    public const int MaximumWeightPercentCeiling = 100;
    /// <summary>Per-asset weight cap in percent; a cap below ten percent keeps most of the account in cash for any universe this size.</summary>
    public static readonly SettingBand MaxWeightPercent = new(1, MaximumWeightPercentCeiling, 10, MaximumWeightPercentCeiling);

    /// <summary>
    /// Most blocks of one partition that mean anything: more distinct blocks than one training budget can visit once (derived, not typed).
    /// </summary>
    public const int MaximumBlocks = RlContinuousContract.TrainingTransitionsPerSeed / RlContract.EpisodeHorizon;

    /// <summary>
    /// Minimum bars of one partition: a block is the smallest unit (hard minimum), the standard band ends at what the Standard evaluation demands
    /// of a partition (<see cref="RlContract.EvaluationEpisodeCount"/> blocks), the hard maximum is <see cref="MaximumBlocks"/> blocks.
    /// </summary>
    public static readonly SettingBand MinimumBars = new(RlContract.SupportLength, MaximumBlocks * RlContract.SupportLength,
        RlContract.SupportLength, RlContract.EvaluationEpisodeCount * RlContract.SupportLength);

    public static SettingBand MaxSymbols(TrainingResourceLimits limits)
        => new(RlPortfolioContract.MinimumSymbols, RlPortfolioResources.MaximumSymbols(limits),
            RlPortfolioContract.MinimumSymbols, StandardMaximumSymbols);
}

/// <summary>
/// Startup validation of <see cref="RlPortfolioSettings"/>: a hard-limit violation throws; a value outside the standard band runs and
/// is returned as a warning for the caller with a logger.
/// </summary>
public static class RlPortfolioSettingsValidator
{
    public static IReadOnlyList<string> Validate(RlPortfolioSettings settings, TrainingResourceLimits limits)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(limits);
        var warnings = new List<string>();
        Check(warnings, nameof(settings.MaxSymbols), settings.MaxSymbols, RlPortfolioLimits.MaxSymbols(limits), "symbols");
        Check(warnings, nameof(settings.HoldoutPercent), settings.HoldoutPercent, RlPortfolioLimits.HoldoutPercent, "percent");
        Check(warnings, nameof(settings.MaxWeightPercent), settings.MaxWeightPercent, RlPortfolioLimits.MaxWeightPercent, "percent");
        if (!Enum.IsDefined(settings.PartitionMode))
            throw new ArgumentOutOfRangeException(nameof(settings.PartitionMode), settings.PartitionMode, "PartitionMode must be Ratio or BlockCount.");
        Check(warnings, nameof(settings.MinimumTrainBars), settings.MinimumTrainBars, RlPortfolioLimits.MinimumBars, "bars");
        Check(warnings, nameof(settings.MinimumValidationBars), settings.MinimumValidationBars, RlPortfolioLimits.MinimumBars, "bars");
        Check(warnings, nameof(settings.MinimumTestBars), settings.MinimumTestBars, RlPortfolioLimits.MinimumBars, "bars");
        return warnings;
    }

    private static void Check(List<string> warnings, string name, long value, SettingBand band, string unit)
    {
        if (!band.IsWithinHardLimits(value))
            throw new ArgumentOutOfRangeException(name, value,
                $"{name} must be {Format(band.HardMin)} to {Format(band.HardMax)} {unit}; the value is outside the system limits.");
        if (!band.IsWithinStandard(value))
            warnings.Add($"{RlPortfolioSettings.SectionName}:{name} = {Format(value)} {unit} is outside the standard range "
                + $"{Format(band.StandardMin)} to {Format(band.StandardMax)}; the run proceeds but memory use or evaluation quality may suffer.");
    }

    private static string Format(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
