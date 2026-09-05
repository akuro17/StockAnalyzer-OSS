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

    /// <summary>
    /// Multiplier for calculating warmup overlap period for recursive/EMA-based indicators.
    /// Period * EmaConvergenceMultiplier steps provides 99.9% convergence.
    /// </summary>
    public const int EmaConvergenceMultiplier = 5;

    // === Dynamic Period Driver (Shared Adaptive Range) ===
    public const int DynamicPeriodMinDefault = 2;
    public const int DynamicPeriodMaxDefault = 200;

    // === Shared Colors ===
    public static readonly IndicatorColor Gray = new(255, 128, 128, 128);
    public static readonly IndicatorColor Purple = new(255, 171, 71, 188); // #AB47BC
    public static readonly IndicatorColor Cyan = new(255, 0, 172, 193); // #00ACC1
    public static readonly IndicatorColor Blue = new(255, 41, 98, 255); // #2962FF
    public static readonly IndicatorColor DeepPurple = new(255, 126, 87, 194); // #7E57C2
    public static readonly IndicatorColor LightBlue = new(255, 33, 150, 243); // #2196F3
    public static readonly IndicatorColor Orange = new(255, 255, 152, 0); // #FF9800
    public static readonly IndicatorColor DodgerBlue = new(255, 30, 144, 255); // #1E90FF

    // === SMA ===
    public const int SmaPeriod = 20;
    public static readonly IndicatorColor SmaColor = new(255, 30, 144, 255); // DodgerBlue #1E90FF

    // === EMA ===
    public const int EmaPeriod = 20;
    public static readonly IndicatorColor EmaColor = new(255, 30, 144, 255); // DodgerBlue #1E90FF

    // === KAMA (Kaufman Adaptive Moving Average) ===
    public const int KamaPeriod = 10;
    public const int KamaFastPeriod = 2;
    public const int KamaSlowPeriod = 30;
    public static readonly IndicatorColor KamaColor = new(255, 30, 144, 255); // DodgerBlue #1E90FF

    // === AMA (Adaptive Moving Average) ===
    public const int AmaPeriod = 10;
    public const int AmaFastPeriod = 2;
    public const int AmaSlowPeriod = 30;
    public static readonly IndicatorColor AmaColor = new(255, 0, 191, 255); // DeepSkyBlue #00BFFF

    // === VIDYA (Variable Index Dynamic Average) ===
    public const int VidyaSmoothPeriod = 9;
    public const int VidyaCmoPeriod = 9;
    public static readonly IndicatorColor VidyaColor = new(255, 0, 188, 212); // Cyan #00BCD4

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
    public const int DtwDefaultWarpingRadius = 3;
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

    // === Prime Number Bands ===
    public const int PrimeNumberBandsPeriod = 8;
    public const decimal PrimeNumberBandsScaleMultiplier = 10.0m;
    public static readonly IndicatorColor PrimeNumberBandsMiddleColor = new(255, 41, 98, 255); // Blue #2962FF (Middle Band)
    public static readonly IndicatorColor PrimeNumberBandsBandsColor = new(255, 126, 87, 194);  // DeepPurple #7E57C2 (Bands: Upper / Lower)

    // === FFT Cycle ===
    public const int FftCycleDefaultWindowSize = 64;
    public static readonly IndicatorColor FftCycleColor = new(255, 41, 98, 255);           // Blue #2962FF (Dominant Cycle Period)
    public static readonly IndicatorColor FftCycleStrengthColor = new(255, 255, 167, 38);  // Amber #FFA726 (Cycle Strength)
    public static readonly IndicatorColor FftCycleOscillatorColor = new(255, 171, 71, 188); // Purple #AB47BC (Oscillator)

    // === Fourier Transform ===
    public const int FourierTransformDefaultTargetPeriod = 20;

    // === FFT Trend Filter ===
    public const int FftTrendFilterDefaultWindowSize = 64;
    public const int FftTrendFilterDefaultNumHarmonics = 4;

    // === IFFT Instantaneous Phase (Analytic Signal) ===
    public const int IfftInstantaneousPhaseDefaultWindowSize = 64;
    public static readonly IndicatorColor IfftInstantaneousPhaseColor = new(255, 38, 166, 154);        // Teal #26A69A (Instantaneous Phase, deg)
    public static readonly IndicatorColor IfftInstantaneousPhaseSineColor = new(255, 41, 98, 255);     // Blue #2962FF (Sine Wave)
    public static readonly IndicatorColor IfftInstantaneousPhaseLeadSineColor = new(255, 255, 112, 67); // DeepOrange #FF7043 (Lead Sine)
    // SineWave/LeadSine are sin()-derived and mathematically bounded to exactly [-1, +1];
    // fix the sub-panel Y axis to this range instead of auto-scaling from visible data.
    public const decimal IfftInstantaneousPhaseSineMinValue = -1.0m;
    public const decimal IfftInstantaneousPhaseSineMaxValue = 1.0m;
    // Prior behavior hardcoded 45 degrees (a quarter turn); kept as the default so existing
    // charts/screener values are unchanged unless the user opts into a different angle.
    public const double IfftInstantaneousPhaseDefaultLeadSineShiftDegrees = 45.0;

    // === IFFT Instantaneous Amplitude (Analytic Signal, overlay line) ===
    public const int IfftInstantaneousAmplitudeDefaultWindowSize = 64;
    public static readonly IndicatorColor IfftInstantaneousAmplitudeColor = new(255, 120, 144, 156); // BlueGrey #78909C (Instantaneous Amplitude line, overlay)

    // === IFFT Band-Pass Filter (auto-tuning dominant-cycle reconstruction, overlay line) ===
    public const int IfftBandPassFilterDefaultWindowSize = 64;
    public const int IfftBandPassFilterDefaultBandWidthBins = 2;
    public static readonly IndicatorColor IfftBandPassFilterColor = new(255, 92, 107, 192); // Indigo #5C6BC0 (Band-Pass Filter line, overlay)

    // === Adaptive RSI ===
    public const int AdaptiveRsiDefaultWindowSize = 64;
    public const int AdaptiveRsiDefaultPeriod = 14;
    public const int AdaptiveRsiMinPeriod = 5;
    public const int AdaptiveRsiMaxPeriod = 50;
    public static readonly IndicatorColor AdaptiveRsiColor = new(255, 171, 71, 188); // Purple #AB47BC
    public static readonly IndicatorColor AdaptiveRsiDominantPeriodColor = new(255, 41, 98, 255); // Blue #2962FF

    // === BandWidth (Bollinger %B width oscillator) ===
    public const int BandWidthPeriod = 20;
    public const decimal BandWidthStdDevMultiplier = 2.0m;

    // === Bollinger Bands Ratio ===
    public const int BollingerBandsRatioPeriod = 20;
    public const decimal BollingerBandsRatioStdDevMultiplier = 2.0m;

    // === Price Oscillator ===
    public const int PriceOscillatorFastPeriod = 10;
    public const int PriceOscillatorSlowPeriod = 20;

    // === Value at Risk / Conditional VaR ===
    public const int VarPeriod = 20;
    public const double VarConfidenceLevel = 0.95;

    // === Hilbert Transform Dominant Cycle ===
    public const int HilbertTransformDefaultPeriod = 14;
    public const int HilbertTransformMinPeriod = 6;
    public const int HilbertTransformMaxPeriod = 50;
    public const int HilbertTransformWarmupBars = 50;
    public const decimal HilbertTransformDefaultSmoothBeta = 0.1m;
    public const decimal HilbertTransformDefaultDeltaLimit = 3.0m;
    public const double HilbertTrendModeDefaultStabilityThreshold = 15.0;
    public const decimal HilbertSineMinValue = -1.0m;
    public const decimal HilbertSineMaxValue = 1.0m;
    public const decimal HilbertTrendModeMinValue = 0.0m;
    public const decimal HilbertTrendModeMaxValue = 1.0m;
    public const double HilbertSineDefaultLeadPhaseRadians = Math.PI / 4.0;
    public static readonly IndicatorColor HilbertTransformColor = new(255, 41, 98, 255); // Blue #2962FF (Dominant Cycle Period)
    public static readonly IndicatorColor HilbertTransformInPhaseColor = new(255, 38, 166, 154); // Green #26A69A (In-Phase)
    public static readonly IndicatorColor HilbertTransformQuadratureColor = new(255, 239, 83, 80); // Red #EF5350 (Quadrature)
    public static readonly IndicatorColor HilbertSineColor = new(255, 41, 98, 255); // Blue #2962FF (Sine)
    public static readonly IndicatorColor HilbertLeadSineColor = new(255, 255, 109, 0); // Orange #FF6D00 (Lead Sine)
    public static readonly IndicatorColor HilbertTrendlineColor = new(255, 255, 82, 82); // Red #FF5252 (Instantaneous Trendline)
    public static readonly IndicatorColor HilbertTrendModeColor = new(255, 38, 166, 154); // Green #26A69A (Trend vs Cycle Mode)

    // === Hidden Markov Model (HMM) ===
    public const int HmmStates = 2;
    public const int HmmPeriod = 100;
    public const int HmmMaxIterations = 30;
    public const double HmmTolerance = 1e-4;
    public const decimal HmmMinValue = 0;
    public const decimal HmmMaxValue = 100;
    public static readonly IndicatorColor HmmColor = new(255, 171, 71, 188); // Purple #AB47BC

    // === Correlation ===
    public const int CorrelationPeriod = 20;
    public const decimal CorrelationMinValue = -1.0m;
    public const decimal CorrelationMaxValue = 1.0m;
    public static readonly IndicatorColor CorrelationColor = new(255, 171, 71, 188); // Purple #AB47BC (matches RsiColor)

    // === Fréchet Distance Oscillator ===
    public const int FrechetOscillatorDefaultPeriod = 20;
    public const int FrechetOscillatorDefaultLag = 10;
    public static readonly IndicatorColor FrechetOscillatorColor = new(255, 171, 71, 188); // Purple #AB47BC

    // === Singular Spectrum Analysis (SSA) ===
    public const int SsaDefaultWindowSize = 64;
    public const int SsaDefaultEmbeddingDimension = 20;
    public const int SsaDefaultNumComponents = 2;
    public static readonly IndicatorColor SsaColor = new(255, 30, 144, 255); // DodgerBlue #1E90FF (Matches SMA)

    // === SSA Residual Volatility Band ===
    public const int SsaResidualBandDefaultWindowSize = 64;
    public const int SsaResidualBandDefaultEmbeddingDimension = 20;
    public const int SsaResidualBandDefaultNumComponents = 2;
    public const decimal SsaResidualBandDefaultMultiplier = 2.0m;
    public static readonly IndicatorColor SsaResidualBandCenterColor = new(255, 30, 144, 255); // DodgerBlue #1E90FF
    public static readonly IndicatorColor SsaResidualBandBandsColor = new(255, 126, 87, 194);   // DeepPurple #7E57C2

    // === SSA Dominant Cycle Extractor ===
    public const int SsaCycleDefaultWindowSize = 64;
    public const int SsaCycleDefaultEmbeddingDimension = 20;
    public const double SsaCycleDefaultDeltaPair = 0.25;
    public static readonly IndicatorColor SsaCycleColor = new(255, 41, 98, 255); // Blue #2962FF
    public static readonly IndicatorColor SsaCycleInPhaseColor = new(255, 38, 166, 154); // Green #26A69A
    public static readonly IndicatorColor SsaCycleQuadratureColor = new(255, 239, 83, 80); // Red #EF5350
    public static readonly IndicatorColor SsaCyclePhaseColor = new(255, 171, 71, 188); // Purple #AB47BC

    // === SSA Entropy ===
    public const int SsaEntropyDefaultWindowSize = 64;
    public const int SsaEntropyDefaultEmbeddingDimension = 20;
    public static readonly IndicatorColor SsaEntropyColor = new(255, 171, 71, 188); // Purple #AB47BC
    public const decimal SsaEntropyMinValue = 0.0m;
    public const decimal SsaEntropyMaxValue = 1.0m;

    // === SSA Squeeze ===
    public const int SsaSqueezeDefaultWindowSize = 64;
    public const int SsaSqueezeDefaultEmbeddingDimension = 20;
    public const int SsaSqueezeDefaultNumComponents = 2;
    public const decimal SsaSqueezeDefaultSsaMultiplier = 2.0m;
    public const int SsaSqueezeDefaultAtrPeriod = 20;
    public const decimal SsaSqueezeDefaultAtrMultiplier = 1.5m;
    public const int SsaSqueezeDefaultMomentumPeriod = 12;
    public const decimal SsaSqueezeDefaultSqueezeThreshold = 1.0m;
    public static readonly IndicatorColor SsaSqueezeMomentumUpColor = new(255, 0, 230, 118);      // Bright Green/Lime #00E676
    public static readonly IndicatorColor SsaSqueezeMomentumUpDecayColor = new(255, 46, 125, 50);  // Dark Green #2E7D32
    public static readonly IndicatorColor SsaSqueezeMomentumDownColor = new(255, 255, 23, 68);     // Bright Red #FF1744
    public static readonly IndicatorColor SsaSqueezeMomentumDownDecayColor = new(255, 255, 214, 0); // Yellow/Gold #FFD600
    public static readonly IndicatorColor SsaSqueezeOnColor = new(255, 255, 23, 68);              // Red (Squeeze ON)
    public static readonly IndicatorColor SsaSqueezeOffColor = new(255, 0, 230, 118);             // Green (Squeeze OFF)

    // === SSA SNR ===
    public const int SsaSnrDefaultWindowSize = 64;
    public const int SsaSnrDefaultEmbeddingDimension = 20;
    public const int SsaSnrDefaultNumComponents = 2;
    public const decimal SsaSnrDefaultThresholdHigh = 10.0m;
    public const decimal SsaSnrDefaultThresholdLow = 3.0m;
    public static readonly IndicatorColor SsaSnrColor = new(255, 33, 150, 243);         // LightBlue #2196F3
    public static readonly IndicatorColor SsaSnrPurityColor = new(255, 171, 71, 188);   // Purple #AB47BC
    public static readonly IndicatorColor SsaSnrThresholdHighColor = new(255, 0, 230, 118); // Green
    public static readonly IndicatorColor SsaSnrThresholdLowColor = new(255, 255, 23, 68);    // Red

    // === SSA Anomaly ===
    public const int SsaAnomalyDefaultWindowSize = 40;
    public const int SsaAnomalyDefaultEmbeddingDimension = 15;
    public const int SsaAnomalyDefaultNumComponents = 2;
    public const bool SsaAnomalyDefaultAutoRank = true;
    public const decimal SsaAnomalyDefaultEnterThreshold = 2.0m;
    public const decimal SsaAnomalyDefaultExitThreshold = 1.0m;
    public const int SsaAnomalyDefaultCoolDownPeriod = 3;
    public const int SsaAnomalyDefaultMinDuration = 2;
    public static readonly IndicatorColor SsaAnomalyZScoreColor = new(255, 33, 150, 243);
    public static readonly IndicatorColor SsaAnomalyBullishColor = new(255, 38, 166, 154);
    public static readonly IndicatorColor SsaAnomalyBearishColor = new(255, 239, 83, 80);
    public static readonly IndicatorColor SsaAnomalyThresholdColor = new(255, 189, 189, 189);

    // === Autoregressive Integrated Moving Average (ARIMA) ===
    public const int ArimaDefaultP = 1;
    public const int ArimaDefaultD = 1;
    public const int ArimaDefaultQ = 1;
    public const int ArimaDefaultPeriod = 30;
    public static readonly IndicatorColor ArimaColor = new(255, 30, 144, 255); // DodgerBlue #1E90FF
}

