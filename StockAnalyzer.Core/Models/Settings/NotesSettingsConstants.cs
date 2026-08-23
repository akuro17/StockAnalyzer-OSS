namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// Centralized constants for default Notes-tab settings (Settings &gt; Notes) and their valid ranges.
/// Mirrors ChartSettingsConstants' role for the Notes subsystem - Rule #37: No magic numbers for
/// global defaults.
/// </summary>
public static class NotesSettingsConstants
{
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

    // --- Appearance ---
    public const double DefaultBodyFontSize = 16.0;
    public const double MinBodyFontSize = 12.0;
    public const double MaxBodyFontSize = 24.0;

    /// <summary>Dark-theme default. ARGB, consumed via <c>IndicatorColor.FromUInt(...)</c>.</summary>
    public const uint DefaultBodyTextColorArgb = 0xFFE0E0E0;

    /// <summary>Dark-theme default. ARGB, consumed via <c>IndicatorColor.FromUInt(...)</c>.</summary>
    public const uint DefaultBodyBackgroundColorArgb = 0xFF181A20;

    /// <summary>Light-theme default (mirrors <c>ThemeColors.Light.ShellText</c>). ARGB, consumed via
    /// <c>IndicatorColor.FromUInt(...)</c>.</summary>
    public const uint DefaultBodyTextColorArgbLight = 0xFF1F2328;

    /// <summary>Light-theme default (mirrors <c>ThemeColors.Light.ShellBackground</c>). ARGB, consumed
    /// via <c>IndicatorColor.FromUInt(...)</c>.</summary>
    public const uint DefaultBodyBackgroundColorArgbLight = 0xFFFFFFFF;

    /// <summary>Defaults to the same value as <see cref="DefaultBodyTextColorArgb"/> (fix request:
    /// "デフォルトカラーを文章と同じ色にして"), consumed via <c>IndicatorColor.FromUInt(...)</c>.</summary>
    public const uint DefaultUrlColorArgb = DefaultBodyTextColorArgb;

    /// <summary>Same rationale as <see cref="DefaultUrlColorArgb"/>.</summary>
    public const uint DefaultHashtagColorArgb = DefaultBodyTextColorArgb;

    /// <summary>Light-theme counterpart of <see cref="DefaultUrlColorArgb"/>.</summary>
    public const uint DefaultUrlColorArgbLight = DefaultBodyTextColorArgbLight;

    /// <summary>Light-theme counterpart of <see cref="DefaultHashtagColorArgb"/>.</summary>
    public const uint DefaultHashtagColorArgbLight = DefaultBodyTextColorArgbLight;

    /// <summary>Returns the theme-appropriate default text/URL/hashtag color.</summary>
    public static uint GetDefaultBodyTextColorArgb(bool isDarkTheme) => isDarkTheme ? DefaultBodyTextColorArgb : DefaultBodyTextColorArgbLight;

    /// <summary>Returns the theme-appropriate default background color.</summary>
    public static uint GetDefaultBodyBackgroundColorArgb(bool isDarkTheme) => isDarkTheme ? DefaultBodyBackgroundColorArgb : DefaultBodyBackgroundColorArgbLight;
}
