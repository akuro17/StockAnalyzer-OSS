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

    // === Rolling VWAP ===
    public const int RollingVwapPeriod = 20;
    public static readonly IndicatorColor RollingVwapColor = new(255, 0, 172, 193); // Cyan #00ACC1

    // === Bollinger Bands ===
    public const int BollingerPeriod = 20;
    public const decimal BollingerStdDevMultiplier = 2.0m;
    public static readonly IndicatorColor BollingerMiddleColor = new(255, 41, 98, 255); // Blue #2962FF
    public static readonly IndicatorColor BollingerBandsColor = new(255, 126, 87, 194);  // Purple #7E57C2

    // === Keltner Channel: ATR multiplier range (the periods use DefaultPeriodMin / DefaultPeriodMax); double because an attribute argument cannot be a decimal constant ===
    public const double KeltnerMinMultiplier = 0.1;
    public const double KeltnerMaxMultiplier = 10.0;

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
    /// <summary>Bounds of the row count of the Volume Profile, Time at Price and Volume Profile Levels parameters (one definition for all three).</summary>
    public const int VolumeProfileMinRowCount = 5;
    public const int VolumeProfileMaxRowCount = 200;
    public const double VolumeProfileOpacity = 0.3;
    public static readonly IndicatorColor VolumeProfileColor = new(255, 128, 128, 128); // Gray
    public static readonly IndicatorColor VolumeProfileValueAreaColor = new(255, 255, 235, 59); // #FFEB3B (Yellow)
    /// <summary>Default colour of the Point of Control bin (Volume Profile, Time at Price; indicators and drawing tools). The single definition of #FF0000 for these features.</summary>
    public static readonly IndicatorColor VolumeProfilePocColor = new(255, 255, 0, 0); // #FF0000 (Red)

    // === MESA ===
    public static readonly IndicatorColor MesaBaseColor = new(255, 41, 98, 255); // Blue #2962FF
    public static readonly IndicatorColor MesaMamaColor = new(255, 41, 98, 255); // Blue #2962FF
    public static readonly IndicatorColor MesaFamaColor = new(255, 126, 87, 194);   // Purple #7E57C2

    // === Structural DTW ===
    public const int DtwDefaultWarpingRadius = 3;
    // Structural DTW (native port of the former Python handler; values kept identical to it)
    public const int StructuralDtwMinCandles = 60;                       // shortest history the analysis accepts
    public const int StructuralDtwMesaWindow = 32;                       // last bars used for the Burg cycle estimate (capped at n / 2)
    public const int StructuralDtwMesaOrder = 10;                        // Burg (maximum entropy) model order
    public const int StructuralDtwMesaResponsePoints = 256;              // frequency-response grid over [0, pi)
    public const double StructuralDtwMesaMinFrequencyFraction = 0.02;    // grid fraction skipped at the low-frequency end when searching the peak
    public const int StructuralDtwMinDominantPeriod = 10;                // dominant period lower clamp (bars)
    public const int StructuralDtwMaxDominantPeriodDivisor = 3;          // dominant period upper clamp = n / divisor
    public const int StructuralDtwFallbackPeriod = 20;                   // period when the spectrum has no positive peak frequency
    public const int StructuralDtwStepDivisor = 4;                       // candidate stride = window / divisor (at least 1)
    public const double StructuralDtwSigmaFloor = 1e-10;                 // population sigma at or below which a segment is flat
    public const double StructuralDtwVolatilityFloor = 1e-10;            // denominator floor of the volatility ratio
    public const double StructuralDtwLogPriceEpsilon = 1e-10;            // added to prices before the log of the fallback volatility
    public const double StructuralDtwVolatilityPenaltyWeight = 0.1;      // weight of the log volatility-ratio penalty on the RMSE-scale distance
    public const int StructuralDtwFallbackVolatilityWindow = 20;         // rolling-std window of the fallback volatility (capped at n / divisor)
    public const int StructuralDtwFallbackVolatilityWindowDivisor = 4;
    public const int StructuralDtwResultDecimals = 4;                    // rounding of distances, probabilities, volatility and future path
    public const double StructuralDtwPercentFactor = 100.0;              // ratio -> percent
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

    // === KST (Know Sure Thing) Signal line ===
    public const int KstSignalPeriod = 9;
    public const int KstMinSignalPeriod = 1;
    public const int KstMaxSignalPeriod = 1000;

    // === VWAP (reset period, volume-weighted standard deviation bands) ===
    public const decimal VwapStdDevMultiplier = 2.0m;
    public const decimal VwapMinStdDevMultiplierDecimal = 0.1m;
    public const decimal VwapMaxStdDevMultiplierDecimal = 10.0m;
    // The double forms exist for attribute arguments ([Range], [CoreParameterRange]); they are derived from the decimal definition, so the band has one source.
    public const double VwapMinStdDevMultiplier = (double)VwapMinStdDevMultiplierDecimal;
    public const double VwapMaxStdDevMultiplier = (double)VwapMaxStdDevMultiplierDecimal;

    // === Anchored VWAP (date anchor) ===
    public const string AnchoredVwapDateFormat = "yyyy-MM-dd";

    // === Double to decimal conversion of ln / sqrt results (rule G4): the largest magnitude that surely fits a decimal (7.9228e28) with a one-digit margin ===
    public const double MaxDoubleToDecimalMagnitude = 7.9e27;

    // === Annualisation of per-bar volatility (opt-in: Annualize default false, so the default output does not depend on the chart time frame) ===
    public const int PeriodsPerYear = 252;
    public const int MinPeriodsPerYear = 1;
    public const int MaxPeriodsPerYear = 366;

    // === Garman-Klass Volatility (opt-in aggregation period) ===
    public const int GarmanKlassPeriod = 1;
    public const int GarmanKlassMinPeriod = 1;
    public const int GarmanKlassMaxPeriod = 1000;

    // === Range-based volatility estimators (Parkinson, Rogers-Satchell); unit: percentage per bar (annual percentage when Annualize is on) ===
    public const int RangeVolatilityDefaultPeriod = 20;
    public const int RangeVolatilityMinPeriod = 2;
    public const int RangeVolatilityMaxPeriod = 1000;

    // === Prime Number Bands ===
    public const int PrimeNumberBandsPeriod = 8;
    public const decimal PrimeNumberBandsScaleMultiplier = 10.0m;
    public static readonly IndicatorColor PrimeNumberBandsMiddleColor = new(255, 41, 98, 255); // Blue #2962FF (Middle Band)
    public static readonly IndicatorColor PrimeNumberBandsBandsColor = new(255, 126, 87, 194);  // DeepPurple #7E57C2 (Bands: Upper / Lower)

    // === FFT Cycle ===
    public const int FftCycleDefaultWindowSize = 64;
    public const int FftCycleMinWindowSize = 4;          // smallest FFT window with a dominant bin beyond k=1
    public const int FftCycleMaxWindowSize = 512;
    public static readonly IndicatorColor FftCycleColor = new(255, 41, 98, 255);           // Blue #2962FF (Dominant Cycle Period)
    public static readonly IndicatorColor FftCycleStrengthColor = new(255, 255, 167, 38);  // Amber #FFA726 (Cycle Strength)
    public static readonly IndicatorColor FftCycleOscillatorColor = new(255, 171, 71, 188); // Purple #AB47BC (Oscillator)

    // === EGARCH (causal rolling QMLE; see docs/indicators_formulas.md) ===
    public const int EgarchDefaultOrder = 1;
    public const int EgarchMinOrder = 1;
    public const int EgarchMaxOrder = 3;
    public const int EgarchDefaultPeriod = 250;          // bars per estimation window; 5+ parameters need far more than a few dozen observations
    public const int EgarchMinPeriod = 50;
    public const int EgarchMaxPeriod = 2000;
    public const int EgarchDefaultRefitInterval = 20;    // bars between re-estimations
    public const int EgarchMinRefitInterval = 1;
    public const int EgarchMaxRefitInterval = 250;
    public const int EgarchDefaultMaxIterations = 1000;  // optimizer iterations per run
    public const int EgarchMinMaxIterations = 50;
    public const int EgarchMaxMaxIterations = 20000;
    // Plausibility band of the conditional sigma, as a multiple of the estimation window's root mean square return (survey of 40,883 blocks: see docs/indicators_formulas.md)
    public const double EgarchDefaultMaxSigmaRatio = 50.0;      // above the 99.9th percentile (13.9) of blocks that never touch a variance guard
    public const double EgarchMinMaxSigmaRatio = 2.0;
    public const double EgarchMaxMaxSigmaRatio = 100000.0;      // effectively off
    public const double EgarchDefaultMinSigmaRatio = 0.01;      // below it every surveyed block had alpha < 0 (collapse family)
    public const double EgarchMinMinSigmaRatio = 0.0;           // 0 = off
    public const double EgarchMaxMinSigmaRatio = 0.5;

    // === GARCH(1,1) (rolling QMLE with variance targeting; unit: percentage per bar, annual percentage when Annualize is on) ===
    // The estimation-window, refit and optimizer bounds are the EGARCH ones on purpose (one definition of "rolling conditional-variance estimation"); GARCH(1,1) has 2 free coefficients, EGARCH 5+.
    public const int Garch11DefaultPeriod = EgarchDefaultPeriod;
    public const int Garch11MinPeriod = EgarchMinPeriod;
    public const int Garch11MaxPeriod = EgarchMaxPeriod;
    public const int Garch11DefaultRefitInterval = EgarchDefaultRefitInterval;
    public const int Garch11MinRefitInterval = EgarchMinRefitInterval;
    public const int Garch11MaxRefitInterval = EgarchMaxRefitInterval;
    public const int Garch11DefaultMaxIterations = EgarchDefaultMaxIterations;
    public const int Garch11MinMaxIterations = EgarchMinMaxIterations;
    public const int Garch11MaxMaxIterations = EgarchMaxMaxIterations;
    public const double GjrGarchInitialAlpha = 0.05;
    public const double GjrGarchInitialBeta = 0.85;
    public const double GjrGarchInitialGamma = 0.10;
    public const double GjrGarchMinAlpha = 1e-6;
    public const double GjrGarchMinBeta = 1e-6;
    public const double GjrGarchMinGamma = 0.0;
    public const double GjrGarchMaxPersistence = 0.9999;
    public const double GjrGarchMinOmega = 1e-12;

    // === Fixed 0..1 axis shared by the bounded ratio indicators (Hurst, Entropy, SSAEntropy) ===
    public const decimal UnitIntervalMinValue = 0.0m;
    public const decimal UnitIntervalMaxValue = 1.0m;

    // === Percentage scale of the volatility estimators (a fraction x 100 = percent) ===
    public const double PercentScale = 100.0;

    // === Hurst exponent (R/S analysis, regression over halving sub-interval lengths; unit: ratio, dimensionless in [0, 1]) ===
    public const int HurstDefaultPeriod = 100;           // log returns in the window
    public const int HurstMinPeriod = 32;                // 32 gives the sub-interval lengths 32, 16, 8: at least three points for the regression
    public const int HurstMaxPeriod = 2000;
    public const int HurstDefaultNeweyWestLag = 0;       // 0 = automatic Newey-West bandwidth selection
    public const int HurstMinNeweyWestLag = 0;
    public const int HurstMaxNeweyWestLag = 100;
    public const int HurstDefaultVarianceRatioLag = 5;
    public const int HurstMinVarianceRatioLag = 2;
    public const int HurstMaxVarianceRatioLag = 50;
    public const decimal HurstMinValue = UnitIntervalMinValue;
    public const decimal HurstMaxValue = UnitIntervalMaxValue;
    public static readonly IndicatorColor HurstModifiedColor = new(255, 0, 114, 178); // Okabe-Ito Blue #0072B2

    // === Statistical tests of a window of returns (statistical functions, docs/indicators_formulas.md, records indicators_formulas_0596 and indicators_formulas_0597) ===
    // Significance level of the p-value tests (Jarque-Bera, Ljung-Box): the constant reference line is drawn at this level.
    public const double TestSignificanceDefault = 0.05;
    public const double TestSignificanceMin = 0.001;
    public const double TestSignificanceMax = 0.20;

    // Skewness / Excess Kurtosis (unit: dimensionless; sample-adjusted G1 and G2 of the last Period returns)
    public const int SkewnessKurtosisDefaultPeriod = 30;
    public const int SkewnessKurtosisMinPeriod = 10;
    public const int SkewnessKurtosisMaxPeriod = 500;
    public const bool SkewnessKurtosisDefaultUseLogReturn = true;
    public static readonly IndicatorColor SkewnessKurtosisKurtosisColor = new(255, 230, 159, 0); // Okabe-Ito Orange #E69F00

    // Jarque-Bera normality test (unit: p-value in [0, 1] on a fixed 0..1 axis; the statistic itself is chart-suppressed)
    public const int JarqueBeraDefaultPeriod = 60;
    public const int JarqueBeraMinPeriod = 10;
    public const int JarqueBeraMaxPeriod = 1000;
    public const bool JarqueBeraDefaultUseLogReturn = true;

    // Ljung-Box autocorrelation test on log returns (unit: p-value in [0, 1] on a fixed 0..1 axis; the Q statistic is chart-suppressed)
    public const int LjungBoxDefaultPeriod = 60;
    public const int LjungBoxMinPeriod = 20;
    public const int LjungBoxMaxPeriod = 500;
    public const int LjungBoxDefaultMaxLag = 10;
    public const int LjungBoxMinMaxLag = 1;
    public const int LjungBoxMaxMaxLag = 30;

    // The reference line of a test panel (significance level of a p-value test, critical value of the ADF statistic, random-walk level of the variance ratio).
    public static readonly IndicatorColor TestSignificanceLineColor = Gray;

    // ADF Stationarity (augmented Dickey-Fuller tau with a constant on the last Period prices; unit: dimensionless statistic, the 5 % critical value is the reference line)
    public const int AdfStationarityDefaultPeriod = 60;
    public const int AdfStationarityMinPeriod = 20;
    public const int AdfStationarityMaxPeriod = 500;
    public const int AdfStationarityDefaultLagOrder = 1;
    public const int AdfStationarityMinLagOrder = 0;
    public const int AdfStationarityMaxLagOrder = StockAnalyzer.Core.MathUtils.StationarityTestMath.MaxLagOrder; // the capacity of the kernel
    public const bool AdfStationarityDefaultUseLogPrice = true;

    // Variance Ratio (Lo-MacKinlay VR(q) of the last Period log returns; unit: dimensionless, 1 = random walk, drawn as a constant reference line; the Z* statistic and its p-value are chart-suppressed)
    public const int VarianceRatioDefaultPeriod = 60;
    public const int VarianceRatioMinPeriod = 20;
    public const int VarianceRatioMaxPeriod = 500;
    public const int VarianceRatioDefaultAggregationHorizon = 5;
    public const int VarianceRatioMinAggregationHorizon = 2;
    public const int VarianceRatioMaxAggregationHorizon = 30;
    public const decimal VarianceRatioRandomWalkLevel = 1m;

    // Quantile Bands (type 7 quantiles of the last Period prices, unit: price; the median is the middle line)
    public const int QuantileBandsDefaultPeriod = 20;
    public const int QuantileBandsMinPeriod = 5;
    public const int QuantileBandsMaxPeriod = 500;
    public const double QuantileBandsDefaultUpperQuantile = 0.75;
    public const double QuantileBandsMinUpperQuantile = 0.51;
    public const double QuantileBandsMaxUpperQuantile = 0.99;
    public const double QuantileBandsDefaultLowerQuantile = 0.25;
    public const double QuantileBandsMinLowerQuantile = 0.01;
    public const double QuantileBandsMaxLowerQuantile = 0.49;

    // Theil-Sen / Siegel regression (robust line of the last Period prices, unit: price). The indicator fits EVERY bar, so its window limit is an independent, measured bound (cost per series is
    // bars x window^2): owner decision 2026-11-27 (DI-1 = A2) = 5,000 bars within about 3 s in Debug and Release for both methods (measured table in the step log of the drawing tool follow-up).
    // It is smaller than the hard cap of the kernel overload (one window at a time, the drawing tool) and larger than the 30 bar bound of the MCP trendBars, the DSL functions and the flag rails.
    public const int TheilSenDefaultPeriod = 20;
    public const int TheilSenMinPeriod = 5;
    public const int TheilSenMaxPeriod = 70;
    public const double TheilSenDefaultBandMultiplier = 2.0;
    public const double TheilSenMinBandMultiplier = 0.0;
    public const double TheilSenMaxBandMultiplier = 10.0;
    public const bool TheilSenDefaultUseSiegelMedian = false;

    // Cornish-Fisher VaR (one-bar lower-tail return quantile of the last Period simple returns; unit: ratio, a loss is negative; the confidence range is the one of the kernel)
    public const int CornishFisherVarDefaultPeriod = 30;
    public const int CornishFisherVarMinPeriod = 10;
    public const int CornishFisherVarMaxPeriod = 500;
    public const double CornishFisherVarDefaultConfidenceLevel = 0.95;
    public const double CornishFisherVarMinConfidenceLevel = StockAnalyzer.Core.MathUtils.NormalityAndTailMath.MinConfidenceLevel;
    public const double CornishFisherVarMaxConfidenceLevel = StockAnalyzer.Core.MathUtils.NormalityAndTailMath.MaxConfidenceLevel;
    public static readonly IndicatorColor CornishFisherGaussianColor = new(255, 204, 121, 167); // Okabe-Ito Reddish purple #CC79A7

    // KPSS level-stationarity statistic (DSL expansion run I-1; unit: dimensionless statistic >= 0, the higher the stronger the evidence against stationarity).
    // The 5 % level equals ArimaMath's private KpssCriticalValue5Percent (a test pins the equality); the 1 % level is the standard Kwiatkowski et al. (1992) value for the level test.
    public const int KpssStationarityDefaultPeriod = 60;
    public const int KpssStationarityMinPeriod = 8; // the kernel returns 0.0 below 8 samples (ArimaMath.KpssMinSampleLength)
    public const int KpssStationarityMaxPeriod = 500;
    public const bool KpssStationarityDefaultUseReturns = false;
    public const decimal KpssCriticalValue5Percent = 0.463m;
    public const decimal KpssCriticalValue1Percent = 0.739m;
    public static readonly IndicatorColor KpssCriticalValue1PercentColor = new(255, 213, 94, 0); // Okabe-Ito Vermillion #D55E00

    // Risk ratios of a window of returns / prices (DSL expansion runs I-1 and I-2). Annualisation is opt-in (default off), like the volatility family: the per-bar value does not
    // depend on the chart time frame; PeriodsPerYear (shared constants above) is also the divisor that turns the annual risk-free rate into a per-bar rate.
    public const bool RiskRatioAnnualizeDefault = false;
    public const double UnannualizedPeriodsPerYear = 1.0; // the kernels' periodsPerYear argument when Annualize is off: no scaling
    public const double RiskRatioMinAnnualRiskFreeRate = -0.5;
    public const double RiskRatioMaxAnnualRiskFreeRate = 1.0;
    public const decimal RiskRatioDefaultAnnualRiskFreeRate = 0m;

    // Rolling Sharpe ratio (unit: dimensionless ratio, per bar or annualised by sqrt(PeriodsPerYear); sample standard deviation, divisor n - 1)
    public const int RollingSharpeDefaultPeriod = 20;
    public const int RollingSharpeMinPeriod = 2;
    public const int RollingSharpeMaxPeriod = 500;
    public const bool RollingSharpeDefaultUseLogReturn = false;
    public static readonly IndicatorColor RollingSharpeColor = new(255, 0, 114, 178); // Okabe-Ito Blue #0072B2

    // Rolling maximum drawdown (unit: positive ratio in [0, 1] of the window peak, or percent when AsPercent is on)
    public const int RollingMaxDrawdownDefaultPeriod = 60;
    public const int RollingMaxDrawdownMinPeriod = 2;
    public const int RollingMaxDrawdownMaxPeriod = 500;
    public const bool RollingMaxDrawdownDefaultAsPercent = true;
    public static readonly IndicatorColor RollingMaxDrawdownColor = new(255, 213, 94, 0); // Okabe-Ito Vermillion #D55E00
    public const decimal PercentFactor = 100m;

    // Two-symbol statistics of the returns of the chart symbol (a) and a comparison symbol (b) over the last Period returns (DSL expansion run I-2). A window needs Period valid pairs.
    // Treynor ratio (unit: return per unit of beta; linear annualisation by PeriodsPerYear)
    public const int TreynorRatioDefaultPeriod = 60;
    public const int TreynorRatioMinPeriod = 3;
    public const int TreynorRatioMaxPeriod = 500;
    public static readonly IndicatorColor TreynorRatioColor = new(255, 0, 158, 115); // Okabe-Ito Bluish green #009E73

    // Information ratio (unit: dimensionless, mean active return over tracking error; annualisation by sqrt(PeriodsPerYear))
    public const int InformationRatioDefaultPeriod = 60;
    public const int InformationRatioMinPeriod = 3;
    public const int InformationRatioMaxPeriod = 500;
    public static readonly IndicatorColor InformationRatioColor = new(255, 86, 180, 233); // Okabe-Ito Sky blue #56B4E9

    // Tracking error (unit: standard deviation of the active return, a fraction or percent; annualisation by sqrt(PeriodsPerYear))
    public const int TrackingErrorDefaultPeriod = 60;
    public const int TrackingErrorMinPeriod = 3;
    public const int TrackingErrorMaxPeriod = 500;
    public const bool TrackingErrorDefaultAsPercent = true;
    public static readonly IndicatorColor TrackingErrorColor = new(255, 230, 159, 0); // Okabe-Ito Orange #E69F00

    // Rolling covariance (unit: return squared, raw and unscaled; sample covariance divides by n - 1, population by n)
    public const int RollingCovarianceDefaultPeriod = 60;
    public const int RollingCovarianceMinPeriod = 2;
    public const int RollingCovarianceMaxPeriod = 500;
    public const bool RollingCovarianceDefaultUseSample = true;
    public static readonly IndicatorColor RollingCovarianceColor = new(255, 204, 121, 167); // Okabe-Ito Reddish purple #CC79A7

    // Price-native window averages (DSL expansion run I-3; unit: price, overlay on the price pane). The window holds the last Period prices of the price source.
    public const int WindowAverageDefaultPeriod = 20;
    public const int WindowAverageMinPeriod = 2;
    public const int WindowAverageMaxPeriod = 500;
    public const decimal TrimFractionDefaultAlpha = 0.10m;
    public const double TrimFractionMinAlpha = 0.0;
    public const double TrimFractionMaxAlpha = StockAnalyzer.Core.MathUtils.DslRobustStatisticsMath.MaxTrimAlpha; // the DSL kernels' limit, so both accept the same settings
    public const decimal HuberMaDefaultTuningK = 1.345m; // equals DslRobustStatisticsMath.DefaultHuberK (a test pins it)
    public const double HuberMaMinTuningK = 0.1;
    public const double HuberMaMaxTuningK = 10.0;
    public const decimal BiweightMaDefaultTuningC = 4.685m; // equals DslRobustStatisticsMath.DefaultBiweightC (a test pins it)
    public const double BiweightMaMinTuningC = 0.5;
    public const double BiweightMaMaxTuningC = 20.0;
    public static readonly IndicatorColor RobustMovingAverageColor = new(255, 0, 114, 178); // Okabe-Ito Blue #0072B2
    public static readonly IndicatorColor GeneralizedMovingAverageColor = new(255, 0, 158, 115); // Okabe-Ito Bluish green #009E73

    // Polynomial regression (DSL expansion run I-4; unit: price; the fitted polynomial of the last Period prices evaluated Horizon bars after the newest bar, drawn at the newest bar)
    public const int PolynomialRegressionDefaultPeriod = 30;
    public const int PolynomialRegressionMinPeriod = 4;
    public const int PolynomialRegressionMaxPeriod = 500;
    public const int PolynomialRegressionDefaultDegree = 2;
    public const int PolynomialRegressionMinDegree = 1;
    public const int PolynomialRegressionMaxDegree = StockAnalyzer.Core.MathUtils.DslRegressionMath.MaxPolyDegree;
    public const int PolynomialRegressionDefaultForecastHorizon = 0;
    public const int PolynomialRegressionMinForecastHorizon = 0;
    public const int PolynomialRegressionMaxForecastHorizon = StockAnalyzer.Core.MathUtils.DslRegressionMath.MaxPolyHorizon;
    public static readonly IndicatorColor PolynomialRegressionColor = new(255, 230, 159, 0); // Okabe-Ito Orange #E69F00

    // Nonlinear regression (DSL expansion run I-4; unit: price; x = 1 .. Period within the window, the fitted value is at x = Period = the newest bar)
    public const int NonlinearRegressionDefaultPeriod = 30;
    public const int NonlinearRegressionMinPeriod = 5;
    public const int NonlinearRegressionMaxPeriod = 500;
    public const double NonlinearLogisticEpsilon = 1e-4; // the logistic fit needs y in [0, 1]: the window is mapped to [eps, 1 - eps]
    public static readonly IndicatorColor NonlinearRegressionColor = new(255, 213, 94, 0); // Okabe-Ito Vermillion #D55E00

    // Tests of returns (DSL expansion run I-5). The recent window holds the returns of the bars [i - Period + 1, i]; the baseline window the BaselinePeriod returns right before it.
    // Statistics are dimensionless, p-values lie in [0, 1], the KL divergence is in nats. The first usable bar of a two-window test is Period + BaselinePeriod.
    public const int OneSampleTTestDefaultPeriod = 30;
    public const int OneSampleTTestMinPeriod = 3;
    public const int OneSampleTTestMaxPeriod = 500;
    public const bool TestDefaultUseLogReturn = false;
    public const decimal OneSampleTTestDefaultNullMean = 0m;
    public const double OneSampleTTestMinNullMean = -1.0; // a mean return per bar of -100 % .. +100 %
    public const double OneSampleTTestMaxNullMean = 1.0;
    public const int TwoSampleTestDefaultPeriod = 20;
    public const int TwoSampleTestDefaultBaselinePeriod = 60;
    public const int TwoSampleTTestMinPeriod = 3;
    public const int TwoSampleTTestMaxPeriod = 250;
    public const int TwoSampleTTestMinBaselinePeriod = 3;
    public const int TwoSampleTTestMaxBaselinePeriod = 500;
    // The Mann-Whitney kernel accepts n1 + n2 <= 512 (DslBivariateAndTestMath.MannWhitneyU), so its two ranges are narrower than those of the other two-sample tests (250 / 500, which
    // can sum to 750): 200 + 300 = 500 stays inside the kernel without a cross-field check. Do NOT unify the ranges with the other tests (pinned by a test).
    public const int MannWhitneyUMinPeriod = 3;
    public const int MannWhitneyUMaxPeriod = 200;
    public const int MannWhitneyUMinBaselinePeriod = 3;
    public const int MannWhitneyUMaxBaselinePeriod = 300;
    public const int KlDivergenceDefaultPeriod = 20;
    public const int KlDivergenceDefaultBaselinePeriod = 60;
    public const int KlDivergenceMinPeriod = 5;
    public const int KlDivergenceMaxPeriod = 250;
    public const int KlDivergenceMinBaselinePeriod = 5;
    public const int KlDivergenceMaxBaselinePeriod = 500;
    public const int KlDivergenceDefaultBinCount = 10;
    public const int KlDivergenceMinBinCount = 4;
    public const int KlDivergenceMaxBinCount = 64;
    public const bool KlDivergenceDefaultUseLogReturn = true;
    public static readonly IndicatorColor TestStatisticColor = new(255, 0, 114, 178); // Okabe-Ito Blue #0072B2

    // Downside Deviation (unit: ratio per bar; sigma- and sigma+ of the last Period simple returns against the target return)
    public const int DownsideDeviationDefaultPeriod = 20;
    public const int DownsideDeviationMinPeriod = 5;
    public const int DownsideDeviationMaxPeriod = 250;
    public const double DownsideDeviationDefaultTargetReturn = 0.0;
    public const double DownsideDeviationMinTargetReturn = -0.10;
    public const double DownsideDeviationMaxTargetReturn = 0.10;
    public static readonly IndicatorColor DownsideDeviationUpsideColor = new(255, 0, 158, 115); // Okabe-Ito Bluish green #009E73

    // Bipower Variation (unit: log-return standard deviation per bar, a ratio; the jump ratio J / RV is a dimensionless value in [0, 1])
    public const int BipowerVariationDefaultPeriod = 20;
    public const int BipowerVariationMinPeriod = 5;
    public const int BipowerVariationMaxPeriod = 250;
    public static readonly IndicatorColor BipowerVariationTotalColor = new(255, 86, 180, 233); // Okabe-Ito Sky blue #56B4E9
    public static readonly IndicatorColor BipowerVariationJumpColor = new(255, 213, 94, 0);    // Okabe-Ito Vermillion #D55E00

    // === Shannon entropy of the log returns (equal-width bins, normalised by ln(BinCount); unit: dimensionless in [0, 1]) ===
    public const int EntropyDefaultPeriod = 30;
    public const int EntropyMinPeriod = 5;
    public const int EntropyMaxPeriod = 1000;
    public const int EntropyDefaultBinCount = 10;
    public const int EntropyMinBinCount = 2;
    public const int EntropyMaxBinCount = 100;
    public const int EntropyDefaultEmbeddingDimension = 3;
    public const int EntropyMinEmbeddingDimension = 3;
    public const int EntropyMaxEmbeddingDimension = 5;
    public const int EntropyDefaultSampleDimension = 2;
    public const int EntropyMinSampleDimension = 1;
    public const int EntropyMaxSampleDimension = 3;
    public const double EntropyDefaultToleranceMultiplier = 0.2;
    public const double EntropyMinToleranceMultiplier = 0.05;
    public const double EntropyMaxToleranceMultiplier = 1.0;
    public const decimal EntropyMinValue = UnitIntervalMinValue;
    public const decimal EntropyMaxValue = UnitIntervalMaxValue;
    public static readonly IndicatorColor EntropyPermutationColor = new(255, 0, 158, 115); // Okabe-Ito Bluish Green #009E73

    // === Fourier Transform ===
    public const int FourierTransformDefaultTargetPeriod = 20;
    public const int FourierTransformMinTargetPeriod = 2;
    public const int FourierTransformMaxTargetPeriod = 500;

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

    // === Polar Coordinate Transform Decomposition (single analytic signal) ===
    // The analytic-signal source parameters are shared with the Hilbert Transform Dominant Cycle
    // block above (Ehlers / IIR fixed, Q1); only the ATR-normalization period is Polar-specific.
    public const int PolarAmplitudeRatioDefaultAtrPeriod = 14;
    public static readonly IndicatorColor PolarAmplitudeRatioColor = new(255, 255, 167, 38); // Amber #FFA726 (ATR-normalized amplitude ratio)
    // Polar Phase: the drawn phasor components (NormalizedInPhase / NormalizedQuadrature) are
    // mathematically bounded to exactly [-1, +1]; fix the sub-panel Y axis to that range.
    public static readonly IndicatorColor PolarPhaseColor = new(255, 38, 166, 154);              // Teal #26A69A (Phase, deg - screener only)
    public static readonly IndicatorColor PolarPhaseNormalizedInPhaseColor = new(255, 41, 98, 255);   // Blue #2962FF (Normalized In-Phase)
    public static readonly IndicatorColor PolarPhaseNormalizedQuadratureColor = new(255, 239, 83, 80); // Red #EF5350 (Normalized Quadrature)
    public const decimal PolarPhaseComponentMinValue = -1.0m;
    public const decimal PolarPhaseComponentMaxValue = 1.0m;
    // Polar Cycle Angular Frequency: omega = 2*pi / InstantaneousPeriod (rad/bar), Homodyne-period-derived.
    public static readonly IndicatorColor PolarCycleAngularFrequencyColor = new(255, 92, 107, 192); // Indigo #5C6BC0 (cycle angular frequency, rad/bar)
    // Diagnostic sub-series of Polar Cycle Angular Frequency (same rad/bar sub-panel):
    public static readonly IndicatorColor PolarPhaseAngularVelocityColor = new(255, 255, 167, 38);  // Amber #FFA726 (measured phase-difference angular velocity, rad/bar)
    public static readonly IndicatorColor PolarPhaseFrequencyErrorColor = new(255, 239, 83, 80);    // Red #EF5350 (|cycle rate - phase rate|, rad/bar)

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
    public const decimal SsaEntropyMinValue = UnitIntervalMinValue;
    public const decimal SsaEntropyMaxValue = UnitIntervalMaxValue;

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
    public const bool ArimaDefaultAutoOrderSelection = false;
    public const int ArimaDefaultMaxAutoP = 3;
    public const int ArimaDefaultMaxAutoQ = 3;
    public const int ArimaMinMaxAutoOrder = 1;
    public const int ArimaMaxMaxAutoOrder = 5;
    public const int ArimaDefaultOrderRefitInterval = 20;
    public const int ArimaMinOrderRefitInterval = 1;
    public const int ArimaMaxOrderRefitInterval = 100;
    public static readonly IndicatorColor ArimaColor = new(255, 30, 144, 255); // DodgerBlue #1E90FF

    // === ER (Kaufman Efficiency Ratio) ===
    public const int EfficiencyRatioPeriod = 10;
    public const decimal EfficiencyRatioMinValue = 0.0m;
    public const decimal EfficiencyRatioMaxValue = 1.0m;
    public static readonly IndicatorColor EfficiencyRatioColor = new(255, 30, 144, 255); // DodgerBlue #1E90FF
    public const decimal EfficiencyRatioNoiseThreshold = 0.30m;
    public const decimal EfficiencyRatioTrendThreshold = 0.60m;

    // === DMI (Directional Movement Index) ===
    public const int DmiDefaultPeriod = 14;
    public static readonly IndicatorColor DmiPlusDiColor = new(255, 38, 166, 154);   // Green #26A69A (+DI)
    public static readonly IndicatorColor DmiMinusDiColor = new(255, 239, 83, 80);  // Red #EF5350 (-DI)

    // === FDI (Fractal Dimension Index) ===
    public const int FdiPeriod = 30;
    public const int FdiSmoothingPeriod = 0;
    public const decimal FdiMinValue = 1.0m;
    public const decimal FdiMaxValue = 2.0m;
    public const decimal FdiNeutralThreshold = 1.5m;
    public const decimal FdiTrendThreshold = 1.4m;
    public const decimal FdiExtremeChopThreshold = 1.7m;
    public static readonly IndicatorColor FdiColor = new(255, 126, 87, 194); // DeepPurple #7E57C2

    // === Clothoid Oscillator ===
    public const int ClothoidOscillatorDefaultPeriod = 14;
    public const double ClothoidOscillatorDefaultDecayAlpha = 1.5;
    public const int ClothoidOscillatorDefaultSignalPeriod = 9;
    public const decimal ClothoidOscillatorMinValue = -100.0m;
    public const decimal ClothoidOscillatorMaxValue = 100.0m;
    public static readonly IndicatorColor ClothoidOscillatorColor = new(255, 30, 144, 255); // DodgerBlue
    public static readonly IndicatorColor ClothoidOscillatorSignalColor = new(255, 255, 152, 0); // Orange
    public static readonly IndicatorColor ClothoidOscillatorHistColor = new(255, 128, 128, 128); // Gray
    public static readonly IndicatorColor ClothoidOscillatorHistUpColor = new(255, 38, 166, 154); // Green #26A69A
    public static readonly IndicatorColor ClothoidOscillatorHistDownColor = new(255, 239, 83, 80); // Red #EF5350

    // === Clothoid Moving Average ===
    public const int ClothoidMovingAverageDefaultPeriod = 20;
    public const double ClothoidMovingAverageDefaultOffset = 0.85;
    public const double ClothoidMovingAverageDefaultSigma = 6.0;
    public static readonly IndicatorColor ClothoidMovingAverageColor = new(255, 30, 144, 255); // DodgerBlue

    // === Cross-Correlation Function (CCF) ===
    public const int CrossCorrelationPeriod = 20;
    public const int CrossCorrelationLag = 0;
    public const int CrossCorrelationMinLag = -10;
    public const int CrossCorrelationMaxLag = 10;
    public const int CrossCorrelationLagSmoothingPeriod = 3;
    public const double CrossCorrelationThreshold = 0.30;
    public const decimal CrossCorrelationMinValue = -1.0m;
    public const decimal CrossCorrelationMaxValue = 1.0m;
    public static readonly IndicatorColor CrossCorrelationColor = new(255, 32, 178, 170); // LightSeaGreen #20B2AA
    public static readonly IndicatorColor CrossCorrelationLagColor = new(255, 255, 167, 38); // Orange #FFA726

    // === Rolling Beta ===
    public const int RollingBetaPeriod = 60;
    public const int RollingBetaMinPeriod = 5;
    public static readonly IndicatorColor RollingBetaColor = new(255, 255, 112, 67); // DeepOrange #FF7043
    public static readonly IndicatorColor RollingBetaCorrelationColor = new(255, 171, 71, 188); // Purple #AB47BC

    // === Rolling Alpha ===
    public const int RollingAlphaPeriod = 60;
    public const int RollingAlphaMinPeriod = 5;
    public const bool RollingAlphaAnnualizeDefault = true;
    public const int RollingAlphaAnnualizationFactor = 252;
    public static readonly IndicatorColor RollingAlphaColor = new(255, 38, 166, 154); // Teal #26A69A

    // === Ratiocator ===
    public const int RatiocatorLookbackPeriod = 20;
    public const int RatiocatorSmoothingPeriod = 10;
    public static readonly IndicatorColor RatiocatorColor = new(255, 0, 150, 136); // Teal #009688
    public static readonly IndicatorColor RatiocatorSignalColor = new(255, 255, 167, 38); // Orange #FFA726

    // === Rolling R-Squared ===
    public const int RollingRSquaredPeriod = 60;
    public const int RollingRSquaredMinPeriod = 5;
    public static readonly IndicatorColor RollingRSquaredColor = new(255, 171, 71, 188); // Purple #AB47BC
    public const decimal RollingRSquaredMinValue = 0m;
    public const decimal RollingRSquaredMaxValue = 1m;

    // === Cointegration Spread ===
    public const int CointegrationSpreadPeriod = 60;
    public const int CointegrationSpreadZScorePeriod = 20;
    public const int CointegrationSpreadMinPeriod = 5;
    public const decimal CointegrationSpreadEpsilon = 1e-12m;
    public const double HalfLifeLogEpsilon = 1e-12;
    public const decimal CointegrationSpreadCriticalValue5Pct = -3.37m;
    public static readonly IndicatorColor CointegrationSpreadColor = new(255, 0, 150, 136);       // Teal #009688
    public static readonly IndicatorColor CointegrationSpreadZScoreColor = new(255, 255, 167, 38); // Orange #FFA726
    public static readonly IndicatorColor CointegrationSpreadHedgeRatioColor = new(255, 171, 71, 188); // Purple #AB47BC
    public static readonly IndicatorColor CointegrationSpreadAdfColor = new(255, 30, 144, 255);    // DodgerBlue #1E90FF

    // === Pair Ratio / Pair Spread ===
    public static readonly IndicatorColor PairRatioColor = new(255, 0, 188, 212);   // Cyan #00BCD4
    public static readonly IndicatorColor PairSpreadColor = new(255, 255, 112, 67); // Deep Orange #FF7043

    // === Classic Pivot Points ===
    public static readonly IndicatorColor ClassicPivotPointsPivotColor = new(255, 255, 214, 0); // Gold / Yellow #FFD600
    public static readonly IndicatorColor ClassicPivotPointsR1Color = new(255, 239, 83, 80);     // Red 400 #EF5350
    public static readonly IndicatorColor ClassicPivotPointsR2Color = new(255, 229, 57, 53);     // Red 600 #E53935
    public static readonly IndicatorColor ClassicPivotPointsR3Color = new(255, 183, 28, 28);     // Red 900 #B71C1C
    public static readonly IndicatorColor ClassicPivotPointsS1Color = new(255, 102, 187, 106);   // Green 400 #66BB6A
    public static readonly IndicatorColor ClassicPivotPointsS2Color = new(255, 67, 160, 71);     // Green 600 #43A047
    public static readonly IndicatorColor ClassicPivotPointsS3Color = new(255, 27, 94, 32);      // Green 900 #1B5E20

    // === Cross-Ticker DTW ===
    public const int CrossTickerDtwDefaultPeriod = 20;
    public const int CrossTickerDtwDefaultWarpingRadius = 3;
    public const decimal CrossTickerDtwMinValue = 0.0m;
    public static readonly IndicatorColor CrossTickerDtwColor = new(255, 86, 180, 233); // Okabe-Ito Sky Blue #56B4E9

    // === Partial Autocorrelation Function (PACF) ===
    public const int PacfDefaultPeriod = 50;
    public const int PacfDefaultLag = 1;
    public const decimal PacfMinValue = -1.0m;
    public const decimal PacfMaxValue = 1.0m;
    public static readonly IndicatorColor PacfColor = new(255, 0, 114, 178); // Okabe-Ito Blue #0072B2
    public static readonly IndicatorColor PacfConfidenceBandColor = new(255, 230, 159, 0); // Okabe-Ito Orange #E69F00

    // === Rank Correlation ===
    public const int RankCorrelationDefaultPeriod = 20;
    public const decimal RankCorrelationMinValue = -1.0m;
    public const decimal RankCorrelationMaxValue = 1.0m;
    public static readonly IndicatorColor RankCorrelationColor = new(255, 0, 158, 115); // Okabe-Ito Bluish Green #009E73

    // === Period bounds shared by the plan-03 indicators (every period is in bars) ===
    public const int DefaultPeriodMin = 1;
    public const int DefaultPeriodMax = 1000;

    // === Chandelier Exit (LeBeau): HH(High, Period) - Multiplier * ATR and LL(Low, Period) + Multiplier * ATR; unit: price-native ===
    public const int ChandelierExitDefaultPeriod = 22;
    public const decimal ChandelierExitDefaultMultiplier = 3.0m;
    public const double ChandelierExitMinMultiplier = 0.1;
    public const double ChandelierExitMaxMultiplier = 20.0;

    // === T3 moving average (Tillson): six cascaded EMAs combined with the volume factor; unit: price-native ===
    public const int T3DefaultPeriod = 5;
    public const decimal T3DefaultVolumeFactor = 0.7m;
    public const double T3MinVolumeFactor = 0.0;
    public const double T3MaxVolumeFactor = 1.0;
    public const int T3EmaStageCount = 6;

    // === McGinley Dynamic: MD = MD_prev + (P - MD_prev) / max(1, k x N x (P / MD_prev)^4); unit: price-native ===
    public const int McGinleyDefaultPeriod = 14;
    public const decimal McGinleyDefaultConstant = 0.6m;
    public const double McGinleyMinConstant = 0.1;
    public const double McGinleyMaxConstant = 2.0;
    /// <summary>Above this price ratio P / MD_prev the step is below 1e-17 of MD_prev, so MD is held (also keeps k x N x r^4 inside the decimal range).</summary>
    public const decimal McGinleyMaxPriceRatio = 1000000m;
    /// <summary>The smallest allowed divisor of the McGinley step; a smaller k x N x r^4 is raised to this value.</summary>
    public const decimal McGinleyMinDivisor = 1m;

    // === TSI (True Strength Index, Blau): 100 x EMA(EMA(m, Long), Short) / EMA(EMA(|m|, Long), Short); unit: oscillator [-100, 100] ===
    public const int TsiDefaultLongPeriod = 25;
    public const int TsiDefaultShortPeriod = 13;
    public const int TsiDefaultSignalPeriod = 7;
    public const decimal TsiMinValue = -100m;
    public const decimal TsiMaxValue = 100m;

    // === Stochastic RSI (Chande / Kroll; the original range 0..1 is scaled to 0..100 here): 100 x (RSI - min(RSI)) / (max(RSI) - min(RSI)); unit: oscillator [0, 100] ===
    public const int StochRsiDefaultRsiPeriod = 14;
    public const int StochRsiDefaultStochPeriod = 14;
    public const int StochRsiDefaultKPeriod = 3;
    public const int StochRsiDefaultDPeriod = 3;

    // === KVO (Klinger Volume Oscillator): EMA(VF, Fast) - EMA(VF, Slow), VF = V x |2 x dm / cm - 1| x trend x 100; unit: volume force (volume x 100) ===
    public const int KvoDefaultFastPeriod = 34;
    public const int KvoDefaultSlowPeriod = 55;
    public const int KvoDefaultSignalPeriod = 13;
    /// <summary>Scale factor of the Klinger volume force (VF = V x |2 x dm / cm - 1| x trend x 100).</summary>
    public const decimal KvoVolumeForceScale = 100m;

    // === Volume Profile Levels: causal POC / VAH / VAL per bar from the last Period bars; unit: price-native ===
    // Period and RowCount default to the Volume Profile ones (VolumeProfilePeriod, VolumeProfileRowCount).
    public const int VolumeProfileLevelsMinPeriod = 2;
    /// <summary>Default value-area share (0.70); the same value as <c>ChartConstants.DefaultValueAreaPercent</c>.</summary>
    public const decimal VolumeProfileLevelsDefaultValueAreaPercentage = (decimal)StockAnalyzer.Core.ChartConstants.DefaultValueAreaPercent;
    public const double VolumeProfileLevelsMinValueAreaPercentage = 0.10;
    public const double VolumeProfileLevelsMaxValueAreaPercentage = 0.99;
    /// <summary>Largest bin count whose volume array is taken from the stack; a larger one is rented from the array pool.</summary>
    public const int VolumeProfileLevelsMaxStackRows = 256;

    // === Mansfield Relative Strength (Stan Weinstein): ((A / B) / SMA(A / B, Period) - 1) * 100; unit: percentage ===
    /// <summary>Default SMA baseline period in bars (Weinstein's 52 weeks on weekly charts; users adjust e.g. to 200 on daily charts).</summary>
    public const int MansfieldRsDefaultPeriod = 52;
    /// <summary>Scale factor converting fractional relative-strength deviation into percentage points.</summary>
    public const decimal MansfieldRsPercentScale = 100m;

    // === Universe Market Breadth (plan 10): McClellan Oscillator & Market Breadth sub-panel indicators ===
    public const int McClellanFastDefaultPeriod = StockAnalyzer.Core.Analysis.MarketBreadthCalculator.DefaultMcClellanFast;
    public const int McClellanSlowDefaultPeriod = StockAnalyzer.Core.Analysis.MarketBreadthCalculator.DefaultMcClellanSlow;
    public const int McClellanFastMinPeriod = 1;
    public const int McClellanFastMaxPeriod = 500;
    public const int McClellanSlowMinPeriod = 2;
    public const int McClellanSlowMaxPeriod = 1000;
    public const int MarketBreadthAdRatioDefaultBars = StockAnalyzer.Core.Analysis.MarketBreadthCalculator.DefaultAdRatioBars;
    public const int MarketBreadthMaDefaultBars = StockAnalyzer.Core.Analysis.MarketBreadthCalculator.DefaultMaBars;
    public const int MarketBreadthHighLowDefaultBars = StockAnalyzer.Core.Analysis.MarketBreadthCalculator.DefaultHighLowBars;

    // === HAR-RV (Corsi 2009 Heterogeneous Autoregressive Realized Volatility; unit: percentage volatility, annual percentage when Annualize is on) ===
    public const int HarRvDefaultEstimationWindow = 60;
    public const int HarRvMinEstimationWindow = 20;
    public const int HarRvMaxEstimationWindow = 500;
    public const int HarRvDefaultShortPeriod = 1;
    public const int HarRvMinShortPeriod = 1;
    public const int HarRvMaxShortPeriod = 20;
    public const int HarRvDefaultMediumPeriod = 5;
    public const int HarRvMinMediumPeriod = 2;
    public const int HarRvMaxMediumPeriod = 60;
    public const int HarRvDefaultLongPeriod = 22;
    public const int HarRvMinLongPeriod = 5;
    public const int HarRvMaxLongPeriod = 252;
    public const int HarRvYangZhangInnerPeriod = 5;
    public const double HarRvYangZhangAlphaFactor = 0.34;
    public const double HarRvYangZhangBetaFactor = 1.34;
    public const double HarRvRidgeLambda = 1e-8;
    public const double HarRvSingularPivotThreshold = 1e-14;
    public static readonly IndicatorColor HarRvShortColor = Cyan;
    public static readonly IndicatorColor HarRvMediumColor = Orange;
    public static readonly IndicatorColor HarRvLongColor = Purple;

    // === BOCPD & Recursive CUSUM (Adams-MacKay 2007 NIG conjugate prior + Page CUSUM; unit: probability in [0, 1]) ===
    public const double BocpdDefaultHazardLambda = 100.0;
    public const double BocpdMinHazardLambda = 10.0;
    public const double BocpdMaxHazardLambda = 500.0;
    public const int BocpdDefaultMaxRunLength = 150;
    public const int BocpdMinMaxRunLength = 20;
    public const int BocpdMaxMaxRunLength = 250;
    public const int BocpdDefaultWarmupPeriod = 50;
    public const int BocpdMinWarmupPeriod = 20;
    public const int BocpdMaxWarmupPeriod = 250;
    public const double BocpdDefaultCusumDrift = 0.5;
    public const double BocpdMinCusumDrift = 0.1;
    public const double BocpdMaxCusumDrift = 2.0;
    public const double BocpdDefaultCusumThreshold = 4.0;
    public const double BocpdMinCusumThreshold = 1.0;
    public const double BocpdMaxCusumThreshold = 20.0;
    public const int BocpdShortRunCutoff = 5;
    public const double BocpdMinStdDev = 1e-12;
    public const double BocpdProbabilityFloor = 1e-300;
    public const double BocpdPriorMu0 = 0.0;
    public const double BocpdPriorKappa0 = 1.0;
    public const double BocpdPriorAlpha0 = 1.0;
    public const double BocpdPriorBeta0 = 1.0;

    // === Granger Causality & Engle DCC(1,1) (unit: F-statistic >= 0 and DCC correlation in [-1, 1]) ===
    public const int GrangerCausalityDefaultPeriod = 60;
    public const int GrangerCausalityMinPeriod = 30;
    public const int GrangerCausalityMaxPeriod = 500;
    public const int GrangerCausalityDefaultLagOrder = 2;
    public const int GrangerCausalityMinLagOrder = 1;
    public const int GrangerCausalityMaxLagOrder = 5;
    public const double GrangerCausalityRidgeLambda = 1e-9;
    public const int GrangerCausalityMinDf2 = 5;
    public const double GrangerCausalityRssFloor = 1e-24;
    public const double GrangerCausalityDccEwmaLambda = 0.94;
    public const double GrangerCausalityDccAlpha = 0.05;
    public const double GrangerCausalityDccBeta = 0.90;
    public const double GrangerCausalityVarianceFloor = 1e-24;
    public const int GrangerCausalityEwmaSeedBars = 10;

    // === Johansen Bivariate Cointegration (Trace / MaxEigen test statistics >= 0; MacKinnon, Haug & Michelis 1999 Table II Case III r=0 Trace CV95 = 15.4947; distinct from MaxEigenStat & Osterwald-Lenum 1992 two-decimal 15.41) ===
    public const int JohansenDefaultPeriod = 100;
    public const int JohansenMinPeriod = 30;
    public const int JohansenMaxPeriod = 500;
    public const double JohansenMomentRidge = 1e-10;
    public const double JohansenDeterminantFloor = 1e-24;
    public const double JohansenMaxEigenvalueClamp = 0.999999;
    public const double JohansenEigenvectorPivotFloor = 1e-12;
    public const decimal JohansenCriticalValue95m = 15.4947m;

    // === Causal Matrix Profile & Petitjean DBA Ensemble ===
    public const int MatrixProfileMinQueryLength = 10;
    public const int MatrixProfileMaxQueryLength = 200;
    public const int MatrixProfileExclusionDivisor = 4;
    public const int DbaDefaultTopK = 5;
    public const int DbaMinTopK = 1;
    public const int DbaMaxTopK = 10;
    public const int DbaDefaultIterations = 5;
    public const int DbaSakoeChibaDivisor = 5;
    public const double DbaDistanceEpsilon = 1e-6;
    public const double DbaLowerQuantile = 0.15;
    public const double DbaUpperQuantile = 0.85;

    // === Block chart overlays (Renko, Point and Figure, Kagi, Three Line Break on a time-series chart); unit: price-native ===
    // Defaults are the ones of the corresponding chart types (GlobalChartSettings / ChartConstants); a drift test keeps them equal. The overlay settings are independent copies, never a live link.
    public const ChartSizingMode BlockChartOverlayDefaultSizingMode = ChartSizingMode.AutoAtr;
    public const ChartRoundingMode BlockChartOverlayDefaultRoundingMode = ChartRoundingMode.None;
    public const AutoFallbackMode BlockChartOverlayDefaultFallbackMode = AutoFallbackMode.Percentage;
    /// <summary>The smallest box / brick / reversal size of the settings dialogs of the chart types.</summary>
    public const double BlockChartOverlayMinSize = 0.0001;
    public const double BlockChartOverlayMaxSize = 1000000000.0;
    public const double BlockChartOverlayMinPercentage = 0.01;
    public const double BlockChartOverlayMaxPercentage = 100.0;
    public const int BlockChartOverlayMinAtrPeriod = 1;
    public const int BlockChartOverlayMaxAtrPeriod = 500;
    public const double BlockChartOverlayMinAtrMultiplier = 0.1;
    public const double BlockChartOverlayMaxAtrMultiplier = 10.0;
    public const int BlockChartOverlayMinReversal = 1;
    public const int BlockChartOverlayMaxReversal = 10;
    public const int BlockChartOverlayMinLineCount = 1;
    public const int BlockChartOverlayMaxLineCount = 100;
    public const double BlockChartOverlayMinMinimumMove = 0.0;
    /// <summary>The up / down colours of the chart types (<c>ChartSettingsConstants.DefaultBullishColor</c> #00B050 / <c>DefaultBearishColor</c> #FF0000); a drift test compares them.</summary>
    public static readonly IndicatorColor BlockChartOverlayUpColor = new(255, 0, 176, 80);
    public static readonly IndicatorColor BlockChartOverlayDownColor = new(255, 255, 0, 0);
    /// <summary>Colours by role: the Reversal dots use the "second line" colour, the Update dots the "main line" colour.</summary>
    public static readonly IndicatorColor BlockChartOverlayReversalColor = StochDColor;
    public static readonly IndicatorColor BlockChartOverlayUpdateColor = SmaColor;
    /// <summary>Diameter of the Reversal / Update dots in device-independent pixels (<see cref="CoreIndicatorSettings.DotSize"/>). The default equals the radius 2.25 that the former rule max(2, 1.5 x thickness) gave for the default thickness, so the default look is unchanged.</summary>
    public const double BlockChartOverlayDefaultDotSize = 4.5;
    public const double BlockChartOverlayMinDotSize = 2.0;
    public const double BlockChartOverlayMaxDotSize = 20.0;
    public const double BlockChartOverlayDotSizeStep = 0.5;
    /// <summary>The IsUpTrend helper series holds 1 (up) or 0 (down); a value at or above this threshold is drawn with the up colour.</summary>
    public const decimal BlockChartOverlayUpTrendThreshold = 0.5m;

    // === Chart-pattern flag indicators (plan ChartPatterns56): 0m / 1m flag in a sub-window with a fixed Y range ===
    /// <summary>Lower end of the fixed Y range of a flag sub-window (a flag is 0m or 1m; unit: flag value).</summary>
    public const decimal PatternFlagMinValue = 0m;
    /// <summary>Upper end of the fixed Y range of a flag sub-window: headroom above the 1m flag so a bar does not touch the panel ceiling.</summary>
    public const decimal PatternFlagMaxValue = 1.2m;
    /// <summary>Lower end of the fixed Y range of a bidirectional flag sub-window: the bearish series is plotted at -1m (plan ChartPatterns56 v2, integration spec 4.2); symmetric to <see cref="PatternFlagMaxValue"/>.</summary>
    public const decimal PatternFlagBidirectionalMinValue = -PatternFlagMaxValue;
    /// <summary>Colours by role (decision U-4 A: an existing named constant is reused where one exists). Bullish and bearish equal the MACD histogram colours.</summary>
    public static readonly IndicatorColor PatternFlagBullishColor = MacdHistogramUpColor;
    public static readonly IndicatorColor PatternFlagBearishColor = MacdHistogramDownColor;
    public static readonly IndicatorColor PatternFlagNeutralColor = new(255, 120, 144, 156);       // Blue Grey #78909C
    public static readonly IndicatorColor PatternRecognitionFlagColor = new(255, 66, 165, 245);   // Blue #42A5F5
    public static readonly IndicatorColor MarketStructureFlagColor = Purple;                       // #AB47BC
    public static readonly IndicatorColor HarmonicFlagColor = new(255, 255, 167, 38);              // Orange #FFA726
    public static readonly IndicatorColor WaveTheoryFlagColor = new(255, 41, 182, 246);            // Light Blue #29B6F6
    public static readonly IndicatorColor PatternFlagGranvilleBuy3Color = new(255, 102, 187, 106); // Green #66BB6A
    public static readonly IndicatorColor PatternFlagGranvilleSell3Color = new(255, 255, 112, 67); // Deep Orange #FF7043
    public static readonly IndicatorColor PatternFlagGranvilleSell4Color = new(255, 236, 64, 122); // Pink #EC407A
}
