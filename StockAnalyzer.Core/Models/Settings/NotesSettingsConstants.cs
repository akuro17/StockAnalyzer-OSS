using StockAnalyzer.Core.Models.Notes;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// Centralized constants for default Notes-tab settings (Settings &gt; Notes) and their valid ranges.
/// Mirrors ChartSettingsConstants' role for the Notes subsystem - Rule #37: No magic numbers for
/// global defaults.
/// </summary>
public static class NotesSettingsConstants
{
    // --- Images ---
    public const double DefaultThumbnailSizePixels = 48.0;
    public const double MinThumbnailSizePixels = 24.0;
    public const double MaxThumbnailSizePixels = 200.0;
    public const NoteImageDisplayMode DefaultImageDisplayMode = NoteImageDisplayMode.AttachmentList;
    public const int DefaultMaxRenderedThumbnails = 3;
    public const int MinRenderedThumbnailsLimit = 1;
    public const int MaxRenderedThumbnailsLimit = 10;

    // --- Read More ---
    public const int DefaultReadMoreMaxCharacters = 150;
    public const int DefaultReadMoreMaxLines = 5;

    // --- Thread Collapse ---
    public const int DefaultThreadCollapseThreshold = 3;
    public const int DefaultTailVisibleCount = 2;
    public const double DefaultConnectorLineLength = 60.0;
    public const double DefaultDashLength = 8.0;

    // --- Timeline Loading ---
    public const int DefaultTimelinePageSize = 10;

    // --- Upcoming Catalysts ---
    public const int DefaultUpcomingCatalystsLimit = 7;
    public const int MinUpcomingCatalystsLimit = 1;
    public const int MaxUpcomingCatalystsLimit = 100;
    public const int DefaultUpcomingCatalystsPastDaysRetention = -1;
    public const int MinUpcomingCatalystsPastDaysRetention = -365;
    public const int MaxUpcomingCatalystsPastDaysRetention = 365;

    // --- Appearance ---
    public const double DefaultBodyFontSize = 16.0;
    public const double MinBodyFontSize = 12.0;
    public const double MaxBodyFontSize = 24.0;

    /// <summary>The Notes background is the "App Background" of Settings &gt; Theme for the theme in effect
    /// (<c>INotesSettingsManager.DefaultBodyBackgroundColor</c>); this is only the fallback where no theme manager is available
    /// (design time, an item built without settings). Read from the Dark preset, never copied, so it cannot drift from the Theme.</summary>
    public static IndicatorColor FallbackBodyBackgroundColor => ThemeColors.Dark.ShellBackground;

    /// <summary>Default body text color of a theme: the "Text" color of that theme preset (<c>ThemeColors.ShellText</c>), read,
    /// never copied. The URL and hashtag colors default to the same value (owner request: same color as the body text).</summary>
    public static IndicatorColor GetDefaultBodyTextColor(bool isDarkTheme) => (isDarkTheme ? ThemeColors.Dark : ThemeColors.Light).ShellText;

    /// <summary>Fallback body / URL / hashtag text color where no theme manager is available (design time, an item built without
    /// settings): the Dark theme default.</summary>
    public static IndicatorColor FallbackBodyTextColor => GetDefaultBodyTextColor(isDarkTheme: true);

    /// <summary>Dark-theme default of the selection highlight behind text selected in a Note body (a dark blue
    /// that keeps the Dark default body text color readable). ARGB, consumed via
    /// <c>IndicatorColor.FromUInt(...)</c>.</summary>
    public const uint DefaultHighlightColorArgb = 0xFF264F78;

    /// <summary>Light-theme default of the selection highlight (a light blue that keeps
    /// the Light default body text color readable). ARGB, consumed via
    /// <c>IndicatorColor.FromUInt(...)</c>.</summary>
    public const uint DefaultHighlightColorArgbLight = 0xFFB3D4FF;

    /// <summary>Returns the theme-appropriate default selection-highlight color.</summary>
    public static uint GetDefaultHighlightColorArgb(bool isDarkTheme) => isDarkTheme ? DefaultHighlightColorArgb : DefaultHighlightColorArgbLight;
}
