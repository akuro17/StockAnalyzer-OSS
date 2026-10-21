using System;
using System.Collections.Generic;
using System.Globalization;

namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// The startup validation phase of the scatter metric settings: the operational values are compared with <see cref="ScatterMetricLimits"/>.
/// A hard-limit violation is fatal (an exception that names the key stops the startup); a value between the standard and the hard band runs
/// and only produces a warning for the operator.
/// </summary>
public static class ScatterMetricSettingsValidator
{
    public const string SectionName = "ScatterMetrics";
    public const string MaxRegisteredMetricsKey = SectionName + ":MaxRegisteredMetrics";
    public const string MaxOffsetKey = SectionName + ":MaxOffset";
    public const string MaxParallelismKey = SectionName + ":MaxParallelism";
    public const string InputDebounceMillisecondsKey = SectionName + ":InputDebounceMilliseconds";

    /// <summary>
    /// Throws when a value is outside its hard band. Returns the warnings of values that run but leave the standard band
    /// (empty when every value is standard); the caller with a logger reports them.
    /// </summary>
    public static IReadOnlyList<string> Validate(int maxRegisteredMetrics, int maxOffset, int maxParallelism, int inputDebounceMilliseconds)
    {
        var warnings = new List<string>();
        CheckBand(MaxRegisteredMetricsKey, nameof(maxRegisteredMetrics), ScatterMetricLimits.MaxRegisteredMetrics, maxRegisteredMetrics, "metrics",
            "a longer Active list is slower to scroll and to pick from", warnings);
        CheckBand(MaxOffsetKey, nameof(maxOffset), ScatterMetricLimits.MaxOffset, maxOffset, "bars",
            "every ticker loads that many more bars per evaluation", warnings);
        CheckBand(MaxParallelismKey, nameof(maxParallelism), ScatterMetricLimits.MaxParallelism, maxParallelism, "tickers",
            "more tickers read their data files at the same time", warnings);
        CheckBand(InputDebounceMillisecondsKey, nameof(inputDebounceMilliseconds), ScatterMetricLimits.InputDebounceMilliseconds, inputDebounceMilliseconds, "ms",
            "the search either runs on every key stroke or reacts late", warnings);
        return warnings;
    }

    private static void CheckBand(string key, string parameterName, SettingBand band, int value, string unit, string consequence, List<string> warnings)
    {
        if (!band.IsWithinHardLimits(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value,
                $"{key} must be {Format(band.HardMin)} to {Format(band.HardMax)} {unit}; the value {Format(value)} is outside the system limits.");
        }

        if (!band.IsWithinStandard(value))
        {
            warnings.Add($"{key} = {Format(value)} {unit} is outside the standard range {Format(band.StandardMin)} to {Format(band.StandardMax)}; "
                + $"the application runs but {consequence}.");
        }
    }

    private static string Format(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
