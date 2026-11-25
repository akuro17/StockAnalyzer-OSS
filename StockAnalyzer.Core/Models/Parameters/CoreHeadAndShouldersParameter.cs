using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters
{
    /// <summary>
    /// Parameters of the Head and Shoulders flag (plan ChartPatterns56 v2, integration spec 5.1, class 1; engine <c>HeadAndShouldersEngine</c>). NEVER rename or move: the class name is the JSON type discriminator
    /// of the workspace. The settings both formations share are in <see cref="CoreFormationPatternParameterBase"/>.
    /// </summary>
    public class CoreHeadAndShouldersParameter : CoreFormationPatternParameterBase
    {
        private decimal _priceZoneThreshold = PatternFlagParameterLimits.FormationZoneThresholdDefault;
        private int _minTotalBars = PatternFlagParameterLimits.HeadAndShouldersMinTotalBarsDefault;
        private int _maxTotalBars = PatternFlagParameterLimits.HeadAndShouldersMaxTotalBarsDefault;
        private int _minPeakIntervalBars = PatternFlagParameterLimits.HeadAndShouldersMinIntervalDefault;
        private int _maxPeakIntervalBars = PatternFlagParameterLimits.HeadAndShouldersMaxIntervalDefault;
        private decimal _shoulderTolerancePercent = PatternFlagParameterLimits.HeadAndShouldersShoulderTolerancePercentDefault;
        private decimal _minHeadProminenceRatio = PatternFlagParameterLimits.HeadAndShouldersHeadProminenceDefault;
        private decimal _maxNecklineSlopeRatio = PatternFlagParameterLimits.HeadAndShouldersNecklineSlopeDefault;

        public CoreHeadAndShouldersParameter()
            : base(new FormationDefaults(
                PatternFlagParameterLimits.HeadAndShouldersPriorTrendMinPercentDefault,
                PatternFlagParameterLimits.HeadAndShouldersNecklineDepthPercentDefault,
                PatternFlagParameterLimits.HeadAndShouldersBreakoutWaitDefault,
                PatternFlagParameterLimits.HeadAndShouldersBreakoutVolumeMultiplierDefault))
        {
        }

        [DisplayName("Price Zone Threshold")]
        [Description("The head must be at or above this position of the price range of the zone lookback for a top (0.85 = the upper 15 percent); a bottom uses 1 minus it. Unit: fraction of the range.")]
        [Category("Trend & Zone")]
        [CoreParameterRange(PatternFlagParameterLimits.FormationZoneThresholdMin, PatternFlagParameterLimits.FormationZoneThresholdMax)]
        public decimal PriceZoneThreshold
        {
            get => _priceZoneThreshold;
            set => SetProperty(ref _priceZoneThreshold, value);
        }

        [DisplayName("Min Total Bars")]
        [Description("Smallest length of the pattern, from the left shoulder to the break bar. Unit: bars.")]
        [Category("Duration")]
        [CoreParameterRange(PatternFlagParameterLimits.HeadAndShouldersMinTotalBarsMin, PatternFlagParameterLimits.HeadAndShouldersMinTotalBarsMax)]
        public int MinTotalBars
        {
            get => _minTotalBars;
            set => SetProperty(ref _minTotalBars, value);
        }

        [DisplayName("Max Total Bars")]
        [Description("Largest length of the pattern, from the left shoulder to the break bar. Unit: bars.")]
        [Category("Duration")]
        [CoreParameterRange(PatternFlagParameterLimits.HeadAndShouldersMaxTotalBarsMin, PatternFlagParameterLimits.HeadAndShouldersMaxTotalBarsMax)]
        public int MaxTotalBars
        {
            get => _maxTotalBars;
            set => SetProperty(ref _maxTotalBars, value);
        }

        [DisplayName("Min Peak Interval (bars)")]
        [Description("Smallest distance from the left shoulder to the head and from the head to the right shoulder. Unit: bars.")]
        [Category("Duration")]
        [CoreParameterRange(PatternFlagParameterLimits.HeadAndShouldersMinIntervalMin, PatternFlagParameterLimits.HeadAndShouldersMinIntervalMax)]
        public int MinPeakIntervalBars
        {
            get => _minPeakIntervalBars;
            set => SetProperty(ref _minPeakIntervalBars, value);
        }

        [DisplayName("Max Peak Interval (bars)")]
        [Description("Largest distance from the left shoulder to the head and from the head to the right shoulder. Unit: bars.")]
        [Category("Duration")]
        [CoreParameterRange(PatternFlagParameterLimits.HeadAndShouldersMaxIntervalMin, PatternFlagParameterLimits.HeadAndShouldersMaxIntervalMax)]
        public int MaxPeakIntervalBars
        {
            get => _maxPeakIntervalBars;
            set => SetProperty(ref _maxPeakIntervalBars, value);
        }

        [DisplayName("Shoulder Tolerance (%)")]
        [Description("Largest difference of the two shoulders, as percent of the higher one; a difference of up to 0.5 ATR also counts. Unit: percent.")]
        [Category("Geometry")]
        [CoreParameterRange(PatternFlagParameterLimits.HeadAndShouldersShoulderTolerancePercentMin, PatternFlagParameterLimits.HeadAndShouldersShoulderTolerancePercentMax)]
        public decimal ShoulderTolerancePercent
        {
            get => _shoulderTolerancePercent;
            set => SetProperty(ref _shoulderTolerancePercent, value);
        }

        [DisplayName("Min Head Prominence")]
        [Description("How far the head must stand out beyond the higher shoulder, as a fraction of the distance from the head to the neckline. Unit: ratio.")]
        [Category("Geometry")]
        [CoreParameterRange(PatternFlagParameterLimits.HeadAndShouldersHeadProminenceMin, PatternFlagParameterLimits.HeadAndShouldersHeadProminenceMax)]
        public decimal MinHeadProminenceRatio
        {
            get => _minHeadProminenceRatio;
            set => SetProperty(ref _minHeadProminenceRatio, value);
        }

        [DisplayName("Max Neckline Slope Ratio")]
        [Description("Largest difference of the two neckline valleys as a fraction of the pattern height (head to neckline). Unit: ratio.")]
        [Category("Geometry")]
        [CoreParameterRange(PatternFlagParameterLimits.HeadAndShouldersNecklineSlopeMin, PatternFlagParameterLimits.HeadAndShouldersNecklineSlopeMax)]
        public decimal MaxNecklineSlopeRatio
        {
            get => _maxNecklineSlopeRatio;
            set => SetProperty(ref _maxNecklineSlopeRatio, value);
        }

        public override void Validate()
        {
            base.Validate();
            ThrowIfOutOfRange(PriceZoneThreshold, PatternFlagParameterLimits.FormationZoneThresholdMin, PatternFlagParameterLimits.FormationZoneThresholdMax, nameof(PriceZoneThreshold));
            ThrowIfOutOfRange(MinTotalBars, PatternFlagParameterLimits.HeadAndShouldersMinTotalBarsMin, PatternFlagParameterLimits.HeadAndShouldersMinTotalBarsMax, nameof(MinTotalBars));
            ThrowIfOutOfRange(MaxTotalBars, PatternFlagParameterLimits.HeadAndShouldersMaxTotalBarsMin, PatternFlagParameterLimits.HeadAndShouldersMaxTotalBarsMax, nameof(MaxTotalBars));
            ThrowIfOutOfRange(MinPeakIntervalBars, PatternFlagParameterLimits.HeadAndShouldersMinIntervalMin, PatternFlagParameterLimits.HeadAndShouldersMinIntervalMax, nameof(MinPeakIntervalBars));
            ThrowIfOutOfRange(MaxPeakIntervalBars, PatternFlagParameterLimits.HeadAndShouldersMaxIntervalMin, PatternFlagParameterLimits.HeadAndShouldersMaxIntervalMax, nameof(MaxPeakIntervalBars));
            ThrowIfOutOfRange(ShoulderTolerancePercent, PatternFlagParameterLimits.HeadAndShouldersShoulderTolerancePercentMin, PatternFlagParameterLimits.HeadAndShouldersShoulderTolerancePercentMax, nameof(ShoulderTolerancePercent));
            ThrowIfOutOfRange(MinHeadProminenceRatio, PatternFlagParameterLimits.HeadAndShouldersHeadProminenceMin, PatternFlagParameterLimits.HeadAndShouldersHeadProminenceMax, nameof(MinHeadProminenceRatio));
            ThrowIfOutOfRange(MaxNecklineSlopeRatio, PatternFlagParameterLimits.HeadAndShouldersNecklineSlopeMin, PatternFlagParameterLimits.HeadAndShouldersNecklineSlopeMax, nameof(MaxNecklineSlopeRatio));

            if (MinTotalBars > MaxTotalBars) throw new System.ArgumentOutOfRangeException(nameof(MinTotalBars), "The smallest total length must not exceed the largest.");
            if (MinPeakIntervalBars > MaxPeakIntervalBars) throw new System.ArgumentOutOfRangeException(nameof(MinPeakIntervalBars), "The smallest peak interval must not exceed the largest.");
        }
    }
}
