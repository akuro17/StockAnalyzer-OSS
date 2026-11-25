using System;
using System.ComponentModel;
using StockAnalyzer.Core.Models.Indicators.PatternFlags;

namespace StockAnalyzer.Core.Models.Parameters
{
    /// <summary>
    /// Shared parameter base of the Elliott Wave Impulse and Corrective flags (plan ChartPatterns56 v2, run V2-7; integration spec 5.5).
    /// NEVER rename or move the concrete subclasses: their names are JSON type discriminators.
    /// </summary>
    public abstract class CoreElliottPatternParameterBase : CoreIndicatorParameterBase
    {
        private PatternDirectionMode _directionMode = PatternDirectionMode.Both;
        private decimal _zigZagThresholdPercent = PatternFlagParameterLimits.ElliottZigZagDefault;
        private decimal _minSwingAtrMultiplier = PatternFlagParameterLimits.ElliottMinSwingAtrDefault;
        private bool _requireOriginPriceZone = true;
        private int _minSpanBars;
        private int _maxSpanBars;
        private decimal _maxTimeSymmetryRatio = PatternFlagParameterLimits.ElliottMaxTimeSymmetryRatioDefault;
        private decimal _fibonacciTolerance = PatternFlagParameterLimits.ElliottFibonacciToleranceDefault;
        private decimal _minConfidenceScore = PatternFlagParameterLimits.ElliottMinConfidenceScoreDefault;
        private int _maxBarsSinceConfirmation = PatternFlagParameterLimits.ElliottMaxBarsSinceConfirmationDefault;

        protected CoreElliottPatternParameterBase(int defaultMinSpanBars, int defaultMaxSpanBars)
        {
            _minSpanBars = defaultMinSpanBars;
            _maxSpanBars = defaultMaxSpanBars;
        }

        [DisplayName("Direction Mode")]
        [Description("Both = bullish and bearish waves set the flag. Bullish Only / Bearish Only = only that direction sets the flag; the other side gives 0 on Main and Direction.")]
        [Category("General")]
        [EnumDisplaySort(SortAlphabetically = false)]
        public PatternDirectionMode DirectionMode
        {
            get => _directionMode;
            set => SetProperty(ref _directionMode, value);
        }

        [DisplayName("ZigZag Threshold (%)")]
        [Description("Minimum percentage swing for the causal ZigZag pivots forming the wave structure. Unit: percent of price.")]
        [Category("Swing")]
        [CoreParameterRange(PatternFlagParameterLimits.ZigZagMin, PatternFlagParameterLimits.ElliottZigZagMax)]
        public decimal ZigZagThresholdPercent
        {
            get => _zigZagThresholdPercent;
            set => SetProperty(ref _zigZagThresholdPercent, value);
        }

        [DisplayName("Min Swing (ATR)")]
        [Description("Each ZigZag swing must span at least this many context ATRs (14 bars). 0 switches the ATR test off. Unit: multiples of ATR.")]
        [Category("Swing")]
        [CoreParameterRange(PatternFlagParameterLimits.ElliottMinSwingAtrMin, PatternFlagParameterLimits.ElliottMinSwingAtrMax)]
        public decimal MinSwingAtrMultiplier
        {
            get => _minSwingAtrMultiplier;
            set => SetProperty(ref _minSwingAtrMultiplier, value);
        }

        [DisplayName("Require Origin Price Zone")]
        [Description("When on, the wave origin p0 must sit in the 40-bar low zone (<= 0.25) for bullish or high zone (>= 0.75) for bearish.")]
        [Category("Trend & Zone")]
        public bool RequireOriginPriceZone
        {
            get => _requireOriginPriceZone;
            set => SetProperty(ref _requireOriginPriceZone, value);
        }

        [DisplayName("Min Span (bars)")]
        [Description("Smallest number of bars from the wave origin p0 to the final pivot. Unit: bars.")]
        [Category("Duration")]
        [CoreParameterRange(PatternFlagParameterLimits.ElliottMinSpanBarsMin, PatternFlagParameterLimits.ElliottMinSpanBarsMax)]
        public int MinSpanBars
        {
            get => _minSpanBars;
            set => SetProperty(ref _minSpanBars, value);
        }

        [DisplayName("Max Span (bars)")]
        [Description("Largest number of bars from the wave origin p0 to the final pivot. Unit: bars.")]
        [Category("Duration")]
        [CoreParameterRange(PatternFlagParameterLimits.ElliottMaxSpanBarsMin, PatternFlagParameterLimits.ElliottMaxSpanBarsMax)]
        public int MaxSpanBars
        {
            get => _maxSpanBars;
            set => SetProperty(ref _maxSpanBars, value);
        }

        [DisplayName("Max Time Symmetry Ratio")]
        [Description("Largest allowed ratio of the longest wave to max(3, shortest wave). Unit: ratio.")]
        [Category("Duration")]
        [CoreParameterRange(PatternFlagParameterLimits.ElliottMaxTimeSymmetryRatioMin, PatternFlagParameterLimits.ElliottMaxTimeSymmetryRatioMax)]
        public decimal MaxTimeSymmetryRatio
        {
            get => _maxTimeSymmetryRatio;
            set => SetProperty(ref _maxTimeSymmetryRatio, value);
        }

        [DisplayName("Fibonacci Tolerance")]
        [Description("Allowed +/- deviation around each wave ratio's ideal Fibonacci interval. Unit: ratio.")]
        [Category("Scoring")]
        [CoreParameterRange(PatternFlagParameterLimits.ElliottFibonacciToleranceMin, PatternFlagParameterLimits.ElliottFibonacciToleranceMax)]
        public decimal FibonacciTolerance
        {
            get => _fibonacciTolerance;
            set => SetProperty(ref _fibonacciTolerance, value);
        }

        [DisplayName("Min Confidence Score")]
        [Description("Minimum weighted Fibonacci trapezoidal score required to accept the wave pattern. Unit: 0 to 1.")]
        [Category("Scoring")]
        [CoreParameterRange(PatternFlagParameterLimits.ElliottMinConfidenceScoreMin, PatternFlagParameterLimits.ElliottMinConfidenceScoreMax)]
        public decimal MinConfidenceScore
        {
            get => _minConfidenceScore;
            set => SetProperty(ref _minConfidenceScore, value);
        }

        [DisplayName("Max Bars Since Confirmation")]
        [Description("Bars after the final pivot confirmation during which the flag stays active (0 = confirmation bar only). Unit: bars.")]
        [Category("Trigger")]
        [CoreParameterRange(PatternFlagParameterLimits.ElliottMaxBarsSinceConfirmationMin, PatternFlagParameterLimits.ElliottMaxBarsSinceConfirmationMax)]
        public int MaxBarsSinceConfirmation
        {
            get => _maxBarsSinceConfirmation;
            set => SetProperty(ref _maxBarsSinceConfirmation, value);
        }

        public override string GetDisplayName(string type) => $"{type} ({ZigZagThresholdPercent}%)";

        /// <summary>Bars requested as warm-up (a request, never a minimum; decision U2-16 A).</summary>
        public override int GetRequiredWarmupBars() => PatternFlagParameterLimits.WarmupBars;

        public override void Validate()
        {
            if (!Enum.IsDefined(DirectionMode)) throw new ArgumentOutOfRangeException(nameof(DirectionMode));
            ThrowIfOutOfRange(ZigZagThresholdPercent, PatternFlagParameterLimits.ZigZagMin, PatternFlagParameterLimits.ElliottZigZagMax, nameof(ZigZagThresholdPercent));
            ThrowIfOutOfRange(MinSwingAtrMultiplier, PatternFlagParameterLimits.ElliottMinSwingAtrMin, PatternFlagParameterLimits.ElliottMinSwingAtrMax, nameof(MinSwingAtrMultiplier));
            ThrowIfOutOfRange(MinSpanBars, PatternFlagParameterLimits.ElliottMinSpanBarsMin, PatternFlagParameterLimits.ElliottMinSpanBarsMax, nameof(MinSpanBars));
            ThrowIfOutOfRange(MaxSpanBars, PatternFlagParameterLimits.ElliottMaxSpanBarsMin, PatternFlagParameterLimits.ElliottMaxSpanBarsMax, nameof(MaxSpanBars));
            ThrowIfOutOfRange(MaxTimeSymmetryRatio, PatternFlagParameterLimits.ElliottMaxTimeSymmetryRatioMin, PatternFlagParameterLimits.ElliottMaxTimeSymmetryRatioMax, nameof(MaxTimeSymmetryRatio));
            ThrowIfOutOfRange(FibonacciTolerance, PatternFlagParameterLimits.ElliottFibonacciToleranceMin, PatternFlagParameterLimits.ElliottFibonacciToleranceMax, nameof(FibonacciTolerance));
            ThrowIfOutOfRange(MinConfidenceScore, PatternFlagParameterLimits.ElliottMinConfidenceScoreMin, PatternFlagParameterLimits.ElliottMinConfidenceScoreMax, nameof(MinConfidenceScore));
            ThrowIfOutOfRange(MaxBarsSinceConfirmation, PatternFlagParameterLimits.ElliottMaxBarsSinceConfirmationMin, PatternFlagParameterLimits.ElliottMaxBarsSinceConfirmationMax, nameof(MaxBarsSinceConfirmation));

            if (MinSpanBars > MaxSpanBars) throw new ArgumentOutOfRangeException(nameof(MinSpanBars), "MinSpanBars must not exceed MaxSpanBars.");
        }
    }
}
