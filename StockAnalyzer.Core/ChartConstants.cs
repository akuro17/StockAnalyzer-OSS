using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core;

public static class ChartConstants
{
    #region Layout Margins

    /// <summary>Left margin for chart area.</summary>
    public const float MarginLeft = 10f;
    
    /// <summary>Right margin for chart area (wide enough for Y-axis price labels).</summary>
    public const float MarginRight = 65f;
    
    /// <summary>Horizontal margin (legacy).</summary>
    public const float MarginHorizontal = 10f;
    
    /// <summary>Top margin for chart area.</summary>
    public const float MarginTop = 0f;
    
    /// <summary>Bottom margin for chart area.</summary>
    public const float MarginBottom = 30f;

    #endregion

    // RSI Defaults
    public const int DefaultRsiPeriod = 14;
    public const decimal DefaultRsiOversoldThreshold = 30m;
    public const decimal RsiNeutralValue = 50m;
    public const decimal RsiMaxValue = 100m;

    // Percentages
    public const decimal PercentageDivisor = 100m;
    public const decimal DefaultFallbackPercentage = 0.01m; // 1%

    // P&F / Renko Defaults
    public const decimal DefaultBoxSize = 1.0m;
    public const int DefaultReversalAmount = 3;

    // General Defaults
    public const decimal DefaultAtrMultiplier = 1.0m;
    public const string DefaultSymbol = "MSFT";
    public const int DefaultVisibleCandleCount = 100;
    public const int DefaultAtrPeriod = 14;

    // Specific Chart Defaults
    public const int DefaultReverseWatchPeriod = 25;
    public const int DefaultReverseWatchDataCount = 100;
    public const decimal DefaultKagiReversalAmount = 1m;
    public const decimal DefaultKagiReversalPercent = 1.0m;
    public const decimal DefaultRenkoBrickSize = 10m;
    public const decimal DefaultRenkoBrickPercent = 1.0m;
    public const decimal DefaultPnfBoxSize = 10m;
    public const int DefaultThreeLineBreakLineCount = 3;

    // Volume Analysis
    public const int DefaultVolumeProfileRowSize = 24;
    public const decimal MinBinSize = 0.01m;
    public const double DefaultValueAreaPercent = 0.70;

    // EMA / Recursive Indicator Convergence
    // Multiplier for calculating warmup overlap period.
    // Period * EmaConvergenceMultiplier steps provides 99.9% convergence for recursive indicators.
    public const int EmaConvergenceMultiplier = IndicatorDefaultConstants.EmaConvergenceMultiplier;

    // Rendering Parameters - Bounds Calculation
    /// <summary>
    /// Padding percentage applied to bounds to prevent data points from touching edges (5%).
    /// </summary>
    public const decimal BoundsPaddingPercent = 0.05m;

    /// <summary>
    /// Shrink factor applied to minimum value when range is zero (0.99 = -1%).
    /// </summary>
    public const decimal MinRangeShrinkFactor = 0.99m;

    /// <summary>
    /// Expand factor applied to maximum value when range is zero (1.01 = +1%).
    /// </summary>
    public const decimal MinRangeExpandFactor = 1.01m;

    // Geometric Pattern Detection (Prompt 33-7)
    /// <summary>Default ZigZag threshold percentage for geometric pattern pivot extraction.</summary>
    public const decimal DefaultGeometricZigZagThreshold = 2.0m; // 2% minimum swing for high resolution

    /// <summary>Multi-scale thresholds to capture nested geometric structures (micro, medium, macro).</summary>
    public static readonly decimal[] GeometricMultiScaleThresholds = { 2.0m, 5.0m, 8.0m };
    
    /// <summary>ATR multiplier for breakout tolerance to allow wicks outside pattern bounds depending on volatility.</summary>
    public const double GeometricAtrBreakoutMultiplier = 0.5; // e.g. 0.5 ATR

    /// <summary>
    /// The maximum percentage variance allowed for a pivot to be considered a valid "touch" on a pattern's regression bounds.
    /// Used to enforce strict structure where patterns must connect at least 2 highs and 2 lows accurately.
    /// </summary>
    public const double GeometricTouchTolerance = 0.05; // 5% touch tolerance
    /// <summary>
    /// Maximum ratio between slopes for two lines to be considered parallel.
    /// If |slopeA - slopeB| / max(|slopeA|, |slopeB|) &lt; this value, lines are parallel.
    /// </summary>
    public const double GeometricParallelSlopeRatio = 0.15;

    /// <summary>Minimum number of pivot points required to perform geometric analysis.</summary>
    public const int GeometricMinPivotCount = 4;

    /// <summary>
    /// Maximum number of candles to look back when checking for a pole
    /// preceding a flag or pennant formation.
    /// </summary>
    public const int GeometricPoleLookbackBars = 10;

    /// <summary>
    /// Minimum percentage move required for a "pole" (sharp directional move)
    /// that precedes a Flag or Pennant formation.
    /// </summary>
    public const decimal GeometricPoleMinPercent = 5.0m;

    /// <summary>
    /// Minimum R-squared (coefficient of determination) for a trendline to be considered valid.
    /// Increased to 0.75 for stricter geometric classification.
    /// </summary>
    public const double GeometricMinRSquared = 0.75;

    /// <summary>
    /// Maximum allowed deviation (breakout) from the drawn pattern lines by candle prices.
    /// If prices pierce the trendlines by more than this percentage (0.005 = 0.5%), the pattern is invalidated.
    /// </summary>
    public const decimal GeometricBreakoutTolerance = 0.005m;

    /// <summary>
    /// Maximum slope magnitude for a trendline to be considered "flat" (horizontal).
    /// Used for ascending/descending triangle detection.
    /// </summary>
    public const double GeometricFlatSlopeThreshold = 0.05;

    // Harmonic Pattern Detection (Prompt 33-9)
    /// <summary>Default ZigZag threshold percentage for harmonic pattern pivot extraction.</summary>
    public const decimal DefaultHarmonicZigZagThreshold = 3.0m;

    /// <summary>Multi-scale thresholds to capture harmonic patterns at different scales.</summary>
    public static readonly decimal[] HarmonicMultiScaleThresholds = { 2.0m, 3.0m, 5.0m, 8.0m };

    /// <summary>Minimum number of pivot points required to form the 5-point XABCD pattern.</summary>
    public const int HarmonicMinPivotCount = 5;

    /// <summary>
    /// Default tolerance for Fibonacci ratio matching.
    /// Relaxed to ±15% to allow for real-market noise.
    /// </summary>
    public const double HarmonicDefaultTolerance = 0.15;

    /// <summary>
    /// Tolerance for ratios defined as ranges (used additively on both bounds).
    /// Relaxed to ±10%.
    /// </summary>
    public const double HarmonicRangeTolerance = 0.10;

    /// <summary>Minimum confidence score for a harmonic pattern to be considered valid.</summary>
    public const double HarmonicMinConfidence = 0.40;

    /// <summary>
    /// Multiscale bonus weight: log2(span) is multiplied by this factor
    /// and added to the raw confidence score to prefer larger patterns.
    /// </summary>
    public const double HarmonicMultiscaleWeight = 0.02;

    /// <summary>PRZ (Potential Reversal Zone) expansion factor applied symmetrically around the ideal D price.</summary>
    public const decimal HarmonicPrzExpansionPercent = 2.0m;

    /// <summary>
    /// Navarro 200 time-zone constraint: minimum allowed ratio of Duration(C-D) / Duration(X-A).
    /// Based on the Fibonacci sequence lower bound.
    /// </summary>
    public const double Navarro200TimeRatioMin = 0.382;

    /// <summary>
    /// Navarro 200 time-zone constraint: maximum allowed ratio of Duration(C-D) / Duration(X-A).
    /// Based on the Fibonacci sequence upper bound.
    /// </summary>
    public const double Navarro200TimeRatioMax = 2.618;

    // Elliott Wave Detection (Prompt 33-13)
    /// <summary>Default ZigZag threshold percentage for Elliott Wave pivot extraction.</summary>
    public const decimal DefaultElliottZigZagThreshold = 5.0m;

    /// <summary>Multi-scale thresholds for capturing Elliott Wave patterns at different zoom levels.</summary>
    public static readonly decimal[] ElliottMultiScaleThresholds = { 3.0m, 5.0m, 8.0m };

    /// <summary>Minimum number of candles required to attempt Elliott Wave detection.</summary>
    public const int ElliottMinCandleCount = 20;

    /// <summary>Minimum number of pivot points required for an impulse wave (5 waves = 6 pivots).</summary>
    public const int ElliottMinPivotCountImpulse = 6;

    /// <summary>Minimum number of pivot points required for a corrective wave (3 waves = 4 pivots).</summary>
    public const int ElliottMinPivotCountCorrective = 4;

    /// <summary>
    /// Tolerance for Fibonacci ratio matching in Elliott Wave scoring.
    /// Relaxed to ±20% to allow for real-market noise in wave proportions.
    /// </summary>
    public const double ElliottFibonacciTolerance = 0.20;

    /// <summary>Minimum confidence score for an Elliott Wave pattern to be considered valid.</summary>
    public const double ElliottMinConfidence = 0.30;

    /// <summary>
    /// Multiscale bonus weight: log2(span) is multiplied by this factor
    /// and added to the raw confidence score to prefer larger, more significant wave structures.
    /// </summary>
    public const double ElliottMultiscaleWeight = 0.02;

    /// <summary>
    /// Tolerance for Wave 4 / Wave 1 overlap check.
    /// Allows minor wick violations up to this fraction of Wave 1 range.
    /// 0.05 = 5% of Wave 1 range allowed as noise.
    /// </summary>
    public const decimal ElliottOverlapTolerance = 0.05m;

    /// <summary>Number of candles to look back for momentum calculation at wave endpoints.</summary>
    public const int ElliottMomentumLookback = 5;

    // Pattern Formation Process Validation (Prompt 60-7)
    /// <summary>Minimum bars (span) for a Harmonic XABCD pattern to be considered valid.</summary>
    public const int FormationMinBarsHarmonic = 8; // Reduced from 15 to allow smaller UI settings to work

    /// <summary>Minimum bars (span) for an Elliott Wave pattern to be considered valid.</summary>
    public const int FormationMinBarsElliott = 13; // Reduced from 20

    /// <summary>Minimum bars (span) for a Geometric Formation to be considered valid.</summary>
    public const int FormationMinBarsGeometric = 10; // Reduced from 15

    /// <summary>
    /// ATR multiplier for per-leg volatility validation.
    /// Each pattern leg must move at least ATR * this value to be considered significant.
    /// </summary>
    public const double FormationVolatilityAtrMultiplier = 0.3; // Reduced from 0.5 to be less aggressive

    /// <summary>
    /// Maximum allowed ratio between the longest and shortest leg durations.
    /// Prevents patterns with extremely asymmetric timing (e.g., one leg = 1 bar, another = 50 bars).
    /// </summary>
    public const double FormationMaxTimeRatio = 8.0; // Increased from 5.0 to allow sharp 1-bar spikes with normal legs

    /// <summary>
    /// Minimum number of bars between anchor points in target price projections.
    /// Projections with closer anchors are suppressed to avoid noise.
    /// </summary>
    public const int TargetProjectionMinAnchorBars = 5;

    // Candle Pattern ATR-Based Thresholds (Prompt 60-7 Step 4)

    /// <summary>Lookback period for ATR calculation in candle pattern detection.</summary>
    public const int CandlePatternAtrPeriod = 14;

    /// <summary>Body/ATR ratio below which a candle is classified as Doji.</summary>
    public const decimal CandlePatternDojiAtrRatio = 0.05m;

    /// <summary>Body/ATR ratio above which a candle body is classified as Large.</summary>
    public const decimal CandlePatternLargeBodyAtrRatio = 0.50m;

    /// <summary>Body/ATR ratio below which a candle body is classified as Small.</summary>
    public const decimal CandlePatternSmallBodyAtrRatio = 0.25m;

    /// <summary>
    /// Minimum candle body size relative to ATR for classical candlestick patterns.
    /// Bodies smaller than ATR * this ratio are considered noise.
    /// </summary>
    public const double CandlePatternAtrMinBodyRatio = 0.3;

    /// <summary>
    /// Maximum wick size relative to ATR for noise wick filtering in candlestick patterns.
    /// Wicks smaller than ATR * this ratio are ignored as noise.
    /// </summary>
    public const double CandlePatternAtrMaxWickRatio = 0.1;

    /// <summary>
    /// Default warping radius for DTW Sakoe-Chiba constraint.
    /// Limits time stretching to prevent matching short patterns to long trends.
    /// </summary>
    public const int DtwDefaultWarpingRadius = IndicatorDefaultConstants.DtwDefaultWarpingRadius;

    /// <summary>
    /// Alpha parameter for the short span penalty in pattern detection.
    /// Determines the exponential curve of the span constraint.
    /// </summary>
    public const double DtwShortSpanPenaltyAlpha = 0.5;

    /// <summary>
    /// Default rolling window size (candles) for FFT Cycle dominant-cycle detection.
    /// Must be a power of two per the Hanning-window/FFT spectral analysis spec.
    /// </summary>
    public const int FftCycleDefaultWindowSize = IndicatorDefaultConstants.FftCycleDefaultWindowSize;

    /// <summary>
    /// Default target period (candles) for the Goertzel-based Fourier Transform
    /// cycle-strength indicator.
    /// </summary>
    public const int FourierTransformDefaultTargetPeriod = IndicatorDefaultConstants.FourierTransformDefaultTargetPeriod;

    /// <summary>
    /// Default rolling window size (candles) for the FFT Trend Filter low-pass reconstruction.
    /// </summary>
    public const int FftTrendFilterDefaultWindowSize = IndicatorDefaultConstants.FftTrendFilterDefaultWindowSize;

    /// <summary>
    /// Default number of low-frequency FFT bins (including DC) kept by the FFT Trend Filter;
    /// higher bins are zeroed out before the inverse transform.
    /// </summary>
    public const int FftTrendFilterDefaultNumHarmonics = IndicatorDefaultConstants.FftTrendFilterDefaultNumHarmonics;

    /// <summary>Default rolling window size (candles) for Singular Spectrum Analysis (SSA).</summary>
    public const int SsaDefaultWindowSize = IndicatorDefaultConstants.SsaDefaultWindowSize;

    /// <summary>Default embedding dimension (lag window length L) for Singular Spectrum Analysis (SSA).</summary>
    public const int SsaDefaultEmbeddingDimension = IndicatorDefaultConstants.SsaDefaultEmbeddingDimension;

    /// <summary>Default number of principal reconstructed components (r) for Singular Spectrum Analysis (SSA).</summary>
    public const int SsaDefaultNumComponents = IndicatorDefaultConstants.SsaDefaultNumComponents;

    // Logic Thresholds
    /// <summary>Standard future offset for Ichimoku Kumo projections (26 periods + buffer).</summary>
    public const int IchimokuStandardFutureOffset = 31;

    /// <summary>Default trend change threshold for Three Line Break conversion.</summary>
    public const decimal ThreeLineBreakTrendThreshold = 0.0001m;

    #region Continuation Candle Pattern Thresholds (Prompt 70-1 Strict)

    /// <summary>大陽線・大陰線の判定における平均実体サイズ倍率 (1.5倍)。</summary>
    public const decimal CandlePatternLargeBodyRatio = 1.5m;

    /// <summary>大足判定において実体がレンジに占める最小比率 (60%)。</summary>
    public const decimal CandlePatternLargeRangeRatio = 0.60m;

    /// <summary>小足判定における平均実体サイズ上限比率 (80%)。</summary>
    public const decimal CandlePatternSmallBodyRatio = 0.80m;

    /// <summary>小足判定において実体がレンジに占める最大比率 (50%)。</summary>
    public const decimal CandlePatternSmallRangeRatio = 0.50m;

    /// <summary>ブレイクアウト足（5本目等）の最小実体サイズ倍率 (1.0倍)。</summary>
    public const decimal CandlePatternBreakoutBodyMinRatio = 1.0m;

    /// <summary>三手一撃におけるStrike足の先行3本実体合算に対する最小比率 (85%)。</summary>
    public const decimal CandlePatternStrikeBodyDominanceRatio = 0.85m;

    /// <summary>三手一撃におけるStrike足の始値許容バッファ比率 (avgBodyの10%)。</summary>
    public const decimal CandlePatternStrikeOpenToleranceRatio = 0.10m;

    /// <summary>赤三兵・黒三兵構造における足の実体最小比率 (ノイズ除外: avgBodyの50%)。</summary>
    public const decimal CandlePatternNoiseBodyMinRatio = 0.50m;

    /// <summary>明確な窓（Gap）判定の最小比率 (avgBodyの10%)。</summary>
    public const decimal CandlePatternGapMinRatio = 0.10m;

    /// <summary>並び赤における2本の陽線の実体サイズ許容乖離率 (30%以内)。</summary>
    public const decimal CandlePatternSimilarBodyToleranceRatio = 0.30m;

    /// <summary>並び赤における始値の近傍許容乖離率 (30%以内)。</summary>
    public const decimal CandlePatternSimilarOpenToleranceRatio = 0.30m;

    #endregion

    #region Advanced Reversal Candle Pattern Thresholds (Prompt 70-2 Strict)

    /// <summary>先詰まり (Advance Block) における上ヒゲの対実体最小比率 (50%)。</summary>
    public const decimal CandlePatternAdvanceBlockShadowRatio = 0.50m;

    /// <summary>先詰まり (Advance Block) における3本目実体の1本目に対する累積最大縮小比率 (85%以下、すなわち15%以上の縮小)。</summary>
    public const decimal CandlePatternAdvanceBlockBodyReductionRatio = 0.85m;

    /// <summary>思案 (Deliberation) における1本目・2本目陽線の平均実体対比最小サイズ (70%)。</summary>
    public const decimal CandlePatternDeliberationMainBodyRatio = 0.70m;

    /// <summary>思案 (Deliberation) における3本目Star足の2本目実体対比最大サイズ (40%)。</summary>
    public const decimal CandlePatternDeliberationStarBodyRatio = 0.40m;

    /// <summary>思案 (Deliberation) における3本目の寄付き許容バッファ比率 (avgBodyの15%)。</summary>
    public const decimal CandlePatternDeliberationOpenBufferRatio = 0.15m;

    /// <summary>スティックサンドイッチ (Stick Sandwich) における3本目の寄付き許容バッファ比率 (avgBodyの10%)。</summary>
    public const decimal CandlePatternStickSandwichOpenBufferRatio = 0.10m;

    /// <summary>スティックサンドイッチ (Stick Sandwich) における2本の陰線終値の許容乖離比率 (avgBodyの5%)。</summary>
    public const decimal CandlePatternPriceEqualityToleranceRatio = 0.05m;

    /// <summary>スティックサンドイッチ (Stick Sandwich) における価格相対トレランス比率 (終値の0.2%)。</summary>
    public const decimal CandlePatternPriceEqualityAbsoluteMinRatio = 0.002m;

    /// <summary>梯子底 (Ladder Bottom) における4本目足の最小ヒゲ/実体比率 (1.0倍)。</summary>
    public const decimal CandlePatternLadderShadowRatio = 1.0m;

    #endregion

    #region Gap Candle Pattern Thresholds (Prompt 70-3 Strict)

    /// <summary>タスキ窓 (Tasuki Gap) における3本目の寄付き許容バッファ比率 (avgBodyの20%)。</summary>
    public const decimal CandlePatternTasukiOpenToleranceRatio = 0.20m;

    /// <summary>離脱 (Breakaway) パターンにおける5本目反発足の最小実体比率 (avgBodyの1.2倍)。</summary>
    public const decimal CandlePatternBreakawayBodyRatio = 1.2m;

    #endregion

    #region Exotic & Minor Candle Pattern Thresholds (Prompt 70-4 Strict v3)

    /// <summary>丸坊主判定におけるヒゲの対レンジ最大許容比率 (5%以下)。</summary>
    public const decimal CandlePatternMarubozuShadowMaxRatio = 0.05m;

    /// <summary>Kicking パターンにおける窓開けの最小ATR倍率 (ATRの50%)。</summary>
    public const decimal CandlePatternKickingGapAtrRatio = 0.50m;

    /// <summary>Kicking パターンにおける窓開けの最小平均実体倍率 (avgBodyの20%)。</summary>
    public const decimal CandlePatternKickingGapBodyRatio = 0.20m;

    /// <summary>Concealing Baby Swallow 3本目における上ヒゲの対実体最小比率 (80%以上)。</summary>
    public const decimal CandlePatternConcealSwallowShadowRatio = 0.80m;

    /// <summary>Identical Three Crows における始値と同値判定の許容乖離比率 (avgBodyの5%)。</summary>
    public const decimal CandlePatternIdenticalCrowsOpenToleranceRatio = 0.05m;

    /// <summary>Identical Three Crows における各陰線の下ヒゲ許容上限比率 (実体の15%以下)。</summary>
    public const decimal CandlePatternIdenticalCrowsShadowMaxRatio = 0.15m;

    #endregion
}

