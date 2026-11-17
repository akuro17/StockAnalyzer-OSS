using System;
using System.Collections.Generic;

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
        SettingBandValidation.Check(MaxRegisteredMetricsKey, nameof(maxRegisteredMetrics), ScatterMetricLimits.MaxRegisteredMetrics, maxRegisteredMetrics, "metrics",
            "a longer Active list is slower to scroll and to pick from", warnings);
        SettingBandValidation.Check(MaxOffsetKey, nameof(maxOffset), ScatterMetricLimits.MaxOffset, maxOffset, "bars",
            "every ticker loads that many more bars per evaluation", warnings);
        SettingBandValidation.Check(MaxParallelismKey, nameof(maxParallelism), ScatterMetricLimits.MaxParallelism, maxParallelism, "tickers",
            "more tickers read their data files at the same time", warnings);
        SettingBandValidation.Check(InputDebounceMillisecondsKey, nameof(inputDebounceMilliseconds), ScatterMetricLimits.InputDebounceMilliseconds, inputDebounceMilliseconds, "ms",
            "the search either runs on every key stroke or reacts late", warnings);
        return warnings;
    }
}
