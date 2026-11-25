using System;
using System.ComponentModel;
using StockAnalyzer.Core.Models.Indicators.PatternFlags;

namespace StockAnalyzer.Core.Models.Parameters
{
    /// <summary>
    /// Parameters of the Harmonic pattern flags: Gartley, Butterfly, and Bat (plan ChartPatterns56 v2, run V2-6, integration spec 5.3; engine <c>HarmonicPatternEngine</c>).
    /// NEVER rename or move: the class name is the JSON type discriminator of the workspace.
    /// </summary>
    public class CoreHarmonicPatternParameter : CoreIndicatorParameterBase
    {
        private PatternDirectionMode _directionMode = PatternDirectionMode.Both;
        private decimal _minLegSwingPercent = PatternFlagParameterLimits.HarmonicMinLegSwingPercentDefault;
        private decimal _minLegAtrMultiplier = PatternFlagParameterLimits.HarmonicMinLegAtrMultiplierDefault;
        private int _minSpanBars = PatternFlagParameterLimits.HarmonicMinSpanBarsDefault;
        private int _maxSpanBars = PatternFlagParameterLimits.HarmonicMaxSpanBarsDefault;
        private int _minLegBars = PatternFlagParameterLimits.HarmonicMinLegBarsDefault;
        private int _maxLegBars = PatternFlagParameterLimits.HarmonicMaxLegBarsDefault;
        private decimal _maxTimeSymmetryRatio = PatternFlagParameterLimits.HarmonicMaxTimeSymmetryRatioDefault;
        private decimal _abXaMin = PatternFlagParameterLimits.HarmonicGartleyAbXaMinDefault;
        private decimal _abXaMax = PatternFlagParameterLimits.HarmonicGartleyAbXaMaxDefault;
        private decimal _bcAbMin = PatternFlagParameterLimits.HarmonicBcAbMinDefault;
        private decimal _bcAbMax = PatternFlagParameterLimits.HarmonicBcAbMaxDefault;
        private decimal _cdBcMin = PatternFlagParameterLimits.HarmonicGartleyCdBcMinDefault;
        private decimal _cdBcMax = PatternFlagParameterLimits.HarmonicGartleyCdBcMaxDefault;
        private decimal _adXaMin = PatternFlagParameterLimits.HarmonicGartleyAdXaMinDefault;
        private decimal _adXaMax = PatternFlagParameterLimits.HarmonicGartleyAdXaMaxDefault;
        private decimal _minConfidenceScore = PatternFlagParameterLimits.HarmonicMinConfidenceScoreDefault;
        private bool _requireIntermediateCleanliness = true;
        private int _maxBarsSinceConfirmation = PatternFlagParameterLimits.HarmonicMaxBarsSinceConfirmationDefault;
        private bool _requireReversalCandle;

        /// <summary>Parameterless constructor required by JSON serialization; initializes with Gartley defaults. Use <see cref="CreateFor"/> for Butterfly and Bat defaults.</summary>
        public CoreHarmonicPatternParameter()
        {
        }

        /// <summary>Creates a <see cref="CoreHarmonicPatternParameter"/> initialized with the specification defaults for <paramref name="type"/>.</summary>
        public static CoreHarmonicPatternParameter CreateFor(IndicatorType type) => type switch
        {
            IndicatorType.HarmonicGartley => new CoreHarmonicPatternParameter(),
            IndicatorType.HarmonicButterfly => new CoreHarmonicPatternParameter
            {
                AbXaMin = PatternFlagParameterLimits.HarmonicButterflyAbXaMinDefault,
                AbXaMax = PatternFlagParameterLimits.HarmonicButterflyAbXaMaxDefault,
                CdBcMin = PatternFlagParameterLimits.HarmonicButterflyCdBcMinDefault,
                CdBcMax = PatternFlagParameterLimits.HarmonicButterflyCdBcMaxDefault,
                AdXaMin = PatternFlagParameterLimits.HarmonicButterflyAdXaMinDefault,
                AdXaMax = PatternFlagParameterLimits.HarmonicButterflyAdXaMaxDefault,
            },
            IndicatorType.HarmonicBat => new CoreHarmonicPatternParameter
            {
                AbXaMin = PatternFlagParameterLimits.HarmonicBatAbXaMinDefault,
                AbXaMax = PatternFlagParameterLimits.HarmonicBatAbXaMaxDefault,
                CdBcMin = PatternFlagParameterLimits.HarmonicBatCdBcMinDefault,
                CdBcMax = PatternFlagParameterLimits.HarmonicBatCdBcMaxDefault,
                AdXaMin = PatternFlagParameterLimits.HarmonicBatAdXaMinDefault,
                AdXaMax = PatternFlagParameterLimits.HarmonicBatAdXaMaxDefault,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not a harmonic indicator type."),
        };

        [DisplayName("Direction Mode")]
        [Description("Both = bullish and bearish harmonic patterns set the flag. Bullish Only / Bearish Only = only that direction sets the flag; the other side gives 0 on Main and Direction.")]
        [Category("General")]
        [EnumDisplaySort(SortAlphabetically = false)]
        public PatternDirectionMode DirectionMode
        {
            get => _directionMode;
            set => SetProperty(ref _directionMode, value);
        }

        [DisplayName("Min Leg Swing (%)")]
        [Description("Minimum percentage move of each of the four legs X-A, A-B, B-C, C-D. Unit: percent.")]
        [Category("Swing")]
        [CoreParameterRange(PatternFlagParameterLimits.HarmonicMinLegSwingPercentMin, PatternFlagParameterLimits.HarmonicMinLegSwingPercentMax)]
        public decimal MinLegSwingPercent
        {
            get => _minLegSwingPercent;
            set => SetProperty(ref _minLegSwingPercent, value);
        }

        [DisplayName("Min Leg Swing (ATR)")]
        [Description("Each leg must also span at least this many context ATRs (14 bars before the leg start). 0 disables the leg ATR check (the initial X-A 2.5 ATR check remains active). Unit: multiples of ATR.")]
        [Category("Swing")]
        [CoreParameterRange(PatternFlagParameterLimits.HarmonicMinLegAtrMultiplierMin, PatternFlagParameterLimits.HarmonicMinLegAtrMultiplierMax)]
        public decimal MinLegAtrMultiplier
        {
            get => _minLegAtrMultiplier;
            set => SetProperty(ref _minLegAtrMultiplier, value);
        }

        [DisplayName("Min Span (bars)")]
        [Description("Smallest number of bars from point X to point D. Unit: bars.")]
        [Category("Duration")]
        [CoreParameterRange(PatternFlagParameterLimits.HarmonicMinSpanBarsMin, PatternFlagParameterLimits.HarmonicMinSpanBarsMax)]
        public int MinSpanBars
        {
            get => _minSpanBars;
            set => SetProperty(ref _minSpanBars, value);
        }

        [DisplayName("Max Span (bars)")]
        [Description("Largest number of bars from point X to point D. Unit: bars.")]
        [Category("Duration")]
        [CoreParameterRange(PatternFlagParameterLimits.HarmonicMaxSpanBarsMin, PatternFlagParameterLimits.HarmonicMaxSpanBarsMax)]
        public int MaxSpanBars
        {
            get => _maxSpanBars;
            set => SetProperty(ref _maxSpanBars, value);
        }

        [DisplayName("Min Leg Bars")]
        [Description("Smallest duration of each individual leg (X-A, A-B, B-C, C-D). Unit: bars.")]
        [Category("Duration")]
        [CoreParameterRange(PatternFlagParameterLimits.HarmonicMinLegBarsMin, PatternFlagParameterLimits.HarmonicMinLegBarsMax)]
        public int MinLegBars
        {
            get => _minLegBars;
            set => SetProperty(ref _minLegBars, value);
        }

        [DisplayName("Max Leg Bars")]
        [Description("Largest duration of each individual leg (X-A, A-B, B-C, C-D). Unit: bars.")]
        [Category("Duration")]
        [CoreParameterRange(PatternFlagParameterLimits.HarmonicMaxLegBarsMin, PatternFlagParameterLimits.HarmonicMaxLegBarsMax)]
        public int MaxLegBars
        {
            get => _maxLegBars;
            set => SetProperty(ref _maxLegBars, value);
        }

        [DisplayName("Max Time Symmetry Ratio")]
        [Description("Largest allowed ratio of the longest leg to max(3, shortest leg). Unit: ratio.")]
        [Category("Duration")]
        [CoreParameterRange(PatternFlagParameterLimits.HarmonicMaxTimeSymmetryRatioMin, PatternFlagParameterLimits.HarmonicMaxTimeSymmetryRatioMax)]
        public decimal MaxTimeSymmetryRatio
        {
            get => _maxTimeSymmetryRatio;
            set => SetProperty(ref _maxTimeSymmetryRatio, value);
        }

        [DisplayName("AB / XA Min")]
        [Description("Lower bound of the allowed AB / XA Fibonacci retracement ratio.")]
        [Category("Fibonacci")]
        [CoreParameterRange(PatternFlagParameterLimits.HarmonicAbXaRangeMin, PatternFlagParameterLimits.HarmonicAbXaRangeMax)]
        public decimal AbXaMin
        {
            get => _abXaMin;
            set => SetProperty(ref _abXaMin, value);
        }

        [DisplayName("AB / XA Max")]
        [Description("Upper bound of the allowed AB / XA Fibonacci retracement ratio.")]
        [Category("Fibonacci")]
        [CoreParameterRange(PatternFlagParameterLimits.HarmonicAbXaRangeMin, PatternFlagParameterLimits.HarmonicAbXaRangeMax)]
        public decimal AbXaMax
        {
            get => _abXaMax;
            set => SetProperty(ref _abXaMax, value);
        }

        [DisplayName("BC / AB Min")]
        [Description("Lower bound of the allowed BC / AB Fibonacci retracement ratio.")]
        [Category("Fibonacci")]
        [CoreParameterRange(PatternFlagParameterLimits.HarmonicBcAbRangeMin, PatternFlagParameterLimits.HarmonicBcAbRangeMax)]
        public decimal BcAbMin
        {
            get => _bcAbMin;
            set => SetProperty(ref _bcAbMin, value);
        }

        [DisplayName("BC / AB Max")]
        [Description("Upper bound of the allowed BC / AB Fibonacci retracement ratio.")]
        [Category("Fibonacci")]
        [CoreParameterRange(PatternFlagParameterLimits.HarmonicBcAbRangeMin, PatternFlagParameterLimits.HarmonicBcAbRangeMax)]
        public decimal BcAbMax
        {
            get => _bcAbMax;
            set => SetProperty(ref _bcAbMax, value);
        }

        [DisplayName("CD / BC Min")]
        [Description("Lower bound of the allowed CD / BC Fibonacci extension ratio.")]
        [Category("Fibonacci")]
        [CoreParameterRange(PatternFlagParameterLimits.HarmonicCdBcRangeMin, PatternFlagParameterLimits.HarmonicCdBcRangeMax)]
        public decimal CdBcMin
        {
            get => _cdBcMin;
            set => SetProperty(ref _cdBcMin, value);
        }

        [DisplayName("CD / BC Max")]
        [Description("Upper bound of the allowed CD / BC Fibonacci extension ratio.")]
        [Category("Fibonacci")]
        [CoreParameterRange(PatternFlagParameterLimits.HarmonicCdBcRangeMin, PatternFlagParameterLimits.HarmonicCdBcRangeMax)]
        public decimal CdBcMax
        {
            get => _cdBcMax;
            set => SetProperty(ref _cdBcMax, value);
        }

        [DisplayName("AD / XA Min")]
        [Description("Lower bound of the allowed AD / XA Fibonacci retracement or extension ratio.")]
        [Category("Fibonacci")]
        [CoreParameterRange(PatternFlagParameterLimits.HarmonicAdXaRangeMin, PatternFlagParameterLimits.HarmonicAdXaRangeMax)]
        public decimal AdXaMin
        {
            get => _adXaMin;
            set => SetProperty(ref _adXaMin, value);
        }

        [DisplayName("AD / XA Max")]
        [Description("Upper bound of the allowed AD / XA Fibonacci retracement or extension ratio.")]
        [Category("Fibonacci")]
        [CoreParameterRange(PatternFlagParameterLimits.HarmonicAdXaRangeMin, PatternFlagParameterLimits.HarmonicAdXaRangeMax)]
        public decimal AdXaMax
        {
            get => _adXaMax;
            set => SetProperty(ref _adXaMax, value);
        }

        [DisplayName("Min Confidence Score")]
        [Description("Minimum weighted Fibonacci trapezoidal score required to accept the pattern. Any individual ratio scoring 0 also rejects it. Unit: 0 to 1.")]
        [Category("Scoring")]
        [CoreParameterRange(PatternFlagParameterLimits.HarmonicMinConfidenceScoreMin, PatternFlagParameterLimits.HarmonicMinConfidenceScoreMax)]
        public decimal MinConfidenceScore
        {
            get => _minConfidenceScore;
            set => SetProperty(ref _minConfidenceScore, value);
        }

        [DisplayName("Require Intermediate Cleanliness")]
        [Description("When on, intermediate bars inside the pattern must not pierce A or X beyond the spec bounds.")]
        [Category("Validity")]
        public bool RequireIntermediateCleanliness
        {
            get => _requireIntermediateCleanliness;
            set => SetProperty(ref _requireIntermediateCleanliness, value);
        }

        [DisplayName("Max Bars Since Confirmation")]
        [Description("Bars after D confirmation (t_conf = i_D + w_min) during which the flag stays active (or waits for a reversal candle). 0 = confirmation bar only. Unit: bars.")]
        [Category("Trigger")]
        [CoreParameterRange(PatternFlagParameterLimits.HarmonicMaxBarsSinceConfirmationMin, PatternFlagParameterLimits.HarmonicMaxBarsSinceConfirmationMax)]
        public int MaxBarsSinceConfirmation
        {
            get => _maxBarsSinceConfirmation;
            set => SetProperty(ref _maxBarsSinceConfirmation, value);
        }

        [DisplayName("Require Reversal Candle")]
        [Description("When on, firing requires a confirmation-window bar whose candle closes in the reversal direction beyond point D.")]
        [Category("Trigger")]
        public bool RequireReversalCandle
        {
            get => _requireReversalCandle;
            set => SetProperty(ref _requireReversalCandle, value);
        }

        public override string GetDisplayName(string type) => $"{type} ({MinConfidenceScore:0.##})";

        /// <summary>Bars requested as warm-up (a request, never a minimum; decision U2-16 A).</summary>
        public override int GetRequiredWarmupBars() => PatternFlagParameterLimits.WarmupBars;

        public override void Validate()
        {
            if (!Enum.IsDefined(DirectionMode)) throw new ArgumentOutOfRangeException(nameof(DirectionMode));
            ThrowIfOutOfRange(MinLegSwingPercent, PatternFlagParameterLimits.HarmonicMinLegSwingPercentMin, PatternFlagParameterLimits.HarmonicMinLegSwingPercentMax, nameof(MinLegSwingPercent));
            ThrowIfOutOfRange(MinLegAtrMultiplier, PatternFlagParameterLimits.HarmonicMinLegAtrMultiplierMin, PatternFlagParameterLimits.HarmonicMinLegAtrMultiplierMax, nameof(MinLegAtrMultiplier));
            ThrowIfOutOfRange(MinSpanBars, PatternFlagParameterLimits.HarmonicMinSpanBarsMin, PatternFlagParameterLimits.HarmonicMinSpanBarsMax, nameof(MinSpanBars));
            ThrowIfOutOfRange(MaxSpanBars, PatternFlagParameterLimits.HarmonicMaxSpanBarsMin, PatternFlagParameterLimits.HarmonicMaxSpanBarsMax, nameof(MaxSpanBars));
            ThrowIfOutOfRange(MinLegBars, PatternFlagParameterLimits.HarmonicMinLegBarsMin, PatternFlagParameterLimits.HarmonicMinLegBarsMax, nameof(MinLegBars));
            ThrowIfOutOfRange(MaxLegBars, PatternFlagParameterLimits.HarmonicMaxLegBarsMin, PatternFlagParameterLimits.HarmonicMaxLegBarsMax, nameof(MaxLegBars));
            ThrowIfOutOfRange(MaxTimeSymmetryRatio, PatternFlagParameterLimits.HarmonicMaxTimeSymmetryRatioMin, PatternFlagParameterLimits.HarmonicMaxTimeSymmetryRatioMax, nameof(MaxTimeSymmetryRatio));
            ThrowIfOutOfRange(AbXaMin, PatternFlagParameterLimits.HarmonicAbXaRangeMin, PatternFlagParameterLimits.HarmonicAbXaRangeMax, nameof(AbXaMin));
            ThrowIfOutOfRange(AbXaMax, PatternFlagParameterLimits.HarmonicAbXaRangeMin, PatternFlagParameterLimits.HarmonicAbXaRangeMax, nameof(AbXaMax));
            ThrowIfOutOfRange(BcAbMin, PatternFlagParameterLimits.HarmonicBcAbRangeMin, PatternFlagParameterLimits.HarmonicBcAbRangeMax, nameof(BcAbMin));
            ThrowIfOutOfRange(BcAbMax, PatternFlagParameterLimits.HarmonicBcAbRangeMin, PatternFlagParameterLimits.HarmonicBcAbRangeMax, nameof(BcAbMax));
            ThrowIfOutOfRange(CdBcMin, PatternFlagParameterLimits.HarmonicCdBcRangeMin, PatternFlagParameterLimits.HarmonicCdBcRangeMax, nameof(CdBcMin));
            ThrowIfOutOfRange(CdBcMax, PatternFlagParameterLimits.HarmonicCdBcRangeMin, PatternFlagParameterLimits.HarmonicCdBcRangeMax, nameof(CdBcMax));
            ThrowIfOutOfRange(AdXaMin, PatternFlagParameterLimits.HarmonicAdXaRangeMin, PatternFlagParameterLimits.HarmonicAdXaRangeMax, nameof(AdXaMin));
            ThrowIfOutOfRange(AdXaMax, PatternFlagParameterLimits.HarmonicAdXaRangeMin, PatternFlagParameterLimits.HarmonicAdXaRangeMax, nameof(AdXaMax));
            ThrowIfOutOfRange(MinConfidenceScore, PatternFlagParameterLimits.HarmonicMinConfidenceScoreMin, PatternFlagParameterLimits.HarmonicMinConfidenceScoreMax, nameof(MinConfidenceScore));
            ThrowIfOutOfRange(MaxBarsSinceConfirmation, PatternFlagParameterLimits.HarmonicMaxBarsSinceConfirmationMin, PatternFlagParameterLimits.HarmonicMaxBarsSinceConfirmationMax, nameof(MaxBarsSinceConfirmation));

            if (MinSpanBars > MaxSpanBars) throw new ArgumentOutOfRangeException(nameof(MinSpanBars), "MinSpanBars must not exceed MaxSpanBars.");
            if (MinLegBars > MaxLegBars) throw new ArgumentOutOfRangeException(nameof(MinLegBars), "MinLegBars must not exceed MaxLegBars.");
            if (AbXaMin > AbXaMax) throw new ArgumentOutOfRangeException(nameof(AbXaMin), "AbXaMin must not exceed AbXaMax.");
            if (BcAbMin > BcAbMax) throw new ArgumentOutOfRangeException(nameof(BcAbMin), "BcAbMin must not exceed BcAbMax.");
            if (CdBcMin > CdBcMax) throw new ArgumentOutOfRangeException(nameof(CdBcMin), "CdBcMin must not exceed CdBcMax.");
            if (AdXaMin > AdXaMax) throw new ArgumentOutOfRangeException(nameof(AdXaMin), "AdXaMin must not exceed AdXaMax.");
        }
    }
}
