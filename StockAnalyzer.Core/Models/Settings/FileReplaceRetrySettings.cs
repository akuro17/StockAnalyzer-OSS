using System;
using System.Collections.Generic;
using System.Globalization;

namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// Bound from the "FileReplaceRetry" configuration section. Windows briefly denies renaming a file or directory that a scanner or
/// watcher has just opened; the operation is retried a bounded number of times. Holds operational values only: the accepted bands
/// live in <see cref="FileReplaceRetryLimits"/> and are checked by <see cref="FileReplaceRetrySettingsValidator"/>. The Python
/// trainer reads the same policy from SA_CHECKPOINT_REPLACE_ATTEMPTS and SA_CHECKPOINT_REPLACE_DELAY_MS (checkpoints.py).
/// </summary>
public sealed class FileReplaceRetrySettings
{
    public const string SectionName = "FileReplaceRetry";

    /// <summary>Total attempts of one operation, the first included.</summary>
    public int Attempts { get; set; } = 10;

    /// <summary>Fixed pause between two attempts.</summary>
    public int DelayMilliseconds { get; set; } = 100;
}

/// <summary>
/// System contract of <see cref="FileReplaceRetrySettings"/> (SA_ARCHITECTURE_RULES, numeric classification). These bands are code,
/// never configuration; the Python mirror is <c>checkpoints.REPLACE_*</c>.
/// </summary>
public static class FileReplaceRetryLimits
{
    public static readonly SettingBand Attempts = new(1, 50, 1, 20);
    public static readonly SettingBand DelayMilliseconds = new(1, 1000, 10, 500);

    /// <summary>
    /// The registry publishes inside its gate, so one failing operation may not hold shutdown and every reader for longer than a user
    /// accepts to wait; attempts x delay is bounded by this contract.
    /// </summary>
    public const long TotalWaitMaxMilliseconds = 10_000;
}

/// <summary>The startup validation phase of <see cref="FileReplaceRetrySettings"/> (the same three tiers as the monitoring settings).</summary>
public static class FileReplaceRetrySettingsValidator
{
    /// <summary>Throws on a hard-limit violation; returns the warnings of values that run but leave the standard band.</summary>
    public static IReadOnlyList<string> Validate(FileReplaceRetrySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var warnings = new List<string>();
        Check(warnings, nameof(settings.Attempts), settings.Attempts, FileReplaceRetryLimits.Attempts);
        Check(warnings, nameof(settings.DelayMilliseconds), settings.DelayMilliseconds, FileReplaceRetryLimits.DelayMilliseconds);
        long total = (long)settings.Attempts * settings.DelayMilliseconds;
        if (total > FileReplaceRetryLimits.TotalWaitMaxMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(settings), total,
                $"{nameof(settings.Attempts)} x {nameof(settings.DelayMilliseconds)} must not exceed {FileReplaceRetryLimits.TotalWaitMaxMilliseconds} milliseconds.");
        return warnings;
    }

    private static void Check(List<string> warnings, string name, long value, SettingBand band)
    {
        if (!band.IsWithinHardLimits(value))
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be {band.HardMin} to {band.HardMax}; the value is outside the system limits.");
        if (!band.IsWithinStandard(value))
            warnings.Add($"{FileReplaceRetrySettings.SectionName}:{name} = {value.ToString(CultureInfo.InvariantCulture)} is outside the standard range "
                + $"{band.StandardMin} to {band.StandardMax}; the application runs but a failing operation may wait longer or retry less.");
    }
}
