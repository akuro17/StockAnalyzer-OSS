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

