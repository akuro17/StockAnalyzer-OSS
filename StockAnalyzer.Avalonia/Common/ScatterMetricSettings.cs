using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Avalonia.Common;

/// <summary>
/// Operational values of the registered scatter metrics (appsettings.json section <c>ScatterMetrics</c>). The accepted ranges are the code contract
/// <see cref="ScatterMetricLimits"/>, checked at startup by <see cref="ScatterMetricSettingsValidator"/>; this class holds no bounds and no validation.
/// Its own section rather than the Screener's, so each screen stays independently tunable.
/// </summary>
public class ScatterMetricSettings
{
    /// <summary>How many metrics the Active list may hold.</summary>
    public int MaxRegisteredMetrics { get; set; } = ScatterMetricLimits.DefaultMaxRegisteredMetrics;

    /// <summary>Largest Offset (bars before the latest bar of the ticker's daily series) a metric may use.</summary>
    public int MaxOffset { get; set; } = ScatterMetricLimits.DefaultMaxOffset;

    /// <summary>How many tickers are evaluated at the same time.</summary>
    public int MaxParallelism { get; set; } = ScatterMetricLimits.DefaultMaxParallelism;

    /// <summary>Pause (ms) after the last key stroke before the registration screen's catalog search runs.</summary>
    public int InputDebounceMilliseconds { get; set; } = ScatterMetricLimits.DefaultInputDebounceMilliseconds;
}
