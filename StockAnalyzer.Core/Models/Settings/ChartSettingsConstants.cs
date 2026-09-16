namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// Centralized constants for default chart settings and visual parameters.
/// Rule #37: No magic strings for global defaults.
/// Renamed to ChartSettingsConstants to avoid collision with Avalonia.Models.SettingsConstants.
/// </summary>
public static class ChartSettingsConstants
{
    // --- Standard Colors ---
    public const string DefaultBullishColor = "#00B050";
    public const string DefaultBearishColor = "#FF0000";
    public const string DefaultNeutralColor = "#808080";
    public const string DefaultLineColor = "#2196F3";
    public const string DefaultAreaColor = "#2196F3";
    
    // --- Heikin Ashi Specialized ---
    public const string DefaultHeikinBullishColor = "#26A69A";
    public const string DefaultHeikinBearishColor = "#EF5350";

    // --- Reverse Watch Semantic Phases ---
    public const string DefaultReverseWatchPhase1 = "#00AA00"; // Green
    public const string DefaultReverseWatchPhase2 = "#005500"; // Dark Green
    public const string DefaultReverseWatchPhase3 = "#E6B800"; // Yellow
    public const string DefaultReverseWatchPhase4 = "#E65C5C"; // Salmon
    public const string DefaultReverseWatchPhase5 = "#EE0000"; // Red
    public const string DefaultReverseWatchPhase6 = "#8B0000"; // Dark Red
    public const string DefaultReverseWatchPhase7 = "#607D8B"; // Blue Gray
    public const string DefaultReverseWatchPhase8 = "#88CC00"; // Lime Green

    // --- Seasonality Chart ---
    /// <summary>Default starting month for the seasonality 1-year cycle (1 = January, 4 = April for Japanese fiscal year).</summary>
    public const uint DefaultSeasonalityStartMonth = 1;

    /// <summary>Minimum selectable seasonality starting month (January).</summary>
    public const uint MinSeasonalityStartMonth = 1;

    /// <summary>Maximum selectable seasonality starting month (December).</summary>
    public const uint MaxSeasonalityStartMonth = 12;

    /// <summary>Default radius (DIP) for the in-progress newest-year endpoint marker in Polar Clock mode.</summary>
    public const float DefaultSeasonalityEndpointMarkerRadius = 4.5f;

    /// <summary>Minimum selectable endpoint marker radius (DIP).</summary>
    public const float MinSeasonalityEndpointMarkerRadius = 1.0f;

    /// <summary>Maximum selectable endpoint marker radius (DIP).</summary>
    public const float MaxSeasonalityEndpointMarkerRadius = 15.0f;

    /// <summary>Spinner step for endpoint marker radius (DIP).</summary>
    public const float SeasonalityEndpointMarkerRadiusStep = 0.5f;

    /// <summary>Default matrix orientation for the monthly returns table (MonthsAsRows = classic).</summary>
    public const StockAnalyzer.Core.Analysis.SeasonalityMonthlyTableOrientation DefaultSeasonalityMonthlyTableOrientation =
        StockAnalyzer.Core.Analysis.SeasonalityMonthlyTableOrientation.MonthsAsRows;

    /// <summary>Default font size (DIP) for the seasonality chart legend / axis labels / monthly table.</summary>
    public const float DefaultSeasonalityFontSize = 14.0f;

    /// <summary>Default number of most-recent calendar years overlaid on the seasonality chart.</summary>
    public const uint DefaultSeasonalityYearsToOverlay = 10;

    /// <summary>
    /// Default stroke width (DIP) for every per-year trace on the seasonality overlay plots (polar
    /// clock and linear year overlay). A chart-tab per-year row may override this for one calendar
    /// year for the session; only this common value persists.
    /// </summary>
    public const float DefaultSeasonalityLineThickness = 1.0f;

    /// <summary>Minimum selectable seasonality per-year trace stroke width (DIP).</summary>
    public const float MinSeasonalityLineThickness = 0.5f;

    /// <summary>Maximum selectable seasonality per-year trace stroke width (DIP).</summary>
    public const float MaxSeasonalityLineThickness = 5.0f;

    /// <summary>Spinner step for the seasonality per-year trace stroke width (DIP).</summary>
    public const float SeasonalityLineThicknessStep = 0.5f;

    // Monthly-returns table up/down/neutral cell colors. Defaults carry over the base theme's
    // Color.Semantic.Success / Color.Semantic.Error (the brushes the table used before this setting);
    // neutral mirrors ThemeColors.SemanticNeutral's base default (the flat/zero-change grey).
    public const string DefaultSeasonalityMonthlyUpColor = "#FF4CAF50";
    public const string DefaultSeasonalityMonthlyDownColor = "#FFF44336";
    public const string DefaultSeasonalityMonthlyNeutralColor = "#FF787B86";

    // Per-year overlay trace / legend palette (recency slot 1..10), filling to
    // SeasonalityChartConstants.SeasonalityBasePaletteSize (10). Defined independently of the
    // reverse-watch phase palette: the ten hues are arranged so that no two colours of the same
    // family (green / red / blue) land on adjacent recency slots, INCLUDING the wrap from slot 10
    // back to slot 1 (ResolveYearColor keys the slot by recencyIndex % SeasonalityBasePaletteSize).
    // Greens sit on slots 1 / 4 / 7, reds on 2 / 9, blues on 8 / 10; gold, pink and purple are
    // singletons. Slot 1 keeps the historical bright green so the most-recent year's colour is
    // unchanged.
    public const string DefaultSeasonalityYearColor1 = "#00AA00";  // Bright green
    public const string DefaultSeasonalityYearColor2 = "#EE0000";  // Red
    public const string DefaultSeasonalityYearColor3 = "#B366FF";  // Light purple
    public const string DefaultSeasonalityYearColor4 = "#005500";  // Dark green
    public const string DefaultSeasonalityYearColor5 = "#E6B800";  // Gold
    public const string DefaultSeasonalityYearColor6 = "#FF3399";  // Pink
    public const string DefaultSeasonalityYearColor7 = "#88CC00";  // Lime green
    public const string DefaultSeasonalityYearColor8 = "#607D8B";  // Blue gray
    public const string DefaultSeasonalityYearColor9 = "#E65C5C";  // Salmon
    public const string DefaultSeasonalityYearColor10 = "#00BFFF"; // Deep sky blue

    // --- Breakout / Multi-Wave Pattern Line (P&F/Renko/Kagi/ThreeLineBreak) ---
    public const string DefaultBreakoutBullishColor = "#00ACC1"; // Cyan
    public const string DefaultBreakoutBearishColor = "#D81B60"; // Magenta

    // --- Drawing Tool Defaults ---
    public const string DefaultDrawingColor = "#00B050";
    public const string DefaultDrawingHandleColor = "#FF0000";
    public const string DefaultDtwUnmatchedColor = "#FF0000";
    public const string DefaultAnchorPointColor = "#00FFFF";
    public const float DefaultDrawingFontSize = 12.0f;
    public const float MinDrawingFontSize = 8.0f;
    public const float MaxDrawingFontSize = 32.0f;
    public const float DefaultDrawingIconFontSize = 32.0f;
    public const float MinDrawingIconFontSize = 8.0f;
    public const float MaxDrawingIconFontSize = 128.0f;

    // --- Smart Guide & Snapping Defaults ---
    public const bool DefaultSmartGuidesEnabled = true;
    public const double DefaultSmartGuideSnapDistance = 5.0;
    public const double MinSmartGuideSnapDistance = 1.0;
    public const double MaxSmartGuideSnapDistance = 50.0;

    // --- Control Point Auto-Hide Defaults ---
    public const int DefaultControlPointHideTimeoutSeconds = 5;
    public const int MinControlPointHideTimeoutSeconds = 1;
    public const int MaxControlPointHideTimeoutSeconds = 60;
}

