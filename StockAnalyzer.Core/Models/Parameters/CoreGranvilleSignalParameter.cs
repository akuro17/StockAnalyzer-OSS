using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters
{
    /// <summary>
    /// Parameters of the Granville's Law Buy Signal and Sell Signal flags (plan ChartPatterns56 v2, run V2-7, integration spec 5.4; engine <c>GranvilleSignalEngine</c>).
    /// NEVER rename or move: the class name is the JSON type discriminator of the workspace. Dedicated class per D-3 / P-V2-06 (avoids exposing dead <c>ShowSubWindowBar</c> from <c>CoreGranvilleLawParameter</c>).
    /// </summary>
    public class CoreGranvilleSignalParameter : CoreIndicatorParameterBase
    {
        private int _maPeriod = PatternFlagParameterLimits.GranvilleSignalMaPeriodDefault;
        private int _slopePeriod = PatternFlagParameterLimits.GranvilleSignalSlopePeriodDefault;
        private decimal _deviationThreshold = PatternFlagParameterLimits.GranvilleSignalDeviationThresholdDefault;
        private decimal _bounceTolerance = PatternFlagParameterLimits.GranvilleSignalBounceToleranceDefault;
        private decimal _flatThreshold = PatternFlagParameterLimits.GranvilleSignalFlatThresholdDefault;
        private bool _enableRule1 = true;
        private bool _enableRule2 = true;
        private bool _enableRule3 = true;
        private bool _enableRule4 = true;
        private bool _requirePrevBarOnMaSideForRule3 = true;
        private bool _rule4FirstEntryOrReversalOnly;
        private bool _requireVolumeOnRule1;

        [DisplayName("MA Period")]
        [Description("Simple Moving Average period used as the trend baseline. Unit: bars.")]
        [Category("General")]
        [CoreParameterRange(PatternFlagParameterLimits.GranvilleSignalMaPeriodMin, PatternFlagParameterLimits.GranvilleSignalMaPeriodMax)]
        public int MaPeriod
        {
            get => _maPeriod;
            set => SetProperty(ref _maPeriod, value);
        }

        [DisplayName("Slope Period")]
        [Description("Lookback bars over which the SMA slope is measured. Unit: bars.")]
        [Category("General")]
        [CoreParameterRange(PatternFlagParameterLimits.GranvilleSignalSlopePeriodMin, PatternFlagParameterLimits.GranvilleSignalSlopePeriodMax)]
        public int SlopePeriod
        {
            get => _slopePeriod;
            set => SetProperty(ref _slopePeriod, value);
        }

        [DisplayName("Deviation Threshold (%)")]
        [Description("Minimum absolute percentage deviation from the SMA required to trigger Rule 4. Unit: percent.")]
        [Category("Signal")]
        [CoreParameterRange(PatternFlagParameterLimits.GranvilleSignalDeviationThresholdMin, PatternFlagParameterLimits.GranvilleSignalDeviationThresholdMax)]
        public decimal DeviationThreshold
        {
            get => _deviationThreshold;
            set => SetProperty(ref _deviationThreshold, value);
        }

        [DisplayName("Bounce Tolerance (%)")]
        [Description("Maximum percentage distance from the SMA allowed for a Rule 3 bounce/rejection touch. Unit: percent.")]
        [Category("Signal")]
        [CoreParameterRange(PatternFlagParameterLimits.GranvilleSignalBounceToleranceMin, PatternFlagParameterLimits.GranvilleSignalBounceToleranceMax)]
        public decimal BounceTolerance
        {
            get => _bounceTolerance;
            set => SetProperty(ref _bounceTolerance, value);
        }

        [DisplayName("Flat Threshold (%)")]
        [Description("Maximum SMA slope percentage considered flat; slopes strictly above this are rising or falling. Unit: percent.")]
        [Category("Signal")]
        [CoreParameterRange(PatternFlagParameterLimits.GranvilleSignalFlatThresholdMin, PatternFlagParameterLimits.GranvilleSignalFlatThresholdMax)]
        public decimal FlatThreshold
        {
            get => _flatThreshold;
            set => SetProperty(ref _flatThreshold, value);
        }

        [DisplayName("Enable Rule 1")]
        [Description("Enables Rule 1 (New Buy / New Sell crossover across a flat or favourable SMA).")]
        [Category("Rules")]
        public bool EnableRule1
        {
            get => _enableRule1;
            set => SetProperty(ref _enableRule1, value);
        }

        [DisplayName("Enable Rule 2")]
        [Description("Enables Rule 2 (Pullback Buy / Return Sell temporary counter-cross against an active SMA trend).")]
        [Category("Rules")]
        public bool EnableRule2
        {
            get => _enableRule2;
            set => SetProperty(ref _enableRule2, value);
        }

        [DisplayName("Enable Rule 3")]
        [Description("Enables Rule 3 (Bounce Buy / Rejection Sell near the SMA without crossing).")]
        [Category("Rules")]
        public bool EnableRule3
        {
            get => _enableRule3;
            set => SetProperty(ref _enableRule3, value);
        }

        [DisplayName("Enable Rule 4")]
        [Description("Enables Rule 4 (Reversal Buy / Reversal Sell extreme deviation against the SMA slope).")]
        [Category("Rules")]
        public bool EnableRule4
        {
            get => _enableRule4;
            set => SetProperty(ref _enableRule4, value);
        }

        [DisplayName("Require Prev Bar on MA Side (Rule 3)")]
        [Description("When on (v2 spec), Rule 3 requires the previous bar's close to already be on the trend side of the previous SMA, keeping all four rules pairwise mutually exclusive.")]
        [Category("Rules")]
        public bool RequirePrevBarOnMaSideForRule3
        {
            get => _requirePrevBarOnMaSideForRule3;
            set => SetProperty(ref _requirePrevBarOnMaSideForRule3, value);
        }

        [DisplayName("Rule 4 First Entry or Reversal Only")]
        [Description("When on, Rule 4 triggers only on the first bar entering the deviation zone or on a reversal candle (otherwise it triggers on every bar inside the zone).")]
        [Category("Rules")]
        public bool Rule4FirstEntryOrReversalOnly
        {
            get => _rule4FirstEntryOrReversalOnly;
            set => SetProperty(ref _rule4FirstEntryOrReversalOnly, value);
        }

        [DisplayName("Require Volume on Rule 1")]
        [Description("When on, Rule 1 requires break-bar volume >= 1.20x the 20-bar average volume (instruments without volume pass).")]
        [Category("Trigger")]
        public bool RequireVolumeOnRule1
        {
            get => _requireVolumeOnRule1;
            set => SetProperty(ref _requireVolumeOnRule1, value);
        }

        public override string GetDisplayName(string type) => $"{type} ({MaPeriod})";

        /// <summary>Bars requested as warm-up: max(N_min, PatternFlagWarmupBars) per plan R-28.</summary>
        public override int GetRequiredWarmupBars() => Math.Max(MaPeriod + SlopePeriod + 1, PatternFlagParameterLimits.WarmupBars);

        public override void Validate()
        {
            ThrowIfOutOfRange(MaPeriod, PatternFlagParameterLimits.GranvilleSignalMaPeriodMin, PatternFlagParameterLimits.GranvilleSignalMaPeriodMax, nameof(MaPeriod));
            ThrowIfOutOfRange(SlopePeriod, PatternFlagParameterLimits.GranvilleSignalSlopePeriodMin, PatternFlagParameterLimits.GranvilleSignalSlopePeriodMax, nameof(SlopePeriod));
            ThrowIfOutOfRange(DeviationThreshold, PatternFlagParameterLimits.GranvilleSignalDeviationThresholdMin, PatternFlagParameterLimits.GranvilleSignalDeviationThresholdMax, nameof(DeviationThreshold));
            ThrowIfOutOfRange(BounceTolerance, PatternFlagParameterLimits.GranvilleSignalBounceToleranceMin, PatternFlagParameterLimits.GranvilleSignalBounceToleranceMax, nameof(BounceTolerance));
            ThrowIfOutOfRange(FlatThreshold, PatternFlagParameterLimits.GranvilleSignalFlatThresholdMin, PatternFlagParameterLimits.GranvilleSignalFlatThresholdMax, nameof(FlatThreshold));
        }
    }
}
