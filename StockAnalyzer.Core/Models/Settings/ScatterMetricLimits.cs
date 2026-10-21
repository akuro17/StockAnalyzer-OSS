namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// System contract of the scatter metric settings (section <c>ScatterMetrics</c>; see SA_ARCHITECTURE_RULES, numeric classification). The bands are code,
/// never configuration: a configuration file cannot widen them. <see cref="ScatterMetricSettingsValidator"/> compares the settings with them at startup;
/// the settings class holds the operational values only.
/// The Offset band is the Screener's own band (one definition, not a copy). The other hard and standard bands are the owner's proposals of 2026-10-21;
/// none of them is derived from a measurement.
/// </summary>
public static class ScatterMetricLimits
{
    /// <summary>The shipped value of <c>ScatterMetrics:MaxRegisteredMetrics</c>; the one definition behind the settings class, the interface default member and the end of the standard band.</summary>
    public const int DefaultMaxRegisteredMetrics = 32;

    /// <summary>The shipped value of <c>ScatterMetrics:MaxOffset</c>: the Screener's shipped Offset limit (one definition).</summary>
    public const int DefaultMaxOffset = ScreenerConditionOffsetLimits.DefaultMaxConditionOffset;

    /// <summary>The shipped value of <c>ScatterMetrics:MaxParallelism</c>: the number of tickers evaluated at the same time (the Screener ships 10).</summary>
    public const int DefaultMaxParallelism = 10;

    /// <summary>The shipped value of <c>ScatterMetrics:InputDebounceMilliseconds</c>: the pause after the last key stroke before the catalog search runs.</summary>
    public const int DefaultInputDebounceMilliseconds = 300;

    /// <summary>How many metrics may be registered. The hard band ends where the Active list and the two combo boxes stop being usable.</summary>
    public static readonly SettingBand MaxRegisteredMetrics = new(1, 128, 1, DefaultMaxRegisteredMetrics);

    /// <summary>
    /// The largest Offset (bars before the latest bar) a metric may use. The same band as <see cref="ScreenerConditionOffsetLimits.MaxConditionOffset"/>:
    /// the history a scan loads grows with the Offset, and the lower bound is the causality invariant (a negative Offset would read a future bar).
    /// </summary>
    public static readonly SettingBand MaxOffset = ScreenerConditionOffsetLimits.MaxConditionOffset;

    /// <summary>How many tickers are evaluated at the same time. At least one; the upper hard bound keeps the open data readers finite.</summary>
    public static readonly SettingBand MaxParallelism = new(1, 64, 1, 16);

    /// <summary>The input debounce. 0 means no pause; the upper hard bound is the longest pause after which the screen would feel unresponsive.</summary>
    public static readonly SettingBand InputDebounceMilliseconds = new(0, 2000, 100, 1000);
}
