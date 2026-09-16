using System.Text.Json.Serialization;
using StockAnalyzer.Core.Analysis;

namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// Represents global chart configuration settings.
/// GS-1-1: This is an application-wide setting record.
/// </summary>
public sealed record GlobalChartSettings
{
    /// <summary>
    /// GS-3-1: Default stroke thickness for drawing objects (DIP).
    /// Range: 0.1 to 10.0, Default: 1.0.
    /// </summary>
    public double DefaultStrokeThickness { get; init; } = 1.0;

    // --- Drawing Tool Settings ---
    public string DrawingDefaultColor { get; init; } = ChartSettingsConstants.DefaultDrawingColor;
    public string DrawingHandleColor { get; init; } = ChartSettingsConstants.DefaultDrawingHandleColor;

    /// <summary>
    /// Highlight color for a drawing object's reference/anchor control point
    /// (the pivot used for future rotation and extension-line operations).
    /// </summary>
    public string AnchorPointColor { get; init; } = ChartSettingsConstants.DefaultAnchorPointColor;
    public float DrawingFontSize { get; init; } = ChartSettingsConstants.DefaultDrawingFontSize;
    public float DrawingIconFontSize { get; init; } = ChartSettingsConstants.DefaultDrawingIconFontSize;
    public bool SmartGuidesEnabled { get; init; } = ChartSettingsConstants.DefaultSmartGuidesEnabled;
    public double SmartGuideSnapDistance { get; init; } = ChartSettingsConstants.DefaultSmartGuideSnapDistance;

    /// <summary>
    /// How long a selected drawing object's control point handles stay visible with no pointer
    /// activity before auto-deselecting. Range: 1 to 60 seconds, Default: 5.
    /// </summary>
    public int ControlPointHideTimeoutSeconds { get; init; } = ChartSettingsConstants.DefaultControlPointHideTimeoutSeconds;

    /// <summary>
    /// Whether a fixed-point drawing tool (e.g. TrendLine, Rectangle) reverts to Pointer or stays
    /// active for continuous drawing once a shape finishes placing its last point.
    /// </summary>
    public Models.DrawingToolContinuationMode DrawingToolContinuationMode { get; init; } = Models.DrawingToolContinuationMode.ReturnToPointer;

    /// <summary>
    /// GS-5: Whether crosshair axis labels are visible.
    /// </summary>
    public bool CrosshairLabelVisible { get; init; } = true;

    public float GhostProjectionFontSize { get; init; } = 12.0f;
    public bool ShowGhostLabelsOnHoverOnly { get; init; } = true;
    public List<string> ComparisonTargets { get; init; } = new();
    public List<RelativePerformancePreset> RelativePerformancePresets { get; init; } = new();
    public bool ShowFloatingTooltip { get; init; } = false;
    public ComparisonMode RelativePerformanceMode { get; init; } = ComparisonMode.Performance;
    public int ComparisonZScorePeriod { get; init; } = 20;
    public bool ShowTickerInsteadOfValue { get; init; } = false;
    
    // --- Layout & Visuals ---
    public float TopMargin { get; init; } = 0f;
    public float BottomMargin { get; init; } = 30f;
    public float RightMargin { get; init; } = 65f;
    public bool IsSubWindowVisible { get; init; } = true;

    /// <summary>
    /// When enabled, indicator calculation fetches extra out-of-display-range historical bars
    /// (sized to the largest active single-Period indicator) so long-period indicators do not
    /// show a leading gap within the currently displayed candle range. Default disabled.
    /// </summary>
    public bool EnableExtendedLookbackForIndicators { get; init; } = false;

    // --- Candlestick / OHLC / Heikin Ashi ---
    public string CandlestickBullishColor { get; init; } = ChartSettingsConstants.DefaultBullishColor;
    public string CandlestickBearishColor { get; init; } = ChartSettingsConstants.DefaultBearishColor;
    public string CandlestickNeutralColor { get; init; } = ChartSettingsConstants.DefaultNeutralColor;
    public string OhlcBullishColor { get; init; } = ChartSettingsConstants.DefaultBullishColor;
    public string OhlcBearishColor { get; init; } = ChartSettingsConstants.DefaultBearishColor;
    public string HeikinBullishColor { get; init; } = ChartSettingsConstants.DefaultBullishColor;
    public string HeikinBearishColor { get; init; } = ChartSettingsConstants.DefaultBearishColor;

    // --- Line / Area ---
    public string LineColor { get; init; } = ChartSettingsConstants.DefaultLineColor;
    public string AreaBaseColor { get; init; } = ChartSettingsConstants.DefaultAreaColor;
    public bool ShowLineMarkers { get; init; } = false;
    public bool ShowAreaMarkers { get; init; } = false;

    // --- Renko ---
    public Models.ChartSizingMode RenkoSizingMode { get; init; } = Models.ChartSizingMode.AutoAtr;
    public decimal RenkoBrickSize { get; init; } = 1.0m;
    public decimal RenkoBrickPercent { get; init; } = 1.0m;
    public int RenkoAtrPeriod { get; init; } = 14;
    public decimal RenkoAtrMultiplier { get; init; } = 1.0m;
    public string RenkoBullishColor { get; init; } = ChartSettingsConstants.DefaultBullishColor;
    public string RenkoBearishColor { get; init; } = ChartSettingsConstants.DefaultBearishColor;
    public bool RenkoShowMultiWavePatterns { get; init; } = false;
    public int RenkoMultiWaveMaxLines { get; init; } = 5;
    public string RenkoMultiWaveBullishColor { get; init; } = ChartSettingsConstants.DefaultBreakoutBullishColor;
    public string RenkoMultiWaveBearishColor { get; init; } = ChartSettingsConstants.DefaultBreakoutBearishColor;
    public bool RenkoShowGhostProjections { get; init; } = false;
    public Models.ChartRoundingMode RenkoRoundingMode { get; init; } = Models.ChartRoundingMode.None;
    public Models.AutoFallbackMode RenkoFallbackMode { get; init; } = Models.AutoFallbackMode.Percentage;
    public int RenkoReversal { get; init; } = 2;
    public float RenkoGhostProjectionFontSize { get; init; } = 11.0f;
    public bool RenkoShowGhostLabelsOnHoverOnly { get; init; } = true;

    // --- Kagi ---
    public Models.ChartSizingMode KagiReversalMode { get; init; } = Models.ChartSizingMode.AutoAtr;
    public decimal KagiReversalAmount { get; init; } = 1.0m;
    public decimal KagiReversalPercent { get; init; } = 1.0m;
    public int KagiAtrPeriod { get; init; } = 14;
    public decimal KagiAtrMultiplier { get; init; } = 1.0m;
    public string KagiBullishColor { get; init; } = ChartSettingsConstants.DefaultBullishColor;
    public string KagiBearishColor { get; init; } = ChartSettingsConstants.DefaultBearishColor;
    public Models.ChartRoundingMode KagiRoundingMode { get; init; } = Models.ChartRoundingMode.None;
    public Models.AutoFallbackMode KagiFallbackMode { get; init; } = Models.AutoFallbackMode.Percentage;
    public float KagiLineThickness { get; init; } = 1.0f;
    public int KagiInitialColumn { get; init; } = 0;
    public bool KagiShowMultiWavePatterns { get; init; } = false;
    public int KagiMultiWaveMaxLines { get; init; } = 5;
    public string KagiMultiWaveBullishColor { get; init; } = ChartSettingsConstants.DefaultBreakoutBullishColor;
    public string KagiMultiWaveBearishColor { get; init; } = ChartSettingsConstants.DefaultBreakoutBearishColor;
    public bool KagiShowGhostProjections { get; init; } = false;
    public float KagiGhostProjectionFontSize { get; init; } = 11.0f;
    public bool KagiShowGhostLabelsOnHoverOnly { get; init; } = true;

    // --- Point & Figure ---
    public Models.ChartSizingMode PnfSizingMode { get; init; } = Models.ChartSizingMode.AutoAtr;
    public decimal PnfBoxSize { get; init; } = 1.0m;
    public int PnfAtrPeriod { get; init; } = 14;
    public decimal PnfAtrMultiplier { get; init; } = 1.0m;
    public int PnfReversal { get; init; } = 3;
    public string PnfBullishColor { get; init; } = ChartSettingsConstants.DefaultBullishColor;
    public string PnfBearishColor { get; init; } = ChartSettingsConstants.DefaultBearishColor;
    public bool PnfShowDoubleBreakout { get; init; } = false;
    public bool PnfShowTripleBreakout { get; init; } = false;
    public bool PnfShowTrendlineBreakout { get; init; } = false;
    public bool PnfShowTriangleBreakout { get; init; } = false;
    public bool PnfShowCatapultBreakout { get; init; } = false;
    public string PnfBreakoutBullishColor { get; init; } = ChartSettingsConstants.DefaultBreakoutBullishColor;
    public string PnfBreakoutBearishColor { get; init; } = ChartSettingsConstants.DefaultBreakoutBearishColor;
    public decimal PnfBoxPercent { get; init; } = 1.0m;
    public Models.ChartRoundingMode PnfRoundingMode { get; init; } = Models.ChartRoundingMode.None;
    public Models.AutoFallbackMode PnfFallbackMode { get; init; } = Models.AutoFallbackMode.Percentage;
    public bool PnfShowMultiWavePatterns { get; init; } = false;
    public int PnfMultiWaveMaxLines { get; init; } = 5;
    public bool PnfShowGhostProjections { get; init; } = false;
    public float PnfGhostProjectionFontSize { get; init; } = 11.0f;
    public bool PnfShowGhostLabelsOnHoverOnly { get; init; } = true;

    // --- Three Line Break ---
    public string ThreeLineBreakBullishColor { get; init; } = ChartSettingsConstants.DefaultBullishColor;
    public string ThreeLineBreakBearishColor { get; init; } = ChartSettingsConstants.DefaultBearishColor;
    public bool ThreeLineBreakShowMultiWavePatterns { get; init; } = false;
    public int ThreeLineBreakMultiWaveMaxLines { get; init; } = 5;
    public string ThreeLineBreakMultiWaveBullishColor { get; init; } = ChartSettingsConstants.DefaultBreakoutBullishColor;
    public string ThreeLineBreakMultiWaveBearishColor { get; init; } = ChartSettingsConstants.DefaultBreakoutBearishColor;
    public bool ThreeLineBreakShowGhostProjections { get; init; } = false;
    public float ThreeLineBreakGhostProjectionFontSize { get; init; } = 11.0f;
    public bool ThreeLineBreakShowGhostLabelsOnHoverOnly { get; init; } = true;

    // --- Reverse Watch ---
    public string ReverseWatchPhase1Color { get; init; } = ChartSettingsConstants.DefaultReverseWatchPhase1;
    public string ReverseWatchPhase2Color { get; init; } = ChartSettingsConstants.DefaultReverseWatchPhase2;
    public string ReverseWatchPhase3Color { get; init; } = ChartSettingsConstants.DefaultReverseWatchPhase3;
    public string ReverseWatchPhase4Color { get; init; } = ChartSettingsConstants.DefaultReverseWatchPhase4;
    public string ReverseWatchPhase5Color { get; init; } = ChartSettingsConstants.DefaultReverseWatchPhase5;
    public string ReverseWatchPhase6Color { get; init; } = ChartSettingsConstants.DefaultReverseWatchPhase6;
    public string ReverseWatchPhase7Color { get; init; } = ChartSettingsConstants.DefaultReverseWatchPhase7;
    public string ReverseWatchPhase8Color { get; init; } = ChartSettingsConstants.DefaultReverseWatchPhase8;
    public bool ShowReverseWatchGrid { get; init; } = true;
    public bool ReverseWatchIsMaBased { get; init; } = true;
    public bool ReverseWatchIsLogScaleVolume { get; init; } = false;
    public int ReverseWatchPeriod { get; init; } = 20;
    public int ReverseWatchDataCount { get; init; } = 100;
    public float ReverseWatchLineThickness { get; init; } = 1.0f;

    // --- Seasonality Chart ---
    /// <summary>Starting month (1..12) for the seasonality 1-year cycle (default: 1 = January, 4 = April).</summary>
    public uint SeasonalityStartMonth { get; init; } = ChartSettingsConstants.DefaultSeasonalityStartMonth;

    /// <summary>Radius (DIP) for the newest-year endpoint marker in Polar Clock mode (default: 4.5).</summary>
    public float SeasonalityEndpointMarkerRadius { get; init; } = ChartSettingsConstants.DefaultSeasonalityEndpointMarkerRadius;

    /// <summary>Matrix orientation for the monthly returns table (Mode "Monthly"). Default: MonthsAsRows.</summary>
    public StockAnalyzer.Core.Analysis.SeasonalityMonthlyTableOrientation SeasonalityMonthlyTableOrientation { get; init; } =
        ChartSettingsConstants.DefaultSeasonalityMonthlyTableOrientation;

    /// <summary>
    /// Number of most-recent calendar years overlaid on the seasonality chart. The persistent single
    /// source of truth: the chart tab's own "Years to overlay" input syncs to this value.
    /// Range: <see cref="SeasonalityChartConstants.MinYearsToOverlay"/> ..
    /// <see cref="SeasonalityChartConstants.MaxYearsToOverlay"/>, Default: 10.
    /// </summary>
    public uint SeasonalityYearsToOverlay { get; init; } = ChartSettingsConstants.DefaultSeasonalityYearsToOverlay;

    /// <summary>
    /// Common stroke width (DIP) applied to every per-year trace on the seasonality overlay plots
    /// (polar clock and linear year overlay). The chart tab's per-year rows can override this for a
    /// single calendar year for the session; only this common value persists. Range:
    /// <see cref="ChartSettingsConstants.MinSeasonalityLineThickness"/> ..
    /// <see cref="ChartSettingsConstants.MaxSeasonalityLineThickness"/>, Default: 1.0.
    /// </summary>
    public float SeasonalityLineThickness { get; init; } = ChartSettingsConstants.DefaultSeasonalityLineThickness;

    /// <summary>Font size (DIP) for the seasonality plot legend rows (year + annual change).</summary>
    public float SeasonalityLegendFontSize { get; init; } = ChartSettingsConstants.DefaultSeasonalityFontSize;

    /// <summary>Font size (DIP) for the seasonality plot month labels and rho grid labels.</summary>
    public float SeasonalityAxisFontSize { get; init; } = ChartSettingsConstants.DefaultSeasonalityFontSize;

    /// <summary>Font size (DIP) for the seasonality monthly-returns table cells and headers.</summary>
    public float SeasonalityMonthlyTableFontSize { get; init; } = ChartSettingsConstants.DefaultSeasonalityFontSize;

    /// <summary>Positive-cell (up) color for the seasonality monthly-returns table.</summary>
    public string SeasonalityMonthlyUpColor { get; init; } = ChartSettingsConstants.DefaultSeasonalityMonthlyUpColor;

    /// <summary>Negative-cell (down) color for the seasonality monthly-returns table.</summary>
    public string SeasonalityMonthlyDownColor { get; init; } = ChartSettingsConstants.DefaultSeasonalityMonthlyDownColor;

    /// <summary>Neutral-cell (no change / zero) color for the seasonality monthly-returns table.</summary>
    public string SeasonalityMonthlyNeutralColor { get; init; } = ChartSettingsConstants.DefaultSeasonalityMonthlyNeutralColor;

    /// <summary>Per-year overlay palette by recency slot (1 = newest year .. 10, then wraps).</summary>
    public string SeasonalityYearColor1 { get; init; } = ChartSettingsConstants.DefaultSeasonalityYearColor1;
    public string SeasonalityYearColor2 { get; init; } = ChartSettingsConstants.DefaultSeasonalityYearColor2;
    public string SeasonalityYearColor3 { get; init; } = ChartSettingsConstants.DefaultSeasonalityYearColor3;
    public string SeasonalityYearColor4 { get; init; } = ChartSettingsConstants.DefaultSeasonalityYearColor4;
    public string SeasonalityYearColor5 { get; init; } = ChartSettingsConstants.DefaultSeasonalityYearColor5;
    public string SeasonalityYearColor6 { get; init; } = ChartSettingsConstants.DefaultSeasonalityYearColor6;
    public string SeasonalityYearColor7 { get; init; } = ChartSettingsConstants.DefaultSeasonalityYearColor7;
    public string SeasonalityYearColor8 { get; init; } = ChartSettingsConstants.DefaultSeasonalityYearColor8;
    public string SeasonalityYearColor9 { get; init; } = ChartSettingsConstants.DefaultSeasonalityYearColor9;
    public string SeasonalityYearColor10 { get; init; } = ChartSettingsConstants.DefaultSeasonalityYearColor10;

    /// <summary>
    /// GS-14-1: Schema version for future migration support.
    /// </summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// GS-3-2: Validates and recovers numeric constraints for settings.
    /// </summary>
    public GlobalChartSettings Validate()
    {
        double thickness = DefaultStrokeThickness;
        if (double.IsNaN(thickness) || double.IsInfinity(thickness) || thickness < 0.1 || thickness > 10.0)
        {
            thickness = 1.0; // Fallback recovered value
        }

        // GS-3-2: Migration/Auto-Heal: Detect old rainbow defaults for Reverse Watch and replace with semantic ones
        bool IsOld(string? val, string target) 
        {
            if (string.IsNullOrEmpty(val)) return false;
            string t6 = target.ToUpperInvariant();
            string t8 = "#FF" + t6.TrimStart('#');
            string v = val.ToUpperInvariant();
            return v == t6 || v == t8;
        }

        bool isOldRainbow = 
            IsOld(ReverseWatchPhase1Color, "#FF0000") &&
            IsOld(ReverseWatchPhase2Color, "#FFA500") &&
            IsOld(ReverseWatchPhase3Color, "#FFFF00") &&
            IsOld(ReverseWatchPhase4Color, "#008000") &&
            IsOld(ReverseWatchPhase5Color, "#0000FF") &&
            IsOld(ReverseWatchPhase6Color, "#4B0082") &&
            IsOld(ReverseWatchPhase7Color, "#EE82EE") &&
            IsOld(ReverseWatchPhase8Color, "#808080");

        static bool IsValidHexColor(string? color)
        {
            if (string.IsNullOrWhiteSpace(color)) return false;
            string c = color.Trim();
            if (!c.StartsWith('#')) return false;
            if (c.Length != 7 && c.Length != 9) return false;
            for (int i = 1; i < c.Length; i++)
            {
                char ch = c[i];
                if (!((ch >= '0' && ch <= '9') || (ch >= 'A' && ch <= 'F') || (ch >= 'a' && ch <= 'f')))
                    return false;
            }
            return true;
        }

        double snapDistance = SmartGuideSnapDistance;
        if (double.IsNaN(snapDistance) || double.IsInfinity(snapDistance) ||
            snapDistance < ChartSettingsConstants.MinSmartGuideSnapDistance ||
            snapDistance > ChartSettingsConstants.MaxSmartGuideSnapDistance)
        {
            snapDistance = ChartSettingsConstants.DefaultSmartGuideSnapDistance;
        }

        var result = this with 
        { 
            DefaultStrokeThickness = thickness,
            DrawingFontSize = Math.Clamp(DrawingFontSize, ChartSettingsConstants.MinDrawingFontSize, ChartSettingsConstants.MaxDrawingFontSize),
            DrawingIconFontSize = Math.Clamp(DrawingIconFontSize, ChartSettingsConstants.MinDrawingIconFontSize, ChartSettingsConstants.MaxDrawingIconFontSize),
            DrawingDefaultColor = IsValidHexColor(DrawingDefaultColor) ? DrawingDefaultColor : ChartSettingsConstants.DefaultDrawingColor,
            DrawingHandleColor = IsValidHexColor(DrawingHandleColor) ? DrawingHandleColor : ChartSettingsConstants.DefaultDrawingHandleColor,
            AnchorPointColor = IsValidHexColor(AnchorPointColor) ? AnchorPointColor : ChartSettingsConstants.DefaultAnchorPointColor,
            SmartGuidesEnabled = SmartGuidesEnabled,
            SmartGuideSnapDistance = snapDistance,
            ControlPointHideTimeoutSeconds = Math.Clamp(ControlPointHideTimeoutSeconds, ChartSettingsConstants.MinControlPointHideTimeoutSeconds, ChartSettingsConstants.MaxControlPointHideTimeoutSeconds),
            GhostProjectionFontSize = Math.Clamp(GhostProjectionFontSize, ChartSettingsConstants.MinDrawingFontSize, ChartSettingsConstants.MaxDrawingFontSize),
            RenkoAtrPeriod = Math.Clamp(RenkoAtrPeriod, 1, 500),
            KagiAtrPeriod = Math.Clamp(KagiAtrPeriod, 1, 500),
            KagiLineThickness = Math.Clamp(KagiLineThickness, 0.5f, 10.0f),
            PnfAtrPeriod = Math.Clamp(PnfAtrPeriod, 1, 500),
            PnfReversal = Math.Clamp(PnfReversal, 1, 10),
            PnfBoxPercent = Math.Clamp(PnfBoxPercent, 0.01m, 100m),
            ComparisonZScorePeriod = Math.Clamp(ComparisonZScorePeriod, 1, 500),
            ReverseWatchPeriod = Math.Clamp(ReverseWatchPeriod, 1, 200),
            ReverseWatchDataCount = Math.Clamp(ReverseWatchDataCount, 10, 1000),
            ReverseWatchLineThickness = Math.Clamp(ReverseWatchLineThickness, 0.5f, 5.0f),
            SeasonalityStartMonth = Math.Clamp(
                SeasonalityStartMonth,
                ChartSettingsConstants.MinSeasonalityStartMonth,
                ChartSettingsConstants.MaxSeasonalityStartMonth),
            SeasonalityEndpointMarkerRadius = Math.Clamp(
                SeasonalityEndpointMarkerRadius,
                ChartSettingsConstants.MinSeasonalityEndpointMarkerRadius,
                ChartSettingsConstants.MaxSeasonalityEndpointMarkerRadius),
            SeasonalityMonthlyTableOrientation = Enum.IsDefined(SeasonalityMonthlyTableOrientation)
                ? SeasonalityMonthlyTableOrientation
                : ChartSettingsConstants.DefaultSeasonalityMonthlyTableOrientation,
            SeasonalityYearsToOverlay = Math.Clamp(
                SeasonalityYearsToOverlay,
                SeasonalityChartConstants.MinYearsToOverlay,
                SeasonalityChartConstants.MaxYearsToOverlay),
            SeasonalityLineThickness = Math.Clamp(
                SeasonalityLineThickness,
                ChartSettingsConstants.MinSeasonalityLineThickness,
                ChartSettingsConstants.MaxSeasonalityLineThickness),
            SeasonalityLegendFontSize = Math.Clamp(SeasonalityLegendFontSize, ChartSettingsConstants.MinDrawingFontSize, ChartSettingsConstants.MaxDrawingFontSize),
            SeasonalityAxisFontSize = Math.Clamp(SeasonalityAxisFontSize, ChartSettingsConstants.MinDrawingFontSize, ChartSettingsConstants.MaxDrawingFontSize),
            SeasonalityMonthlyTableFontSize = Math.Clamp(SeasonalityMonthlyTableFontSize, ChartSettingsConstants.MinDrawingFontSize, ChartSettingsConstants.MaxDrawingFontSize),
            SeasonalityMonthlyUpColor = IsValidHexColor(SeasonalityMonthlyUpColor) ? SeasonalityMonthlyUpColor : ChartSettingsConstants.DefaultSeasonalityMonthlyUpColor,
            SeasonalityMonthlyDownColor = IsValidHexColor(SeasonalityMonthlyDownColor) ? SeasonalityMonthlyDownColor : ChartSettingsConstants.DefaultSeasonalityMonthlyDownColor,
            SeasonalityMonthlyNeutralColor = IsValidHexColor(SeasonalityMonthlyNeutralColor) ? SeasonalityMonthlyNeutralColor : ChartSettingsConstants.DefaultSeasonalityMonthlyNeutralColor,
            SeasonalityYearColor1 = IsValidHexColor(SeasonalityYearColor1) ? SeasonalityYearColor1 : ChartSettingsConstants.DefaultSeasonalityYearColor1,
            SeasonalityYearColor2 = IsValidHexColor(SeasonalityYearColor2) ? SeasonalityYearColor2 : ChartSettingsConstants.DefaultSeasonalityYearColor2,
            SeasonalityYearColor3 = IsValidHexColor(SeasonalityYearColor3) ? SeasonalityYearColor3 : ChartSettingsConstants.DefaultSeasonalityYearColor3,
            SeasonalityYearColor4 = IsValidHexColor(SeasonalityYearColor4) ? SeasonalityYearColor4 : ChartSettingsConstants.DefaultSeasonalityYearColor4,
            SeasonalityYearColor5 = IsValidHexColor(SeasonalityYearColor5) ? SeasonalityYearColor5 : ChartSettingsConstants.DefaultSeasonalityYearColor5,
            SeasonalityYearColor6 = IsValidHexColor(SeasonalityYearColor6) ? SeasonalityYearColor6 : ChartSettingsConstants.DefaultSeasonalityYearColor6,
            SeasonalityYearColor7 = IsValidHexColor(SeasonalityYearColor7) ? SeasonalityYearColor7 : ChartSettingsConstants.DefaultSeasonalityYearColor7,
            SeasonalityYearColor8 = IsValidHexColor(SeasonalityYearColor8) ? SeasonalityYearColor8 : ChartSettingsConstants.DefaultSeasonalityYearColor8,
            SeasonalityYearColor9 = IsValidHexColor(SeasonalityYearColor9) ? SeasonalityYearColor9 : ChartSettingsConstants.DefaultSeasonalityYearColor9,
            SeasonalityYearColor10 = IsValidHexColor(SeasonalityYearColor10) ? SeasonalityYearColor10 : ChartSettingsConstants.DefaultSeasonalityYearColor10,
            TopMargin = Math.Clamp(TopMargin, 0f, 200f),
            BottomMargin = Math.Clamp(BottomMargin, 0f, 200f),
            RightMargin = Math.Clamp(RightMargin, 0f, 200f)
        };

        if (isOldRainbow)
        {
            result = result with
            {
                ReverseWatchPhase1Color = ChartSettingsConstants.DefaultReverseWatchPhase1,
                ReverseWatchPhase2Color = ChartSettingsConstants.DefaultReverseWatchPhase2,
                ReverseWatchPhase3Color = ChartSettingsConstants.DefaultReverseWatchPhase3,
                ReverseWatchPhase4Color = ChartSettingsConstants.DefaultReverseWatchPhase4,
                ReverseWatchPhase5Color = ChartSettingsConstants.DefaultReverseWatchPhase5,
                ReverseWatchPhase6Color = ChartSettingsConstants.DefaultReverseWatchPhase6,
                ReverseWatchPhase7Color = ChartSettingsConstants.DefaultReverseWatchPhase7,
                ReverseWatchPhase8Color = ChartSettingsConstants.DefaultReverseWatchPhase8
            };
        }

        return result;
    }
}

/// <summary>
/// GS-7-1: AOT-compatible JSON serialization context for settings.
/// </summary>
[JsonSerializable(typeof(GlobalChartSettings))]
internal partial class GlobalChartSettingsJsonContext : JsonSerializerContext
{
}
