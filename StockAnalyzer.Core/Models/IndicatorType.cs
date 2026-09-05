using System.ComponentModel;

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
    [Description("Guppy Moving Average")] GMA,
    
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
    [Description("Engle-Granger Test")] EngleGrangerTest,
    
    // Batch 9: Compatible Advanced Indicators
    [Description("McClellan Oscillator")] McClellanOscillator,
    [Description("Expected Move")] ExpectedMove,

    // Batch 10
    [Description("Murrey Math Lines")] MurreyMath,
    [Description("Correlation Proxy")] CorrelationProxy,
    [Description("EGARCH")] Egarch,
    [Description("Options Greeks")] Greeks,

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
    [Description("Three Line Break Signal")] ThreeLineBreakSignal,
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
    [Description("Variable Index Dynamic Average")] VIDYA
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

        // 1. Try standard member name parsing (case-insensitive)
        if (Enum.TryParse<IndicatorType>(value, true, out result))
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
