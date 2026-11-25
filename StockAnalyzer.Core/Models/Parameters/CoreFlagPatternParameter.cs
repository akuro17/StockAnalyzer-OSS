using System.ComponentModel;
using StockAnalyzer.Core.Models.Indicators.PatternFlags;

namespace StockAnalyzer.Core.Models.Parameters
{
    /// <summary>
    /// Parameters of the Flag pattern flag (plan ChartPatterns56 v2, run V2-4, integration spec 5.1 class 3; engine <c>FlagEngine</c>). NEVER rename or move: the class name is the JSON type discriminator of the
    /// workspace. The pole and the flag channel are found from the bars alone (no ZigZag), so there is no pivot setting.
    /// </summary>
    public class CoreFlagPatternParameter : CoreIndicatorParameterBase
    {
        private PatternDirectionMode _directionMode = PatternDirectionMode.Both;
        private int _minPoleBars = PatternFlagParameterLimits.FlagMinPoleBarsDefault;
        private int _maxPoleBars = PatternFlagParameterLimits.FlagMaxPoleBarsDefault;
        private decimal _minPoleMovePercent = PatternFlagParameterLimits.FlagPoleMovePercentDefault;
        private decimal _minPoleAtrMultiplier = PatternFlagParameterLimits.FlagPoleAtrMultiplierDefault;
        private decimal _minPoleBodyRatio = PatternFlagParameterLimits.FlagPoleBodyRatioDefault;
        private int _minFlagBars = PatternFlagParameterLimits.FlagMinFlagBarsDefault;
        private int _maxFlagBars = PatternFlagParameterLimits.FlagMaxFlagBarsDefault;
        private decimal _minRSquared = PatternFlagParameterLimits.FlagMinRSquaredDefault;
        private decimal _maxSlopeDiffAtr = PatternFlagParameterLimits.FlagMaxSlopeDiffAtrDefault;
        private decimal _minRetraceRatio = PatternFlagParameterLimits.FlagMinRetraceDefault;
        private decimal _maxRetraceRatio = PatternFlagParameterLimits.FlagMaxRetraceDefault;
        private decimal _maxChannelWidthRatio = PatternFlagParameterLimits.FlagMaxChannelWidthDefault;
        private int _minTouchCount = PatternFlagParameterLimits.FlagMinTouchCountDefault;
        private bool _requireVolumeConfirmation = true;
        private int _signalPersistenceBars = PatternFlagParameterLimits.FormationSignalPersistenceDefault;

        [DisplayName("Direction Mode")]
        [Description("Both = a bullish and a bearish flag set the flag. Bullish Only / Bearish Only = only that direction is searched; the other one gives 0 on Main and Direction and never wins a tie.")]
        [Category("General")]
        [EnumDisplaySort(SortAlphabetically = false)]
        public PatternDirectionMode DirectionMode
        {
            get => _directionMode;
            set => SetProperty(ref _directionMode, value);
        }

        [DisplayName("Min Pole Bars")]
        [Description("Shortest pole (the sharp move before the flag). Unit: bars.")]
        [Category("Pole")]
        [CoreParameterRange(PatternFlagParameterLimits.FlagMinPoleBarsMin, PatternFlagParameterLimits.FlagMinPoleBarsMax)]
        public int MinPoleBars
        {
            get => _minPoleBars;
            set => SetProperty(ref _minPoleBars, value);
        }

        [DisplayName("Max Pole Bars")]
        [Description("Longest pole. Unit: bars.")]
        [Category("Pole")]
        [CoreParameterRange(PatternFlagParameterLimits.FlagMaxPoleBarsMin, PatternFlagParameterLimits.FlagMaxPoleBarsMax)]
        public int MaxPoleBars
        {
            get => _maxPoleBars;
            set => SetProperty(ref _maxPoleBars, value);
        }

        [DisplayName("Min Pole Move (%)")]
        [Description("Smallest price change of the pole, as percent of its start price. The move must also be at least the ATR multiple below. Unit: percent.")]
        [Category("Pole")]
        [CoreParameterRange(PatternFlagParameterLimits.FlagPoleMovePercentMin, PatternFlagParameterLimits.FlagPoleMovePercentMax)]
        public decimal MinPoleMovePercent
        {
            get => _minPoleMovePercent;
            set => SetProperty(ref _minPoleMovePercent, value);
        }

        [DisplayName("Min Pole Move (ATR)")]
        [Description("Smallest height of the pole in average true ranges (14 bars before the pole). Unit: multiples of ATR.")]
        [Category("Pole")]
        [CoreParameterRange(PatternFlagParameterLimits.FlagPoleAtrMultiplierMin, PatternFlagParameterLimits.FlagPoleAtrMultiplierMax)]
        public decimal MinPoleAtrMultiplier
        {
            get => _minPoleAtrMultiplier;
            set => SetProperty(ref _minPoleAtrMultiplier, value);
        }

        [DisplayName("Min Pole Body Ratio")]
        [Description("Smallest share of the pole's bars that close in the direction of the pole (up for a bullish flag). Unit: ratio.")]
        [Category("Pole")]
        [CoreParameterRange(PatternFlagParameterLimits.FlagPoleBodyRatioMin, PatternFlagParameterLimits.FlagPoleBodyRatioMax)]
        public decimal MinPoleBodyRatio
        {
            get => _minPoleBodyRatio;
            set => SetProperty(ref _minPoleBodyRatio, value);
        }

        [DisplayName("Min Flag Bars")]
        [Description("Shortest flag (the narrow channel after the pole). Unit: bars.")]
        [Category("Flag Channel")]
        [CoreParameterRange(PatternFlagParameterLimits.FlagMinFlagBarsMin, PatternFlagParameterLimits.FlagMinFlagBarsMax)]
        public int MinFlagBars
        {
            get => _minFlagBars;
            set => SetProperty(ref _minFlagBars, value);
        }

        [DisplayName("Max Flag Bars")]
        [Description("Longest flag. The rails are fitted over the flag, and the fit is limited to 30 bars. Unit: bars.")]
        [Category("Flag Channel")]
        [CoreParameterRange(PatternFlagParameterLimits.FlagMaxFlagBarsMin, PatternFlagParameterLimits.FlagMaxFlagBarsMax)]
        public int MaxFlagBars
        {
            get => _maxFlagBars;
            set => SetProperty(ref _maxFlagBars, value);
        }

        [DisplayName("Min R-Squared")]
        [Description("Smallest mean fit quality of the two rails (Theil-Sen lines through the highs and the lows, residuals floored by half an ATR). Unit: 0 to 1.")]
        [Category("Flag Channel")]
        [CoreParameterRange(PatternFlagParameterLimits.FlagMinRSquaredMin, PatternFlagParameterLimits.FlagMinRSquaredMax)]
        public decimal MinRSquared
        {
            get => _minRSquared;
            set => SetProperty(ref _minRSquared, value);
        }

        [DisplayName("Max Slope Difference (ATR)")]
        [Description("Largest difference of the two rail slopes per bar, in ATRs (parallel channel). Unit: multiples of ATR per bar.")]
        [Category("Flag Channel")]
        [CoreParameterRange(PatternFlagParameterLimits.FlagMaxSlopeDiffAtrMin, PatternFlagParameterLimits.FlagMaxSlopeDiffAtrMax)]
        public decimal MaxSlopeDiffAtr
        {
            get => _maxSlopeDiffAtr;
            set => SetProperty(ref _maxSlopeDiffAtr, value);
        }

        [DisplayName("Min Retrace Ratio")]
        [Description("Smallest pull-back of the flag against the pole, as a fraction of the pole height. Unit: ratio.")]
        [Category("Flag Channel")]
        [CoreParameterRange(PatternFlagParameterLimits.FlagMinRetraceMin, PatternFlagParameterLimits.FlagMinRetraceMax)]
        public decimal MinRetraceRatio
        {
            get => _minRetraceRatio;
            set => SetProperty(ref _minRetraceRatio, value);
        }

        [DisplayName("Max Retrace Ratio")]
        [Description("Largest pull-back of the flag against the pole, as a fraction of the pole height. Unit: ratio.")]
        [Category("Flag Channel")]
        [CoreParameterRange(PatternFlagParameterLimits.FlagMaxRetraceMin, PatternFlagParameterLimits.FlagMaxRetraceMax)]
        public decimal MaxRetraceRatio
        {
            get => _maxRetraceRatio;
            set => SetProperty(ref _maxRetraceRatio, value);
        }

        [DisplayName("Max Channel Width Ratio")]
        [Description("Largest mean width of the channel as a fraction of the pole height. Unit: ratio.")]
        [Category("Flag Channel")]
        [CoreParameterRange(PatternFlagParameterLimits.FlagMaxChannelWidthMin, PatternFlagParameterLimits.FlagMaxChannelWidthMax)]
        public decimal MaxChannelWidthRatio
        {
            get => _maxChannelWidthRatio;
            set => SetProperty(ref _maxChannelWidthRatio, value);
        }

        [DisplayName("Min Touch Count")]
        [Description("Least number of independent touches (at least two bars apart) of each rail. Unit: touches.")]
        [Category("Flag Channel")]
        [CoreParameterRange(PatternFlagParameterLimits.FlagMinTouchCountMin, PatternFlagParameterLimits.FlagMinTouchCountMax)]
        public int MinTouchCount
        {
            get => _minTouchCount;
            set => SetProperty(ref _minTouchCount, value);
        }

        [DisplayName("Require Volume Confirmation")]
        [Description("When on, the flag's mean volume must be below 85 percent of the pole's and the break bar's volume at least 130 percent of the flag's mean. Instruments without volume pass.")]
        [Category("Trigger")]
        public bool RequireVolumeConfirmation
        {
            get => _requireVolumeConfirmation;
            set => SetProperty(ref _requireVolumeConfirmation, value);
        }

        [DisplayName("Signal Persistence (bars)")]
        [Description("The flag is 1 on the break bar and on this many bars after it (0 = the break bar only). Unit: bars.")]
        [Category("Trigger")]
        [CoreParameterRange(PatternFlagParameterLimits.FormationSignalPersistenceMin, PatternFlagParameterLimits.FormationSignalPersistenceMax)]
        public int SignalPersistenceBars
        {
            get => _signalPersistenceBars;
            set => SetProperty(ref _signalPersistenceBars, value);
        }

        public override string GetDisplayName(string type) => $"{type} ({MinPoleBars}-{MaxPoleBars}/{MinFlagBars}-{MaxFlagBars})";

        /// <summary>Bars requested as warm-up (a request, never a minimum; decision U2-16 A).</summary>
        public override int GetRequiredWarmupBars() => PatternFlagParameterLimits.WarmupBars;

        public override void Validate()
        {
            if (!System.Enum.IsDefined(DirectionMode)) throw new System.ArgumentOutOfRangeException(nameof(DirectionMode));
            ThrowIfOutOfRange(MinPoleBars, PatternFlagParameterLimits.FlagMinPoleBarsMin, PatternFlagParameterLimits.FlagMinPoleBarsMax, nameof(MinPoleBars));
            ThrowIfOutOfRange(MaxPoleBars, PatternFlagParameterLimits.FlagMaxPoleBarsMin, PatternFlagParameterLimits.FlagMaxPoleBarsMax, nameof(MaxPoleBars));
            ThrowIfOutOfRange(MinPoleMovePercent, PatternFlagParameterLimits.FlagPoleMovePercentMin, PatternFlagParameterLimits.FlagPoleMovePercentMax, nameof(MinPoleMovePercent));
            ThrowIfOutOfRange(MinPoleAtrMultiplier, PatternFlagParameterLimits.FlagPoleAtrMultiplierMin, PatternFlagParameterLimits.FlagPoleAtrMultiplierMax, nameof(MinPoleAtrMultiplier));
            ThrowIfOutOfRange(MinPoleBodyRatio, PatternFlagParameterLimits.FlagPoleBodyRatioMin, PatternFlagParameterLimits.FlagPoleBodyRatioMax, nameof(MinPoleBodyRatio));
            ThrowIfOutOfRange(MinFlagBars, PatternFlagParameterLimits.FlagMinFlagBarsMin, PatternFlagParameterLimits.FlagMinFlagBarsMax, nameof(MinFlagBars));
            ThrowIfOutOfRange(MaxFlagBars, PatternFlagParameterLimits.FlagMaxFlagBarsMin, PatternFlagParameterLimits.FlagMaxFlagBarsMax, nameof(MaxFlagBars));
            ThrowIfOutOfRange(MinRSquared, PatternFlagParameterLimits.FlagMinRSquaredMin, PatternFlagParameterLimits.FlagMinRSquaredMax, nameof(MinRSquared));
            ThrowIfOutOfRange(MaxSlopeDiffAtr, PatternFlagParameterLimits.FlagMaxSlopeDiffAtrMin, PatternFlagParameterLimits.FlagMaxSlopeDiffAtrMax, nameof(MaxSlopeDiffAtr));
            ThrowIfOutOfRange(MinRetraceRatio, PatternFlagParameterLimits.FlagMinRetraceMin, PatternFlagParameterLimits.FlagMinRetraceMax, nameof(MinRetraceRatio));
            ThrowIfOutOfRange(MaxRetraceRatio, PatternFlagParameterLimits.FlagMaxRetraceMin, PatternFlagParameterLimits.FlagMaxRetraceMax, nameof(MaxRetraceRatio));
            ThrowIfOutOfRange(MaxChannelWidthRatio, PatternFlagParameterLimits.FlagMaxChannelWidthMin, PatternFlagParameterLimits.FlagMaxChannelWidthMax, nameof(MaxChannelWidthRatio));
            ThrowIfOutOfRange(MinTouchCount, PatternFlagParameterLimits.FlagMinTouchCountMin, PatternFlagParameterLimits.FlagMinTouchCountMax, nameof(MinTouchCount));
            ThrowIfOutOfRange(SignalPersistenceBars, PatternFlagParameterLimits.FormationSignalPersistenceMin, PatternFlagParameterLimits.FormationSignalPersistenceMax, nameof(SignalPersistenceBars));

            if (MinPoleBars > MaxPoleBars) throw new System.ArgumentOutOfRangeException(nameof(MinPoleBars), "The shortest pole must not exceed the longest.");
            if (MinFlagBars > MaxFlagBars) throw new System.ArgumentOutOfRangeException(nameof(MinFlagBars), "The shortest flag must not exceed the longest.");
            if (MinRetraceRatio > MaxRetraceRatio) throw new System.ArgumentOutOfRangeException(nameof(MinRetraceRatio), "The smallest retrace must not exceed the largest.");
        }
    }
}
