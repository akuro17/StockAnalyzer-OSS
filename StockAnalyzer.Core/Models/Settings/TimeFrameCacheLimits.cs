namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// System contract of the TimeFrame cache settings inside <c>MarketDataSettings</c> (see SA_ARCHITECTURE_RULES, numeric classification).
/// The bands are code, never configuration: <see cref="TimeFrameCacheSettingsValidator"/> compares the configured value at startup.
/// </summary>
public static class TimeFrameCacheLimits
{
    /// <summary>
    /// Shipped default of <c>MarketDataSettings:TimeFrameCacheMaxEntries</c>, sized for rapid switching in 2,000-4,000 ticker universes.
    /// <see cref="StockAnalyzer.Core.Services.TimeFrameManager"/> preserves its standalone default constructor argument (20) for design-time and legacy callers.
    /// </summary>
    public const int DefaultMaxEntries = 256;

    /// <summary>
    /// Hard band [1, 4096] guards against invalid cache bounds and excessive memory retention; standard band [20, 512] runs silently without warning.
    /// </summary>
    public static readonly SettingBand MaxEntries = new(1, 4096, 20, 512);
}
