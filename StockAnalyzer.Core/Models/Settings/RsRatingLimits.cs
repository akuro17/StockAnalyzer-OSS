namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// System contract of the RS Rating settings that live in the <c>MarketDataSettings</c> section (see SA_ARCHITECTURE_RULES, numeric classification).
/// The bands are code, never configuration: a configuration file cannot widen them. <see cref="RsRatingSettingsValidator"/> compares the settings
/// with them at startup; <see cref="MarketDataSettings"/> holds the operational values only. The bands are the owner's proposals of 2026-11-16
/// (decision D-1c of the plan 09/10 peer review); none of them is derived from a measurement.
/// </summary>
public static class RsRatingLimits
{
    /// <summary>
    /// The shipped value of <c>MarketDataSettings:RsRatingMaxStaleCalendarDays</c> (decision D-1a): a symbol whose latest bar is more than this many
    /// calendar days older than the latest bar of the universe is left out of the RS Rating. It covers the regular market closures (year end, long holidays)
    /// of a universe that mixes markets. The one definition behind the settings class and the standard band.
    /// </summary>
    public const int DefaultMaxStaleCalendarDays = 10;

    /// <summary>
    /// The shipped value of <c>MarketDataSettings:RsRatingRefreshDebounceMilliseconds</c> (decision D-7b): the pause after the last data-refreshed message
    /// before the Tickers grid recomputes the RS Rating, so that a bulk sync costs one query. It equals the layout save debounce of <c>LayoutSaveScheduler</c>.
    /// </summary>
    public const int DefaultRefreshDebounceMilliseconds = 500;

    /// <summary>
    /// How far (calendar days) the latest bar of a symbol may lag behind the latest bar of the universe. 0 requires the same date. The hard upper bound
    /// is one year: a longer tolerance would not exclude any symbol that still has a bar in the current year, so the exclusion would be meaningless.
    /// </summary>
    public static readonly SettingBand MaxStaleCalendarDays = new(0, 366, 3, 31);

    /// <summary>
    /// The refresh debounce. 0 recomputes on every message (a bulk sync then queries once per symbol); the upper hard bound is the longest pause after which
    /// the column would look frozen.
    /// </summary>
    public static readonly SettingBand RefreshDebounceMilliseconds = new(0, 5000, 100, 2000);
}
