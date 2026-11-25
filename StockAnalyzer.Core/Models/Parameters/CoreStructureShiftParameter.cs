using System.ComponentModel;
using StockAnalyzer.Core.Models.Indicators.PatternFlags;

namespace StockAnalyzer.Core.Models.Parameters
{
    /// <summary>
    /// Parameters of the Break of Structure and Change of Character flags (plan ChartPatterns56 v2, run V2-5, integration spec 5.2 class 4; engine <c>StructureShiftEngine</c>). NEVER rename or move: the class
    /// name is the JSON type discriminator of the workspace. The two indicators share this class because they share one state machine.
    /// </summary>
    public class CoreStructureShiftParameter : CoreIndicatorParameterBase
    {
        private PatternDirectionMode _directionMode = PatternDirectionMode.Both;
        private decimal _zigZagThresholdPercent = PatternFlagParameterLimits.StructureShiftZigZagDefault;
        private decimal _minSwingAtrMultiplier = PatternFlagParameterLimits.StructureShiftMinSwingAtrDefault;
        private int _maxPivotAgeBars = PatternFlagParameterLimits.StructureShiftMaxPivotAgeDefault;
        private decimal _breakoutMarginPercent = PatternFlagParameterLimits.StructureShiftBreakoutMarginPercentDefault;
        private bool _requireVolumeConfirmation = PatternFlagParameterLimits.StructureShiftRequireVolumeDefault;
        private decimal _breakoutVolumeMultiplier = PatternFlagParameterLimits.StructureShiftBreakoutVolumeMultiplierDefault;
        private int _signalPersistenceBars = PatternFlagParameterLimits.StructureShiftSignalPersistenceDefault;

        /// <summary>Creates a <see cref="CoreStructureShiftParameter"/> initialized with the specification defaults for <paramref name="type"/> (<c>1.15m</c> for BOS, <c>1.25m</c> for CHoCH).</summary>
        public static CoreStructureShiftParameter CreateFor(IndicatorType type) => type switch
        {
            IndicatorType.MarketStructureBos => new CoreStructureShiftParameter
            {
                BreakoutVolumeMultiplier = PatternFlagParameterLimits.StructureShiftBosBreakoutVolumeMultiplierDefault,
            },
            IndicatorType.MarketStructureChoch => new CoreStructureShiftParameter
            {
                BreakoutVolumeMultiplier = PatternFlagParameterLimits.StructureShiftChochBreakoutVolumeMultiplierDefault,
            },
            _ => throw new System.ArgumentOutOfRangeException(nameof(type), type, "Not a market structure shift indicator type."),
        };

        [DisplayName("Direction Mode")]
        [Description("Both = a bullish and a bearish shift set the flag. Bullish Only / Bearish Only = only that direction sets the flag; the other one gives 0 on Main and Direction. The trend state always follows both directions, so the filter never changes which shifts exist.")]
        [Category("General")]
        [EnumDisplaySort(SortAlphabetically = false)]
        public PatternDirectionMode DirectionMode
        {
            get => _directionMode;
            set => SetProperty(ref _directionMode, value);
        }

        [DisplayName("ZigZag Threshold (%)")]
        [Description("Minimum percentage swing that makes a confirmed swing high or low. A shift is a close beyond such a swing. Unit: percent of price.")]
        [Category("Swing")]
        [CoreParameterRange(PatternFlagParameterLimits.ZigZagMin, PatternFlagParameterLimits.StructureShiftZigZagMax)]
        public decimal ZigZagThresholdPercent
        {
            get => _zigZagThresholdPercent;
            set => SetProperty(ref _zigZagThresholdPercent, value);
        }

        [DisplayName("Min Swing (ATR)")]
        [Description("A swing must also span at least this many average true ranges (14 bars) to be a swing. 0 switches the test off. Unit: multiples of ATR.")]
        [Category("Swing")]
        [CoreParameterRange(PatternFlagParameterLimits.StructureShiftMinSwingAtrMin, PatternFlagParameterLimits.StructureShiftMinSwingAtrMax)]
        public decimal MinSwingAtrMultiplier
        {
            get => _minSwingAtrMultiplier;
            set => SetProperty(ref _minSwingAtrMultiplier, value);
        }

        [DisplayName("Max Pivot Age (bars)")]
        [Description("Break of Structure only: the most bars between the confirmation of the swing that is broken and the break bar; an older swing is ignored. The protected swing of Change of Character never expires, so a trend can always flip. Unit: bars.")]
        [Category("Duration")]
        [CoreParameterRange(PatternFlagParameterLimits.StructureShiftMaxPivotAgeMin, PatternFlagParameterLimits.StructureShiftMaxPivotAgeMax)]
        public int MaxPivotAgeBars
        {
            get => _maxPivotAgeBars;
            set => SetProperty(ref _maxPivotAgeBars, value);
        }

        [DisplayName("Breakout Margin (%)")]
        [Description("The close must pass the swing by this percent of its price, or by 0.10 ATR if that is more. 0 = no margin at all: a close beyond the swing is enough (strict inequality, the ATR term is off too). Unit: percent.")]
        [Category("Trigger")]
        [CoreParameterRange(PatternFlagParameterLimits.StructureShiftBreakoutMarginPercentMin, PatternFlagParameterLimits.StructureShiftBreakoutMarginPercentMax)]
        public decimal BreakoutMarginPercent
        {
            get => _breakoutMarginPercent;
            set => SetProperty(ref _breakoutMarginPercent, value);
        }

        [DisplayName("Signal Persistence (bars)")]
        [Description("The flag is 1 on the break bar and on this many bars after it (0 = the break bar only). Unit: bars.")]
        [Category("Trigger")]
        [CoreParameterRange(PatternFlagParameterLimits.StructureShiftSignalPersistenceMin, PatternFlagParameterLimits.StructureShiftSignalPersistenceMax)]
        public int SignalPersistenceBars
        {
            get => _signalPersistenceBars;
            set => SetProperty(ref _signalPersistenceBars, value);
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
        [CoreParameterRange(PatternFlagParameterLimits.StructureShiftBreakoutVolumeMultiplierMin, PatternFlagParameterLimits.StructureShiftBreakoutVolumeMultiplierMax)]
        public decimal BreakoutVolumeMultiplier
        {
            get => _breakoutVolumeMultiplier;
            set => SetProperty(ref _breakoutVolumeMultiplier, value);
        }

        public override string GetDisplayName(string type) => $"{type} ({ZigZagThresholdPercent}%)";

        /// <summary>Bars requested as warm-up (a request, never a minimum; decision U2-16 A): the flag reads the whole loaded series.</summary>
        public override int GetRequiredWarmupBars() => PatternFlagParameterLimits.WarmupBars;

        public override void Validate()
        {
            if (!System.Enum.IsDefined(DirectionMode)) throw new System.ArgumentOutOfRangeException(nameof(DirectionMode));
            ThrowIfOutOfRange(ZigZagThresholdPercent, PatternFlagParameterLimits.ZigZagMin, PatternFlagParameterLimits.StructureShiftZigZagMax, nameof(ZigZagThresholdPercent));
            ThrowIfOutOfRange(MinSwingAtrMultiplier, PatternFlagParameterLimits.StructureShiftMinSwingAtrMin, PatternFlagParameterLimits.StructureShiftMinSwingAtrMax, nameof(MinSwingAtrMultiplier));
            ThrowIfOutOfRange(MaxPivotAgeBars, PatternFlagParameterLimits.StructureShiftMaxPivotAgeMin, PatternFlagParameterLimits.StructureShiftMaxPivotAgeMax, nameof(MaxPivotAgeBars));
            ThrowIfOutOfRange(BreakoutMarginPercent, PatternFlagParameterLimits.StructureShiftBreakoutMarginPercentMin, PatternFlagParameterLimits.StructureShiftBreakoutMarginPercentMax, nameof(BreakoutMarginPercent));
            ThrowIfOutOfRange(SignalPersistenceBars, PatternFlagParameterLimits.StructureShiftSignalPersistenceMin, PatternFlagParameterLimits.StructureShiftSignalPersistenceMax, nameof(SignalPersistenceBars));
            ThrowIfOutOfRange(BreakoutVolumeMultiplier, PatternFlagParameterLimits.StructureShiftBreakoutVolumeMultiplierMin, PatternFlagParameterLimits.StructureShiftBreakoutVolumeMultiplierMax, nameof(BreakoutVolumeMultiplier));
        }
    }
}
