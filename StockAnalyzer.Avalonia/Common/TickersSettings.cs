namespace StockAnalyzer.Avalonia.Common;

/// <summary>
/// Configuration ("Tickers" section of appsettings.json) for the Tickers tab settings page
/// (Settings &gt; Tickers, Y:\Temp\sa_implementation_plan_TickersSettingsCategory.md). Each default and
/// tunable bound has exactly one definition: the property initializer below.
/// </summary>
public class TickersSettings
{
    /// <summary>
    /// Smallest interval, in whole seconds, between two automatic ticker changes. An invariant (a zero or
    /// negative interval would make the auto-play timer spin), therefore a code constant rather than a setting.
    /// </summary>
    public const int MinAutoPlayIntervalSeconds = 1;

    /// <summary>Default interval, in whole seconds, between two automatic ticker changes.</summary>
    public int AutoPlayDefaultIntervalSeconds { get; set; } = 5;

    /// <summary>Largest interval, in whole seconds, the interval input accepts.</summary>
    public int AutoPlayMaxIntervalSeconds { get; set; } = 3600;

    /// <summary>Default of "stop when the interval elapses on the last row of the list".</summary>
    public bool AutoPlayDefaultStopAtListEnd { get; set; } = true;

    /// <summary>Default of "stop when a different ticker list is selected".</summary>
    public bool AutoPlayDefaultStopOnListChange { get; set; } = true;

    public void Validate()
    {
        if (AutoPlayMaxIntervalSeconds < MinAutoPlayIntervalSeconds)
            throw new System.InvalidOperationException($"TickersSettings: AutoPlayMaxIntervalSeconds must be >= {MinAutoPlayIntervalSeconds}.");
        if (AutoPlayDefaultIntervalSeconds < MinAutoPlayIntervalSeconds || AutoPlayDefaultIntervalSeconds > AutoPlayMaxIntervalSeconds)
            throw new System.InvalidOperationException("TickersSettings: AutoPlayDefaultIntervalSeconds must be within [MinAutoPlayIntervalSeconds, AutoPlayMaxIntervalSeconds].");
    }
}
