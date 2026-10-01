using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Common;

/// <summary>
/// Single source of the user-visible "Read more" / "Show less" wording shared by the Notes timeline card's toggle
/// (<c>NoteTimelineItemViewModel.ToggleLabel</c>) and, for "Read more", the Tickers-tab Notes hover popup
/// (<see cref="TickerNotesDisplayContext.ReadMoreLabel"/>), resolved from the locale files.
/// </summary>
public static class NoteReadMoreLabel
{
    /// <summary>Locale key (present in every shipped locale file) of the "Read more" wording.</summary>
    public const string LocalizationKey = "Notes_ReadMore";

    /// <summary>Locale key (present in every shipped locale file) of the "Show less" wording.</summary>
    public const string ShowLessLocalizationKey = "Notes_ShowLess";

    /// <summary>The "Read more" wording for the current UI language.</summary>
    public static string Get() => LocalizationManager.Instance[LocalizationKey];

    /// <summary>The "Show less" wording for the current UI language.</summary>
    public static string GetShowLess() => LocalizationManager.Instance[ShowLessLocalizationKey];
}
