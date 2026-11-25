using System.ComponentModel;
using StockAnalyzer.Core.Models.Indicators.PatternFlags;

namespace StockAnalyzer.Core.Models.Parameters
{
    /// <summary>
    /// The settings Head and Shoulders and Double Top / Bottom have in common (plan ChartPatterns56 v2, run V2-3; integration spec 5.1): one definition of the properties for the two parameter classes
    /// <see cref="CoreHeadAndShouldersParameter"/> and <see cref="CoreDoubleTopBottomParameter"/> (decision D-V2-3 A: one class per indicator). The two types differ only in four defaults, which the
    /// derived class passes to the constructor, and in the settings that only one of them has. Abstract, so it is never a JSON discriminator; the two derived class names are permanent.
    /// </summary>
    public abstract class CoreFormationPatternParameterBase : CoreIndicatorParameterBase
    {
        /// <summary>The defaults in which Head and Shoulders and Double Top / Bottom differ (spec 5.1).</summary>
        protected readonly record struct FormationDefaults(decimal PriorTrendMinPercent, decimal MinNecklineDepthPercent, int MaxBreakoutWaitBars, decimal BreakoutVolumeMultiplier);

        private PatternDirectionMode _directionMode = PatternDirectionMode.Both;
        private decimal _zigZagThresholdPercent = PatternFlagParameterLimits.FormationZigZagDefault;
        private decimal _minSwingAtrMultiplier = PatternFlagParameterLimits.FormationMinSwingAtrDefault;
        private int _fractalWindowBars = PatternFlagParameterLimits.FormationFractalBarsDefault;
        private bool _requirePriorTrend = true;
        private int _priorTrendLookbackBars = PatternFlagParameterLimits.FormationPriorTrendLookbackDefault;
        private decimal _priorTrendMinPercent;
        private int _priceZoneLookbackBars = PatternFlagParameterLimits.FormationZoneLookbackDefault;
        private decimal _minNecklineDepthPercent;
        private int _maxBreakoutWaitBars;
        private decimal _breakoutMarginPercent = PatternFlagParameterLimits.FormationBreakoutMarginPercentDefault;
        private bool _requireVolumeConfirmation = true;
        private decimal _breakoutVolumeMultiplier;
        private int _signalPersistenceBars = PatternFlagParameterLimits.FormationSignalPersistenceDefault;

        protected CoreFormationPatternParameterBase(FormationDefaults defaults)
        {
            _priorTrendMinPercent = defaults.PriorTrendMinPercent;
            _minNecklineDepthPercent = defaults.MinNecklineDepthPercent;
            _maxBreakoutWaitBars = defaults.MaxBreakoutWaitBars;
            _breakoutVolumeMultiplier = defaults.BreakoutVolumeMultiplier;
        }

        [DisplayName("Direction Mode")]
        [Description("Both = a top and a bottom pattern set the flag. Bullish Only / Bearish Only = only that direction can be found; the other one is not searched, so it never wins a tie and Main and Direction stay 0 for it.")]
        [Category("General")]
        [EnumDisplaySort(SortAlphabetically = false)]
        public PatternDirectionMode DirectionMode
        {
            get => _directionMode;
            set => SetProperty(ref _directionMode, value);
        }

        [DisplayName("ZigZag Threshold (%)")]
        [Description("Minimum percentage swing that makes a pivot. Larger values give fewer, larger pivots. Unit: percent of price.")]
        [Category("Swing")]
        [CoreParameterRange(PatternFlagParameterLimits.ZigZagMin, PatternFlagParameterLimits.FormationZigZagMax)]
        public decimal ZigZagThresholdPercent
        {
            get => _zigZagThresholdPercent;
            set => SetProperty(ref _zigZagThresholdPercent, value);
        }

        [DisplayName("Min Swing (ATR)")]
        [Description("A swing must also span at least this many average true ranges (14 bars) to make a pivot. 0 switches the test off. Unit: multiples of ATR.")]
        [Category("Swing")]
        [CoreParameterRange(PatternFlagParameterLimits.FormationMinSwingAtrMin, PatternFlagParameterLimits.FormationMinSwingAtrMax)]
        public decimal MinSwingAtrMultiplier
        {
            get => _minSwingAtrMultiplier;
            set => SetProperty(ref _minSwingAtrMultiplier, value);
        }

        [DisplayName("Fractal Window (bars)")]
        [Description("A pivot is confirmed only when this many bars on each side are not more extreme, so it is confirmed this many bars after it. Unit: bars.")]
        [Category("Swing")]
        [CoreParameterRange(PatternFlagParameterLimits.FormationFractalBarsMin, PatternFlagParameterLimits.FormationFractalBarsMax)]
        public int FractalWindowBars
        {
            get => _fractalWindowBars;
            set => SetProperty(ref _fractalWindowBars, value);
        }

        [DisplayName("Require Prior Trend")]
        [Description("When on, a prior move in the direction of the pattern must come before it (up before a top, down before a bottom). Off = only the price zone and the geometry decide.")]
        [Category("Trend & Zone")]
        public bool RequirePriorTrend
        {
            get => _requirePriorTrend;
            set => SetProperty(ref _requirePriorTrend, value);
        }

        [DisplayName("Prior Trend Lookback (bars)")]
        [Description("Bars before the pattern start in which the prior move is measured (the start bar itself is not included). Double Top / Bottom also measures the width of the prior move for its retrace limit over the same bars. Unit: bars.")]
        [Category("Trend & Zone")]
        [CoreParameterRange(PatternFlagParameterLimits.FormationPriorTrendLookbackMin, PatternFlagParameterLimits.FormationPriorTrendLookbackMax)]
        public int PriorTrendLookbackBars
        {
            get => _priorTrendLookbackBars;
            set => SetProperty(ref _priorTrendLookbackBars, value);
        }

        [DisplayName("Prior Trend Min Move (%)")]
        [Description("Minimum prior move, as percent of the low (up-trend) or high (down-trend) of the lookback; a move of at least the ATR multiple of the specification also counts. Unit: percent.")]
        [Category("Trend & Zone")]
        [CoreParameterRange(PatternFlagParameterLimits.FormationPriorTrendMinPercentMin, PatternFlagParameterLimits.FormationPriorTrendMinPercentMax)]
        public decimal PriorTrendMinPercent
        {
            get => _priorTrendMinPercent;
            set => SetProperty(ref _priorTrendMinPercent, value);
        }

        [DisplayName("Price Zone Lookback (bars)")]
        [Description("Bars of the price range in which the peaks must be in the upper (top) or lower (bottom) part. Unit: bars.")]
        [Category("Trend & Zone")]
        [CoreParameterRange(PatternFlagParameterLimits.FormationZoneLookbackMin, PatternFlagParameterLimits.FormationZoneLookbackMax)]
        public int PriceZoneLookbackBars
        {
            get => _priceZoneLookbackBars;
            set => SetProperty(ref _priceZoneLookbackBars, value);
        }

        [DisplayName("Min Neckline Depth (%)")]
        [Description("Minimum depth of the pattern as percent of the reference price; a depth of at least 2.5 ATR that is also at least 2 percent counts as well. Unit: percent.")]
        [Category("Geometry")]
        [CoreParameterRange(PatternFlagParameterLimits.FormationNecklineDepthPercentMin, PatternFlagParameterLimits.FormationNecklineDepthPercentMax)]
        public decimal MinNecklineDepthPercent
        {
            get => _minNecklineDepthPercent;
            set => SetProperty(ref _minNecklineDepthPercent, value);
        }

        [DisplayName("Max Breakout Wait (bars)")]
        [Description("Most bars from the last pivot of the pattern to the break bar. It must not be smaller than the fractal window, because the last pivot is only confirmed that many bars after it. Unit: bars.")]
        [Category("Duration")]
        [CoreParameterRange(PatternFlagParameterLimits.FormationBreakoutWaitMin, PatternFlagParameterLimits.FormationBreakoutWaitMax)]
        public int MaxBreakoutWaitBars
        {
            get => _maxBreakoutWaitBars;
            set => SetProperty(ref _maxBreakoutWaitBars, value);
        }

        [DisplayName("Breakout Margin (%)")]
        [Description("The close must pass the neckline by this percent of the neckline price, or by 0.25 ATR if that is more. 0 = no margin at all: a close beyond the neckline is enough (strict inequality, the ATR term is off too). Unit: percent.")]
        [Category("Trigger")]
        [CoreParameterRange(PatternFlagParameterLimits.FormationBreakoutMarginPercentMin, PatternFlagParameterLimits.FormationBreakoutMarginPercentMax)]
        public decimal BreakoutMarginPercent
        {
            get => _breakoutMarginPercent;
            set => SetProperty(ref _breakoutMarginPercent, value);
        }

        [DisplayName("Require Volume Confirmation")]
        [Description("When on, the break bar needs a volume of at least the multiple below of the average of the 20 bars before it. Instruments without volume pass.")]
        [Category("Trigger")]
        public bool RequireVolumeConfirmation
        {
            get => _requireVolumeConfirmation;
            set => SetProperty(ref _requireVolumeConfirmation, value);
        }

        [DisplayName("Breakout Volume Multiplier")]
        [Description("Minimum volume of the break bar as a multiple of the 20-bar average volume before it. Unit: multiple.")]
        [Category("Trigger")]
        [CoreParameterRange(PatternFlagParameterLimits.FormationBreakoutVolumeMultiplierMin, PatternFlagParameterLimits.FormationBreakoutVolumeMultiplierMax)]
        public decimal BreakoutVolumeMultiplier
        {
            get => _breakoutVolumeMultiplier;
            set => SetProperty(ref _breakoutVolumeMultiplier, value);
        }

        [DisplayName("Signal Persistence (bars)")]
        [Description("The flag is 1 on the break bar and on this many bars after it, unless the pattern is invalidated first (0 = the break bar only). Unit: bars.")]
        [Category("Trigger")]
        [CoreParameterRange(PatternFlagParameterLimits.FormationSignalPersistenceMin, PatternFlagParameterLimits.FormationSignalPersistenceMax)]
        public int SignalPersistenceBars
        {
            get => _signalPersistenceBars;
            set => SetProperty(ref _signalPersistenceBars, value);
        }

        public override string GetDisplayName(string type) => $"{type} ({ZigZagThresholdPercent}%)";

        /// <summary>Bars requested as warm-up (a request, never a minimum; decision U2-16 A).</summary>
        public override int GetRequiredWarmupBars() => PatternFlagParameterLimits.WarmupBars;

        public override void Validate()
        {
            if (!System.Enum.IsDefined(DirectionMode)) throw new System.ArgumentOutOfRangeException(nameof(DirectionMode));
            ThrowIfOutOfRange(ZigZagThresholdPercent, PatternFlagParameterLimits.ZigZagMin, PatternFlagParameterLimits.FormationZigZagMax, nameof(ZigZagThresholdPercent));
            ThrowIfOutOfRange(MinSwingAtrMultiplier, PatternFlagParameterLimits.FormationMinSwingAtrMin, PatternFlagParameterLimits.FormationMinSwingAtrMax, nameof(MinSwingAtrMultiplier));
            ThrowIfOutOfRange(FractalWindowBars, PatternFlagParameterLimits.FormationFractalBarsMin, PatternFlagParameterLimits.FormationFractalBarsMax, nameof(FractalWindowBars));
            ThrowIfOutOfRange(PriorTrendLookbackBars, PatternFlagParameterLimits.FormationPriorTrendLookbackMin, PatternFlagParameterLimits.FormationPriorTrendLookbackMax, nameof(PriorTrendLookbackBars));
            ThrowIfOutOfRange(PriorTrendMinPercent, PatternFlagParameterLimits.FormationPriorTrendMinPercentMin, PatternFlagParameterLimits.FormationPriorTrendMinPercentMax, nameof(PriorTrendMinPercent));
            ThrowIfOutOfRange(PriceZoneLookbackBars, PatternFlagParameterLimits.FormationZoneLookbackMin, PatternFlagParameterLimits.FormationZoneLookbackMax, nameof(PriceZoneLookbackBars));
            ThrowIfOutOfRange(MinNecklineDepthPercent, PatternFlagParameterLimits.FormationNecklineDepthPercentMin, PatternFlagParameterLimits.FormationNecklineDepthPercentMax, nameof(MinNecklineDepthPercent));
            ThrowIfOutOfRange(MaxBreakoutWaitBars, PatternFlagParameterLimits.FormationBreakoutWaitMin, PatternFlagParameterLimits.FormationBreakoutWaitMax, nameof(MaxBreakoutWaitBars));
            ThrowIfOutOfRange(BreakoutMarginPercent, PatternFlagParameterLimits.FormationBreakoutMarginPercentMin, PatternFlagParameterLimits.FormationBreakoutMarginPercentMax, nameof(BreakoutMarginPercent));
            ThrowIfOutOfRange(BreakoutVolumeMultiplier, PatternFlagParameterLimits.FormationBreakoutVolumeMultiplierMin, PatternFlagParameterLimits.FormationBreakoutVolumeMultiplierMax, nameof(BreakoutVolumeMultiplier));
            ThrowIfOutOfRange(SignalPersistenceBars, PatternFlagParameterLimits.FormationSignalPersistenceMin, PatternFlagParameterLimits.FormationSignalPersistenceMax, nameof(SignalPersistenceBars));

            // Plan R-19: the last pivot is confirmed FractalWindowBars after it, so a shorter wait could never fire: a silent loss of the pattern, refused instead.
            if (MaxBreakoutWaitBars < FractalWindowBars) throw new System.ArgumentOutOfRangeException(nameof(MaxBreakoutWaitBars), "The breakout wait must not be shorter than the fractal window.");
        }
    }
}
