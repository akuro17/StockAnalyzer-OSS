using System.ComponentModel;
using StockAnalyzer.Core.Utils;

namespace StockAnalyzer.Core.Models;

/// <summary>
/// Enum defining all available technical indicators.
/// This provides compile-time type safety and prevents string-based errors.
/// </summary>
public enum IndicatorType
{
    // Moving Averages (Batch 1)
    [Description("Simple Moving Average")] SMA,
    [Description("Exponential Moving Average")] EMA,
    [Description("Weighted Moving Average")] WMA,
    [Description("Hull Moving Average")] HMA,
    [Description("Triangular Moving Average")] TMA,
    [Description("Double Exponential Moving Average")] DEMA,
    [Description("Triple Exponential Moving Average")] TEMA,
    [Description("Volume Weighted Moving Average")] VWMA,
    [Description("Volatility Adaptive Moving Average")] VAMA,
    [Description("Smoothed Moving Average")] SMMA,

    // Batch 2
    [Description("Linear Regression Moving Average")] LRMA,
    [Description("Arnaud Legoux Moving Average")] ALMA,
    [Description("Zero Lag Exponential Moving Average")] ZLEMA,
    [Description("Least Squares Moving Average")] LSMA,
    [Description("Guppy Multiple Moving Average")] GMA,

    // Batch 3
    [Description("TRIX Oscillator")] TRIX,
    [Description("Momentum")] Momentum,
    [Description("Stochastic Momentum Index")] SMI,
    [Description("Bollinger BandWidth")] BandWidth,
    [Description("Bollinger Bands Ratio")] BollingerBandsRatio,

    // Volume Indicators (Batch 4)
    [Description("Accumulation/Distribution Line")] ADL,
    [Description("Volume Price Trend")] VPT,
    [Description("Positive Volume Index")] PVI,

    // Oscillators (Batch 5)
    [Description("Fisher Transform")] FisherTransform,
    [Description("Ultimate Oscillator")] UltimateOscillator,
    [Description("Fast Fourier Transform Cycle")] FFTCycle,

    // Risk Indicators (Batch 7)
    [Description("Value at Risk")] VaR,
    [Description("Conditional Value at Risk")] CVaR,
    [Description("Variance")] Variance,
    [Description("Standard Deviation")] StandardDeviation,

    // Statistics (Batch 9)
    [Description("Correlation")] Correlation,
    [Description("Linear Regression")] LinearRegression,
    [Description("Regression Slope")] RegressionSlope,

    // Existing Indicators
    [Description("Bollinger Bands")] BB,
    [Description("Relative Strength Index")] RSI,
    [Description("Moving Average Convergence Divergence")] MACD,
    [Description("Average True Range")] ATR,
    [Description("On-Balance Volume")] OBV,
    [Description("Volume Weighted Average Price")] VWAP,
    [Description("Chaikin Money Flow")] CMF,
    [Description("Chaikin Oscillator")] ChaikinOscillator,
    [Description("Ease of Movement")] EOM,
    [Description("Balance of Power")] BOP,
    [Description("Amihud Illiquidity")] AmihudIlliquidity,
    [Description("Parabolic SAR")] ParabolicSAR,
    [Description("Average Directional Index")] ADX,
    [Description("ADX Rating")] ADXR,
    [Description("Directional Movement Index")] DMI,
    [Description("Aroon")] Aroon,
    [Description("SuperTrend")] SuperTrend,

    [Description("Keltner Channels")] Keltner,
    [Description("Displaced Moving Average")] Dma,
    [Description("Kaufman Adaptive Moving Average")] KAMA,
    [Description("Adaptive Moving Average")]
    [Obsolete("Duplicate of KAMA (Perry Kaufman's Adaptive Moving Average). Kept for enum ordinal stability of existing serialized templates; no longer registered in IndicatorFactory.")]
    AMA,
    [Description("Jurik Moving Average")] JMA,
    [Description("Chaikin Volatility")] ChaikinVolatility,
    [Description("Yang-Zhang Volatility")] YangZhangVolatility,
    [Description("Choppiness Index")] ChoppinessIndex,
    [Description("Williams Vix Fix")] WilliamsVixFix,
    [Description("Awesome Oscillator")] AwesomeOscillator,
    [Description("Acceleration Oscillator")] AccelerationOscillator,
    [Description("Commodity Channel Index")] CCI,
    [Description("Rate of Change")] ROC,
    [Description("Rank Correlation Index")] RCI,
    [Description("Stochastic Oscillator")] Stoch,
    [Description("Money Flow Index")] MFI,

    // Batch 8: Statistical & Advanced
    [Description("Wilder's Smoothing")] WilderSmoothing,
    [Description("Pivot Points Advanced")] PivotPointsAdvanced,
    [Description("GARCH(1,1)")] Garch11,
    [Description("Fourier Transform")] FourierTransform,
    [Description("Hilbert Transform")] HilbertTransform,
    [Description("Engle-Granger Test")]
    [Obsolete("Never implemented. Kept for enum ordinal stability of existing serialized templates; no class is registered in IndicatorFactory.")]
    EngleGrangerTest,

    // Batch 9: Compatible Advanced Indicators
    [Description("McClellan Oscillator")] McClellanOscillator,
    [Description("Expected Move")] ExpectedMove,

    // Batch 10
    [Description("Murrey Math Lines")] MurreyMath,
    [Description("Correlation Proxy")]
    [Obsolete("Never implemented. Kept for enum ordinal stability of existing serialized templates; no class is registered in IndicatorFactory.")]
    CorrelationProxy,
    [Description("EGARCH")] Egarch,
    [Description("Options Greeks")]
    [Obsolete("Never implemented. Kept for enum ordinal stability of existing serialized templates; no class is registered in IndicatorFactory.")]
    Greeks,

    // Batch 11
    [Description("Renko")] Renko,
    [Description("Kagi")] Kagi,
    [Description("Point and Figure")] PointAndFigure,
    [Description("Hurst Exponent")] HurstExponent,
    [Description("Historical Volatility")] HistoricalVolatility,

    // Batch 11 (Chart versions - true non-time-series from 3_1.md)
    [Description("Renko Chart")] RenkoChart,
    [Description("Kagi Chart")] KagiChart,
    [Description("Point and Figure Chart")] PointAndFigureChart,

    // Batch 12
    [Description("Ichimoku Kinko Hyo")] Ichimoku,
    [Description("ZigZag")] ZigZag,
    [Description("Williams %R")] WilliamsR,


    // Batch 13
    [Description("Force Index")] ForceIndex,
    [Description("Negative Volume Index")] NegativeVolumeIndex,
    [Description("Darvas Box")] DarvasBox,
    [Description("Three Line Break")] ThreeLineBreak,
    [Description("Three Line Break Signal")]
    [Obsolete("Deprecated reversal signal indicator. Kept for enum ordinal stability of existing serialized templates; no longer registered in IndicatorFactory.")]
    ThreeLineBreakSignal,
    [Description("Darvas Box Signal")] DarvasBoxSignal,
    [Description("Vortex Signal")] VortexSignal,

    // Batch 14
    [Description("Elder Force Index")] ElderForceIndex,
    [Description("Elder-ray Index")] ElderRay,
    [Description("Chande Momentum Oscillator")] CMO,
    [Description("Elder Impulse System")] ElderImpulse,
    [Description("Coppock Curve")] CoppockCurve,
    [Description("Mass Index")] MassIndex,
    [Description("Vortex Indicator")] Vortex,
    [Description("Schaff Trend Cycle")] SchaffTrendCycle,
    [Description("Connors RSI")] ConnorsRsi,
    [Description("DeMarker")] DeMarker,
    [Description("Detrended Price Oscillator")] DPO,
    [Description("Know Sure Thing")] KST,
    [Description("Percentage Price Oscillator")] PPO,
    [Description("Price Oscillator")] PriceOscillator,
    [Description("Relative Vigor Index")] RVI,

    // Batch 15: WebAI Advanced Indicators
    [Description("Kalman Filter")] KalmanFilter,
    [Description("Fractal Adaptive Moving Average")] FRAMA,
    [Description("Entropy")] Entropy,
    [Description("Anchored VWAP")] AnchoredVWAP,
    [Description("Garman-Klass Volatility")] GarmanKlassVolatility,
    [Description("Z-Score")] ZScore,
    [Description("Kyle's Lambda")] KylesLambda,
    [Description("Price Volume Trend")] PVT,
    [Description("Multi-Timeframe Lagged EMA")] MtfLaggedEma,
    [Description("Donchian Channels")] Donchian,
    [Description("Normalized Average True Range")] NATR,
    [Description("RVI Volatility")] RviVolatility,
    [Description("Ulcer Index")] UlcerIndex,

    // Batch 17: Category 23
    [Description("Envelope")] Envelope,
    [Description("Psychological Line")] PsychologicalLine,
    [Description("MA Deviation Rate")] MaDeviationRate,
    [Description("Volume Moving Average")] VolumeMA,
    [Description("Volume Rate of Change")] VolumeROC,
    [Description("High/Low Band")]
    [Obsolete("Replaced by Highest/Lowest (Price Source unification). Kept for enum ordinal stability of existing serialized templates; no longer registered in IndicatorFactory.")]
    HighLowBand,
    [Description("Classic Pivot Points")] ClassicPivotPoints,
    [Description("Volume Profile")] VolumeProfile,
    [Description("Volume")] Volume,

    // Batch 17: Overlays
    [Description("Renko Overlay")] RenkoOverlay,
    [Description("Point and Figure Overlay")] PointAndFigureOverlay,
    // [Description("Three Line Break Overlay")] ThreeLineBreakOverlay, // REMOVED as redundant
    [Description("Kagi Overlay")] KagiOverlay,
    [Description("MESA Adaptive Moving Average")] MESA,
    [Description("QStick")] QStick,
    [Description("Market Structure Shift")] MarketStructureShift,
    [Description("Structural DTW Oscillator")] StructuralDtw,

    // Batch 18: Signal Detection
    [Description("Granville's Law")] GranvilleLaw,

    [Description("Moving Average Cross")] MovingAverageCross,

    // Prompt 61-1: Prime Number Oscillator
    [Description("Prime Number Oscillator")] PrimeNumberOscillator,

    // Prompt 61-2: Prime Number Bands
    [Description("Prime Number Bands")] PrimeNumberBands,

    // Prompt 61-6: B-Spline Moving Average
    [Description("B-Spline Moving Average")] BSMA,

    // Batch 19: FFT Extensions
    [Description("FFT Trend Filter")] FFTTrendFilter,
    [Description("IFFT Instantaneous Phase")] IFFTInstantaneousPhase,
    [Description("IFFT Instantaneous Amplitude")] IFFTInstantaneousAmplitude,

    // BL-6 (WebAI review): auto-tuning band-pass reconstruction of the dominant cycle
    [Description("IFFT Band-Pass Filter")] IFFTBandPassFilter,

    // Adaptive Indicators
    [Description("Adaptive Relative Strength Index")] AdaptiveRSI,

    // Batch 20: HighLowBand split (Price Source unification)
    [Description("Highest")] Highest,
    [Description("Lowest")] Lowest,

    // Batch 21: True Indicators
    [Description("True High")] TrueHigh,
    [Description("True Low")] TrueLow,
    [Description("True Range")] TrueRange,

    // Advanced Regime Detection
    [Description("Hidden Markov Model")] HiddenMarkovModel,

    // Advanced Geometric / Waveform
    [Description("Fréchet Distance Oscillator")] FrechetOscillator,

    // Advanced Spectral / Decomposition
    [Description("Singular Spectrum Analysis")] SSA,
    [Description("SSA Residual Volatility Band")] SSAResidualBand,
    [Description("SSA Dominant Cycle Extractor")] SSACycle,
    [Description("SSA Entropy")] SSAEntropy,
    [Description("SSA Squeeze")] SSASqueeze,
    [Description("SSA Signal-to-Noise Ratio")] SSASNR,
    [Description("SSA Anomaly")] SSAAnomaly,

    // Advanced Signal Processing / Hilbert Transform Suite
    [Description("Hilbert Sine Wave")] HilbertSine,
    [Description("Hilbert Instantaneous Trendline")] HilbertTrendline,
    [Description("Hilbert Trend vs Cycle Mode")] HilbertTrendMode,

    // Advanced Time Series / Econometric
    [Description("Autoregressive Integrated Moving Average")] ARIMA,

    // Geometric / Line Detection
    [Description("Hough Trend Strength")] HoughTrendStrength,
    [Description("Hough Trend Angle")] HoughTrendAngle,

    // Price Series Overlay
    [Description("Price")] Price,

    // Adaptive Moving Average
    [Description("Variable Index Dynamic Average")] VIDYA,

    // Volume Weighted Rolling Average
    [Description("Rolling VWAP")] RollingVWAP,

    // Polar Coordinate Transform Decomposition (single analytic signal; consumes PolarCoordinateDecompositionEngine)
    [Description("Polar Amplitude Ratio")] PolarAmplitudeRatio,
    [Description("Polar Phase")] PolarPhase,
    // Cycle/period-derived angular frequency (omega = 2*pi / InstantaneousPeriod), not phase-difference angular velocity.
    [Description("Polar Cycle Angular Frequency")] PolarCycleAngularFrequency,

    // Autocorrelation
    [Description("Autocorrelation")] Autocorrelation,

    // Kaufman Efficiency Ratio (standalone oscillator)
    [Description("Kaufman Efficiency Ratio")] EfficiencyRatio,

    // Fractal Dimension Index (Sevcik-Matulich regime oscillator)
    [Description("Fractal Dimension Index")] FDI,

    // Time at Price (Price residence time profile)
    [Description("Time at Price")] TimeAtPrice,

    // Clothoid Differential Geometry & Fresnel Smoothing
    [Description("Clothoid Oscillator")] ClothoidOscillator,
    [Description("Clothoid Moving Average")] ClothoidMovingAverage,

    // Cross-Correlation Function (CCF)
    [Description("Cross-Correlation Function")] CrossCorrelation,

    // Rolling Beta
    [Description("Rolling Beta")] RollingBeta,

    // Rolling Alpha
    [Description("Rolling Alpha")] RollingAlpha,

    // Relative Strength Ratio
    [Description("Ratiocator")] Ratiocator,

    // Rolling R-Squared
    [Description("Rolling R-Squared")] RollingRSquared,

    // Statistics / Econometric
    [Description("Cointegration Spread")] CointegrationSpread,

    // Pair Ratio (A / B) and Pair Spread (A - B) of the chart symbol and a comparison symbol
    [Description("Pair Ratio")] PairRatio,
    [Description("Pair Spread")] PairSpread,

    // Batch 1 & 2: Advanced Correlation & Time-Series Suite
    [Description("Cross-Ticker DTW")] CrossTickerDtw,
    [Description("Partial Autocorrelation Function")] PACF,
    [Description("Rank Correlation")] RankCorrelation,

    // WebAiExpansion plan 03: new price/volume indicators (append-only; ordinals of serialized templates must stay stable).
    [Description("Chandelier Exit")] ChandelierExit,
    [Description("T3 Moving Average")] T3,
    [Description("McGinley Dynamic")] McGinleyDynamic,
    [Description("True Strength Index")] TSI,
    [Description("Stochastic RSI")] StochRSI,
    [Description("Klinger Volume Oscillator")] KVO,
    [Description("Volume Profile Levels")] VolumeProfileLevels,

    // WebAiExpansion plan 04: range-based volatility estimators (append-only; Garch11, HurstExponent and Entropy keep their existing members).
    [Description("Parkinson Volatility")] ParkinsonVolatility,
    [Description("Rogers-Satchell Volatility")] RogersSatchellVolatility,

    // WebAiExpansion plan 09: relative strength (append-only).
    [Description("Mansfield RS")] MansfieldRS,

    // WebAiExpansion plan 10: universe market breadth indicator (append-only).
    [Description("Market Breadth")] MarketBreadth,

    // Time-series volatility expansion: Heterogeneous Autoregressive Realized Volatility (append-only).
    [Description("HAR-RV")] HarRv,

    // Time-series regime change detection: Bayesian Online Changepoint Detection & Recursive CUSUM (append-only).
    [Description("BOCPD Regime")] Bocpd,

    // Cross-asset econometrics: bivariate Granger causality + Engle DCC(1,1) and Johansen VECM cointegration (append-only).
    [Description("Granger Causality")] GrangerCausality,
    [Description("Johansen Cointegration")] JohansenCointegration,

    // Statistical functions (docs/indicators_formulas.md, section Statistical Functions): statistics of a window of returns (append-only).
    [Description("Skewness / Kurtosis")] SkewnessKurtosis,
    [Description("Jarque-Bera")] JarqueBera,
    [Description("Ljung-Box Q")] LjungBoxQ,

    // Statistical functions plan, run R4: asymmetric risk and realized jump variation of a window of returns (append-only).
    [Description("Downside Deviation")] DownsideDeviation,
    [Description("Bipower Variation")] BipowerVariation,

    // Statistical functions plan, run R5: augmented Dickey-Fuller unit-root test of a window of prices (append-only).
    [Description("ADF Stationarity")] ADFStationarity,

    // Statistical functions plan, run R6: Lo-MacKinlay variance ratio test of a window of log returns (append-only).
    [Description("Variance Ratio")] VarianceRatio,

    // Statistical functions plan, run R7: robust price bands and lines of a window of prices, and the Cornish-Fisher Value at Risk of a window of returns (append-only).
    [Description("Quantile Bands")] QuantileBands,
    [Description("Theil-Sen Regression")] TheilSenRegression,
    [Description("Cornish-Fisher VaR")] CornishFisherVaR,

    // Chart patterns plan (ChartPatterns56), run 2: the 43 candlestick pattern flags (append-only; the member name is persisted, the description is a UI label that continues the
    // Screener "Chart Patterns" criteria short name). One pattern = one 0/1 flag series; the pattern of each type is in PatternFlags.Candles.CandlePatternFlagTable.
    [Description("Bullish Marubozu")] CandleBullishMarubozu,
    [Description("Bearish Marubozu")] CandleBearishMarubozu,
    [Description("Hammer / Bullish Umbrella")] CandleHammer,
    [Description("Inverted Hammer")] CandleInvertedHammer,
    [Description("Hanging Man / Bearish Umbrella")] CandleHangingMan,
    [Description("Shooting Star")] CandleShootingStar,
    [Description("Doji / Cross Doji")] CandleCrossDoji,
    [Description("Dragonfly Doji")] CandleDragonflyDoji,
    [Description("Gravestone Doji")] CandleGravestoneDoji,
    [Description("Bullish Engulfing")] CandleBullishEngulfing,
    [Description("Bearish Engulfing")] CandleBearishEngulfing,
    [Description("Bullish Harami")] CandleBullishHarami,
    [Description("Bearish Harami")] CandleBearishHarami,
    [Description("Piercing Line")] CandlePiercingLine,
    [Description("Dark Cloud Cover")] CandleDarkCloudCover,
    [Description("Homing Pigeon")] CandleHomingPigeon,
    [Description("Bullish Kicking")] CandleBullishKicking,
    [Description("Bearish Kicking")] CandleBearishKicking,
    [Description("Morning Star")] CandleMorningStar,
    [Description("Evening Star")] CandleEveningStar,
    [Description("Three White Soldiers")] CandleThreeWhiteSoldiers,
    [Description("Three Black Crows")] CandleThreeBlackCrows,
    [Description("Bullish Side-by-Side White Lines")] CandleBullishSideBySideWhiteLines,
    [Description("Bearish Side-by-Side White Lines")] CandleBearishSideBySideWhiteLines,
    [Description("Bullish Abandoned Baby")] CandleBullishAbandonedBaby,
    [Description("Bearish Abandoned Baby")] CandleBearishAbandonedBaby,
    [Description("Advance Block")] CandleAdvanceBlock,
    [Description("Deliberation")] CandleDeliberation,
    [Description("Stick Sandwich")] CandleStickSandwich,
    [Description("Bullish Tasuki Gap")] CandleBullishTasukiGap,
    [Description("Bearish Tasuki Gap")] CandleBearishTasukiGap,
    [Description("Bullish Gap Three Methods")] CandleBullishGapThreeMethods,
    [Description("Bearish Gap Three Methods")] CandleBearishGapThreeMethods,
    [Description("Identical Three Crows")] CandleIdenticalThreeCrows,
    [Description("Bullish Three-Line Strike")] CandleBullishThreeLineStrike,
    [Description("Bearish Three-Line Strike")] CandleBearishThreeLineStrike,
    [Description("Concealing Baby Swallow")] CandleConcealingBabySwallow,
    [Description("Rising Three Methods")] CandleRisingThreeMethods,
    [Description("Falling Three Methods")] CandleFallingThreeMethods,
    [Description("Mat Hold")] CandleMatHold,
    [Description("Ladder Bottom")] CandleLadderBottom,
    [Description("Bullish Breakaway")] CandleBullishBreakaway,
    [Description("Bearish Breakaway")] CandleBearishBreakaway,

    // Chart patterns plan (ChartPatterns56), run 3: formation and market structure flags (append-only; descriptions continue the Screener "Chart Patterns" criteria short names).
    [Description("Head and Shoulders")] PatternHeadAndShoulders,
    [Description("Double Top / Bottom")] PatternDoubleTopBottom,
    [Description("Bullish / Bearish Flag")] PatternFlag,
    [Description("Break of Structure (BOS)")] MarketStructureBos,
    [Description("Change of Character (CHoCH)")] MarketStructureChoch,
    [Description("Higher High / Higher Low")] MarketStructureHhHl,

    // Chart patterns plan (ChartPatterns56), run V2-6: harmonic flags (append-only; descriptions continue the Screener "Chart Patterns" criteria short names).
    [Description("Gartley Pattern")] HarmonicGartley,
    [Description("Butterfly Pattern")] HarmonicButterfly,
    [Description("Bat Pattern")] HarmonicBat,
    [Description("Granville's Law Buy Signal")] GranvilleBuySignal,
    [Description("Granville's Law Sell Signal")] GranvilleSellSignal,
    [Description("Elliott Wave Impulse 1-5")] ElliottWaveImpulse,
    [Description("Elliott Wave Corrective ABC")] ElliottWaveCorrective,

    // WebAi remediation plan (U-W10): bearish market structure trend mirror (append-only; preserves all existing ordinals).
    [Description("Lower Low / Lower High")] MarketStructureLlLh,

    // Split statistical moment indicators (SA-SPEC-SKEW-KURT-DIST-001, append-only; preserves all existing ordinals).
    [Description("Skewness")] Skewness,
    [Description("Kurtosis")] Kurtosis,

    // DSL indicator and drawing tool expansion, run I-1: KPSS stationarity, rolling Sharpe ratio and rolling maximum drawdown (append-only; preserves all existing ordinals).
    [Description("KPSS Stationarity")] KpssStationarity,
    [Description("Rolling Sharpe Ratio")] RollingSharpe,
    [Description("Rolling Max Drawdown")] RollingMaxDrawdown,

    // DSL indicator and drawing tool expansion, run I-2: two-symbol statistics of the returns of the chart symbol and a comparison symbol (append-only; preserves all existing ordinals).
    [Description("Treynor Ratio")] TreynorRatio,
    [Description("Information Ratio")] InformationRatio,
    [Description("Tracking Error")] TrackingError,
    [Description("Rolling Covariance")] RollingCovariance,

    // DSL indicator and drawing tool expansion, run I-3: price-native robust and generalised window averages (overlays; append-only; preserves all existing ordinals).
    [Description("Trimmed Moving Average")] TrimmedMA,
    [Description("Winsorized Moving Average")] WinsorizedMA,
    [Description("Huber Moving Average")] HuberMA,
    [Description("Biweight Moving Average")] BiweightMA,
    [Description("Geometric Moving Average")] GeometricMA,
    [Description("Harmonic Moving Average")] HarmonicMA,

    // DSL indicator and drawing tool expansion, run I-4: polynomial and nonlinear regression overlays (append-only).
    [Description("Polynomial Regression")] PolynomialRegression,
    [Description("Nonlinear Regression")] NonlinearRegression,

    // DSL indicator and drawing tool expansion, run I-5: hypothesis tests of the returns of the last bars against a null mean or against the returns of the preceding bars (append-only).
    [Description("One-Sample t-Test")] OneSampleTTest,
    [Description("Two-Sample t-Test (Welch)")] TwoSampleTTest,
    [Description("Two-Sample KS Test")] KsTwoSampleTest,
    [Description("Mann-Whitney U Test")] MannWhitneyUTest,
    [Description("KL Divergence")] KlDivergence
}

public static class IndicatorTypeExtensions
{
    /// <summary>
    /// Gets the programmatic name of the indicator (e.g., "SMA", "EMA").
    /// </summary>
    public static string GetFormalName(this IndicatorType type)
    {
        return type.ToString();
    }

    /// <summary>
    /// Gets the localized/friendly name of the indicator (e.g., "Simple Moving Average").
    /// </summary>
    public static string GetDescription(this IndicatorType type)
    {
        var field = type.GetType().GetField(type.ToString());
        if (field == null) return type.ToString();

        var attribute = (System.ComponentModel.DescriptionAttribute?)Attribute.GetCustomAttribute(field, typeof(System.ComponentModel.DescriptionAttribute));
        return attribute?.Description ?? type.ToString();
    }

    /// <summary>
    /// Attempts to parse an IndicatorType from either its member name or its description.
    /// </summary>
    public static bool TryParse(string value, out IndicatorType result)
    {
        if (string.IsNullOrEmpty(value))
        {
            result = default;
            return false;
        }

        // 1. Try strict member name parsing (case-insensitive, rejects numeric strings, zero-boxing)
        if (StrictEnumValidator.TryParseStrict(value, out result))
        {
            return true;
        }

        // 2. Try matching against descriptions
        foreach (IndicatorType type in Enum.GetValues(typeof(IndicatorType)))
        {
            if (string.Equals(type.GetDescription(), value, StringComparison.OrdinalIgnoreCase))
            {
                result = type;
                return true;
            }
        }

        return false;
    }
}
