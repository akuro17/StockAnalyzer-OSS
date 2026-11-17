using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// The startup validation phase of the RS Rating settings: the operational values are compared with <see cref="RsRatingLimits"/>.
/// A hard-limit violation is fatal (an exception that names the key stops the startup); a value between the standard and the hard band runs
/// and only produces a warning for the operator. A key that is absent from the configuration keeps the C# default of
/// <see cref="MarketDataSettings"/>, which lies inside the standard band, so it passes silently.
/// </summary>
public static class RsRatingSettingsValidator
{
    public const string SectionName = "MarketDataSettings";
    public const string MaxStaleCalendarDaysKey = SectionName + ":RsRatingMaxStaleCalendarDays";
    public const string RefreshDebounceMillisecondsKey = SectionName + ":RsRatingRefreshDebounceMilliseconds";

    /// <summary>
    /// Throws when a value is outside its hard band. Returns the warnings of values that run but leave the standard band
    /// (empty when every value is standard); the caller with a logger reports them.
    /// </summary>
    public static IReadOnlyList<string> Validate(int maxStaleCalendarDays, int refreshDebounceMilliseconds)
    {
        var warnings = new List<string>();
        CheckMaxStaleCalendarDays(maxStaleCalendarDays, warnings);
        SettingBandValidation.Check(RefreshDebounceMillisecondsKey, nameof(refreshDebounceMilliseconds), RsRatingLimits.RefreshDebounceMilliseconds, refreshDebounceMilliseconds, "ms",
            "the column either recomputes on every message or reacts late", warnings);
        return warnings;
    }

    /// <summary>
    /// The check of the stale tolerance alone, for the consumer that reads only this value (the RS Rating data provider), so that it never runs with a value outside
    /// the hard band even when it is built before the startup validation has run. Same exception and same warnings as <see cref="Validate"/>.
    /// </summary>
    public static IReadOnlyList<string> ValidateMaxStaleCalendarDays(int maxStaleCalendarDays)
    {
        var warnings = new List<string>();
        CheckMaxStaleCalendarDays(maxStaleCalendarDays, warnings);
        return warnings;
    }

    private static void CheckMaxStaleCalendarDays(int maxStaleCalendarDays, List<string> warnings)
        => SettingBandValidation.Check(MaxStaleCalendarDaysKey, nameof(maxStaleCalendarDays), RsRatingLimits.MaxStaleCalendarDays, maxStaleCalendarDays, "days",
            "a short tolerance leaves out symbols of markets with long closures, a long one lets stale symbols into the ranking", warnings);
}
