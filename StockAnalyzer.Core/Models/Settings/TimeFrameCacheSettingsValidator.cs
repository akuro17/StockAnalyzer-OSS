using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// Startup validation for <see cref="MarketDataSettings.TimeFrameCacheMaxEntries"/> against <see cref="TimeFrameCacheLimits"/>.
/// Values outside the hard band stop startup; values between the standard and hard bands emit an operator warning.
/// </summary>
public static class TimeFrameCacheSettingsValidator
{
    public const string SectionName = "MarketDataSettings";
    public const string MaxEntriesKey = SectionName + ":TimeFrameCacheMaxEntries";

    /// <summary>
    /// Throws when <paramref name="maxEntries"/> is outside its hard band; returns warnings when outside the standard band.
    /// </summary>
    public static IReadOnlyList<string> Validate(int maxEntries)
    {
        var warnings = new List<string>();
        SettingBandValidation.Check(
            MaxEntriesKey,
            nameof(maxEntries),
            TimeFrameCacheLimits.MaxEntries,
            maxEntries,
            "entries",
            "fewer entries increase Parquet reads when switching tickers, while larger counts keep more bars in memory",
            warnings);
        return warnings;
    }
}
