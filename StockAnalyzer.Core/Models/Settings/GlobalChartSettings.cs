using System.Text.Json.Serialization;

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
    public decimal PnfBoxPercent { get; init; } = 1.0m;
    public Models.ChartRoundingMode PnfRoundingMode { get; init; } = Models.ChartRoundingMode.None;
    public Models.AutoFallbackMode PnfFallbackMode { get; init; } = Models.AutoFallbackMode.Percentage;
    public bool PnfShowMultiWavePatterns { get; init; } = false;
    public bool PnfShowGhostProjections { get; init; } = false;
    public float PnfGhostProjectionFontSize { get; init; } = 11.0f;
    public bool PnfShowGhostLabelsOnHoverOnly { get; init; } = true;

    // --- Three Line Break ---
    public string ThreeLineBreakBullishColor { get; init; } = ChartSettingsConstants.DefaultBullishColor;
    public string ThreeLineBreakBearishColor { get; init; } = ChartSettingsConstants.DefaultBearishColor;
    public bool ThreeLineBreakShowMultiWavePatterns { get; init; } = false;
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

        var result = this with 
        { 
            DefaultStrokeThickness = thickness,
            GhostProjectionFontSize = Math.Clamp(GhostProjectionFontSize, 8.0f, 32.0f),
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
