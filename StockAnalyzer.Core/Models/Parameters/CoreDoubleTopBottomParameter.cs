using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters
{
    /// <summary>
    /// Parameters of the Double Top / Bottom flag (plan ChartPatterns56 v2, integration spec 5.1, class 2; engine <c>DoubleTopBottomEngine</c>). NEVER rename or move: the class name is the JSON type discriminator
    /// of the workspace. The settings both formations share are in <see cref="CoreFormationPatternParameterBase"/>.
    /// </summary>
    public class CoreDoubleTopBottomParameter : CoreFormationPatternParameterBase
    {
        private decimal _peak1ZoneThreshold = PatternFlagParameterLimits.FormationZoneThresholdDefault;
        private decimal _peak2ZoneThreshold = PatternFlagParameterLimits.FormationSecondPeakZoneThresholdDefault;
        private int _minPeakIntervalBars = PatternFlagParameterLimits.DoubleMinIntervalDefault;
        private int _maxPeakIntervalBars = PatternFlagParameterLimits.DoubleMaxIntervalDefault;
        private decimal _peakTolerancePercent = PatternFlagParameterLimits.DoublePeakTolerancePercentDefault;
        private decimal _maxNecklineRetraceRatio = PatternFlagParameterLimits.DoubleMaxRetraceRatioDefault;

        public CoreDoubleTopBottomParameter()
            : base(new FormationDefaults(
                PatternFlagParameterLimits.DoublePriorTrendMinPercentDefault,
                PatternFlagParameterLimits.DoubleNecklineDepthPercentDefault,
                PatternFlagParameterLimits.DoubleBreakoutWaitDefault,
                PatternFlagParameterLimits.DoubleBreakoutVolumeMultiplierDefault))
        {
        }

        [DisplayName("Peak 1 Zone Threshold")]
        [Description("The first peak must be at or above this position of the price range of the zone lookback for a top (0.85 = the upper 15 percent); a bottom uses 1 minus it. Unit: fraction of the range.")]
        [Category("Trend & Zone")]
        [CoreParameterRange(PatternFlagParameterLimits.FormationZoneThresholdMin, PatternFlagParameterLimits.FormationZoneThresholdMax)]
        public decimal Peak1ZoneThreshold
        {
            get => _peak1ZoneThreshold;
            set => SetProperty(ref _peak1ZoneThreshold, value);
        }

        [DisplayName("Peak 2 Zone Threshold")]
        [Description("The same position test for the second peak; a bottom uses 1 minus it. Unit: fraction of the range.")]
        [Category("Trend & Zone")]
        [CoreParameterRange(PatternFlagParameterLimits.FormationZoneThresholdMin, PatternFlagParameterLimits.FormationZoneThresholdMax)]
        public decimal Peak2ZoneThreshold
        {
            get => _peak2ZoneThreshold;
            set => SetProperty(ref _peak2ZoneThreshold, value);
        }

        [DisplayName("Min Peak Interval (bars)")]
        [Description("Smallest distance from the first peak to the second. Unit: bars.")]
        [Category("Duration")]
        [CoreParameterRange(PatternFlagParameterLimits.DoubleMinIntervalMin, PatternFlagParameterLimits.DoubleMinIntervalMax)]
        public int MinPeakIntervalBars
        {
            get => _minPeakIntervalBars;
            set => SetProperty(ref _minPeakIntervalBars, value);
        }

        [DisplayName("Max Peak Interval (bars)")]
        [Description("Largest distance from the first peak to the second. Unit: bars.")]
        [Category("Duration")]
        [CoreParameterRange(PatternFlagParameterLimits.DoubleMaxIntervalMin, PatternFlagParameterLimits.DoubleMaxIntervalMax)]
        public int MaxPeakIntervalBars
        {
            get => _maxPeakIntervalBars;
            set => SetProperty(ref _maxPeakIntervalBars, value);
        }

        [DisplayName("Peak Tolerance (%)")]
        [Description("Largest difference of the two peaks, as percent of the higher one; a difference of up to 0.5 ATR also counts. Unit: percent.")]
        [Category("Geometry")]
        [CoreParameterRange(PatternFlagParameterLimits.DoublePeakTolerancePercentMin, PatternFlagParameterLimits.DoublePeakTolerancePercentMax)]
        public decimal PeakTolerancePercent
        {
            get => _peakTolerancePercent;
            set => SetProperty(ref _peakTolerancePercent, value);
        }

        [DisplayName("Max Neckline Retrace Ratio")]
        [Description("Largest depth of the valley between the peaks as a fraction of the prior move (from its lowest low to the first peak). Only used while Require Prior Trend is on. Unit: ratio.")]
        [Category("Geometry")]
        [CoreParameterRange(PatternFlagParameterLimits.DoubleMaxRetraceRatioMin, PatternFlagParameterLimits.DoubleMaxRetraceRatioMax)]
        public decimal MaxNecklineRetraceRatio
        {
            get => _maxNecklineRetraceRatio;
            set => SetProperty(ref _maxNecklineRetraceRatio, value);
        }

        public override void Validate()
        {
            base.Validate();
            ThrowIfOutOfRange(Peak1ZoneThreshold, PatternFlagParameterLimits.FormationZoneThresholdMin, PatternFlagParameterLimits.FormationZoneThresholdMax, nameof(Peak1ZoneThreshold));
            ThrowIfOutOfRange(Peak2ZoneThreshold, PatternFlagParameterLimits.FormationZoneThresholdMin, PatternFlagParameterLimits.FormationZoneThresholdMax, nameof(Peak2ZoneThreshold));
            ThrowIfOutOfRange(MinPeakIntervalBars, PatternFlagParameterLimits.DoubleMinIntervalMin, PatternFlagParameterLimits.DoubleMinIntervalMax, nameof(MinPeakIntervalBars));
            ThrowIfOutOfRange(MaxPeakIntervalBars, PatternFlagParameterLimits.DoubleMaxIntervalMin, PatternFlagParameterLimits.DoubleMaxIntervalMax, nameof(MaxPeakIntervalBars));
            ThrowIfOutOfRange(PeakTolerancePercent, PatternFlagParameterLimits.DoublePeakTolerancePercentMin, PatternFlagParameterLimits.DoublePeakTolerancePercentMax, nameof(PeakTolerancePercent));
            ThrowIfOutOfRange(MaxNecklineRetraceRatio, PatternFlagParameterLimits.DoubleMaxRetraceRatioMin, PatternFlagParameterLimits.DoubleMaxRetraceRatioMax, nameof(MaxNecklineRetraceRatio));

            if (MinPeakIntervalBars > MaxPeakIntervalBars) throw new System.ArgumentOutOfRangeException(nameof(MinPeakIntervalBars), "The smallest peak interval must not exceed the largest.");
        }
    }
}
