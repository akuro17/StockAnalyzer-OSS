namespace StockAnalyzer.Core.Models;

/// <summary>
/// Centralized default values for all indicator settings.
/// Replaces hardcoded magic numbers in DefaultCoreIndicatorSettings.
/// </summary>
public static class IndicatorDefaultConstants
{
    // === Common Defaults ===
    public const double DefaultOverlayThickness = 1.5;
    public const double DefaultSubPanelThickness = 1.5;
    public const double DefaultBandThickness = 1.0;

    // === Shared Colors ===
    public static readonly IndicatorColor Gray = new(255, 128, 128, 128);
    public static readonly IndicatorColor Purple = new(255, 171, 71, 188); // #AB47BC
    public static readonly IndicatorColor Cyan = new(255, 0, 172, 193); // #00ACC1
    public static readonly IndicatorColor Blue = new(255, 41, 98, 255); // #2962FF
    public static readonly IndicatorColor DeepPurple = new(255, 126, 87, 194); // #7E57C2
    public static readonly IndicatorColor LightBlue = new(255, 33, 150, 243); // #2196F3
    public static readonly IndicatorColor Orange = new(255, 255, 152, 0); // #FF9800

    // === SMA ===
    public const int SmaPeriod = 20;
    public static readonly IndicatorColor SmaColor = new(255, 30, 144, 255); // DodgerBlue #1E90FF

    // === EMA ===
    public const int EmaPeriod = 20;
    public static readonly IndicatorColor EmaColor = new(255, 30, 144, 255); // DodgerBlue #1E90FF

    // === Bollinger Bands ===
    public const int BollingerPeriod = 20;
    public const decimal BollingerStdDevMultiplier = 2.0m;
    public static readonly IndicatorColor BollingerMiddleColor = new(255, 41, 98, 255); // Blue #2962FF
    public static readonly IndicatorColor BollingerBandsColor = new(255, 126, 87, 194);  // Purple #7E57C2

    // === Ichimoku ===
    public static readonly IndicatorColor IchimokuBaseColor = new(255, 128, 128, 128); // Gray
    public static readonly IndicatorColor IchimokuTenkanColor = new(255, 171, 71, 188);   // Purple #AB47BC
    public static readonly IndicatorColor IchimokuKijunColor = new(255, 41, 98, 255);    // Blue #2962FF
    public static readonly IndicatorColor IchimokuChikouColor = new(255, 117, 117, 117);   // Gray #757575
    public static readonly IndicatorColor IchimokuSenkouAColor = new(255, 67, 160, 71); // Green #43A047
    public static readonly IndicatorColor IchimokuSenkouBColor = new(255, 251, 140, 0); // Orange #FB8C00

    // === Parabolic SAR ===
    public const decimal ParabolicAccelerationStart = 0.02m;
    public const decimal ParabolicAccelerationStep = 0.02m;
    public const decimal ParabolicAccelerationMax = 0.2m;
    public static readonly IndicatorColor ParabolicColor = new(255, 0, 255, 255); // Cyan
    public static readonly IndicatorColor ParabolicUpColor = new(255, 0, 255, 0); // Green
    public static readonly IndicatorColor ParabolicDownColor = new(255, 255, 0, 0); // Red

    // === RSI ===
    public const int RsiPeriod = 14;
    public static readonly IndicatorColor RsiColor = new(255, 171, 71, 188); // Purple #AB47BC
    public const decimal RsiMinValue = 0;
    public const decimal RsiMaxValue = 100;

    // === MACD ===
    public static readonly IndicatorColor MacdLineColor = new(255, 41, 98, 255);     // Blue #2962FF
    public static readonly IndicatorColor MacdSignalColor = new(255, 251, 140, 0);    // Orange #FB8C00
    public static readonly IndicatorColor MacdHistogramColor = new(255, 128, 128, 128); // Gray
    public static readonly IndicatorColor MacdHistogramUpColor = new(255, 38, 166, 154);   // Green #26A69A
    public static readonly IndicatorColor MacdHistogramDownColor = new(255, 239, 83, 80); // Red #EF5350

    // === Stochastic ===
    public static readonly IndicatorColor StochBaseColor = new(255, 0, 172, 193); // Cyan #00ACC1
    public const decimal StochMinValue = 0;
    public const decimal StochMaxValue = 100;
    public static readonly IndicatorColor StochKColor = new(255, 0, 172, 193);  // Cyan #00ACC1
    public static readonly IndicatorColor StochDColor = new(255, 171, 71, 188);  // Purple #AB47BC

    // === Volume ===
    public static readonly IndicatorColor VolumeUpColor = new(255, 0, 128, 0);   // Dark Green (SKColors.Green equivalent)
    public static readonly IndicatorColor VolumeDownColor = new(255, 255, 0, 0); // Red (SKColors.Red equivalent)

    // === Volume Profile ===
    public const int VolumeProfilePeriod = 200;
    public const int VolumeProfileRowCount = 50;
    public const double VolumeProfileOpacity = 0.3;
    public static readonly IndicatorColor VolumeProfileColor = new(255, 128, 128, 128); // Gray

    // === MESA ===
    public static readonly IndicatorColor MesaBaseColor = new(255, 41, 98, 255); // Blue #2962FF
    public static readonly IndicatorColor MesaMamaColor = new(255, 41, 98, 255); // Blue #2962FF
    public static readonly IndicatorColor MesaFamaColor = new(255, 126, 87, 194);   // Purple #7E57C2

    // === Structural DTW ===
    public static readonly IndicatorColor StructuralDtwColor = new(255, 30, 144, 255); // DodgerBlue

    // === Divergence & Cross ===
    public static readonly IndicatorColor DivergenceBullishColor = new(255, 76, 175, 80); // Green
    public static readonly IndicatorColor DivergenceBearishColor = new(255, 244, 67, 54); // Red
    public static readonly IndicatorColor CrossGoldenColor = new(255, 255, 193, 7); // Amber/Gold
    public static readonly IndicatorColor CrossDeadColor = new(255, 156, 39, 176); // Purple
    public const int DivergencePivotLookback = 5;
    public const int CrossShortPeriod = 25;
    public const int CrossLongPeriod = 75;

    // === Granville's Law ===
    public const int GranvilleMaPeriod = 200;
    public const int GranvilleSlopePeriod = 5;
    public const decimal GranvilleDeviationThreshold = 10.0m;
    public const decimal GranvilleBounceTolerance = 0.5m;
    public const decimal GranvilleFlatThreshold = 0.05m;
    public const bool GranvilleShowSubWindowBar = true;
    public const decimal GranvilleHistogramMax = 4.0m;
    public const decimal GranvilleHistogramMin = -4.0m;

    public static readonly IndicatorColor GranvilleBuy1Color = new(255, 56, 142, 60);   // #388E3C
    public static readonly IndicatorColor GranvilleBuy2Color = new(255, 76, 175, 80);   // #4CAF50
    public static readonly IndicatorColor GranvilleBuy3Color = new(255, 129, 199, 132); // #81C784
    public static readonly IndicatorColor GranvilleBuy4Color = new(255, 200, 230, 201); // #C8E6C9
    public static readonly IndicatorColor GranvilleSell1Color = new(255, 211, 47, 47);  // #D32F2F
    public static readonly IndicatorColor GranvilleSell2Color = new(255, 244, 67, 54);  // #F44336
    public static readonly IndicatorColor GranvilleSell3Color = new(255, 229, 115, 115); // #E57373
    public static readonly IndicatorColor GranvilleSell4Color = new(255, 255, 205, 210); // #FFCDD2

    // === Subwindow Indicators ===
    public static readonly IndicatorColor AtrColor = new(255, 171, 71, 188); // Purple #AB47BC
    public static readonly IndicatorColor RocColor = new(255, 171, 71, 188); // Purple #AB47BC
}
