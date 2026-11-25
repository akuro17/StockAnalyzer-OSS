using System;
using System.ComponentModel;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;

namespace StockAnalyzer.Core.Models.Parameters
{
    /// <summary>
    /// Base parameter class of the 43 Candlestick Pattern flags (plan ChartPatterns56 v2, integration spec 5.6, decision D-V2-3 A).
    /// NEVER rename or move: the concrete class name is persisted as the workspace JSON type discriminator.
    /// Per decision U2-02 A and plan §13.6 item 1, context is always separated before the pattern (<c>e = t - k</c>); no <c>ExcludePatternBarsFromContext</c> exists.
    /// </summary>
    public class CoreCandlePatternParameter : CoreIndicatorParameterBase
    {
        private int _atrPeriod = PatternFlagParameterLimits.CandleAtrPeriodDefault;
        private bool _requirePriorTrend = true;
        private int _priorTrendLookbackBars = PatternFlagParameterLimits.CandlePriorTrendLookbackDefault;
        private decimal _priorTrendAtrMultiplier = PatternFlagParameterLimits.CandlePriorTrendAtrDefault;
        private bool _requirePriceZone = true;
        private int _priceZoneLookbackBars = PatternFlagParameterLimits.CandlePriceZoneLookbackDefault;
        private decimal _priceZoneThreshold = PatternFlagParameterLimits.CandlePriceZoneThresholdDefault;
        private decimal _largeBodyAvgRatio = PatternFlagParameterLimits.CandleLargeBodyAvgRatioDefault;
        private decimal _largeBodyAtrRatio = PatternFlagParameterLimits.CandleLargeBodyAtrRatioDefault;
        private decimal _largeRangeRatio = PatternFlagParameterLimits.CandleLargeRangeRatioDefault;
        private decimal _smallBodyAvgRatio = PatternFlagParameterLimits.CandleSmallBodyAvgRatioDefault;
        private decimal _smallRangeRatio = PatternFlagParameterLimits.CandleSmallRangeRatioDefault;
        private decimal _dojiBodyRangeRatio = PatternFlagParameterLimits.CandleDojiBodyRangeRatioDefault;
        private decimal _wickToleranceRangeRatio = PatternFlagParameterLimits.CandleWickToleranceRangeRatioDefault;
        private bool _requireVolumeConfirmation;
        private decimal _volumeMultiplier = PatternFlagParameterLimits.CandleVolumeMultiplierDefault;

        /// <summary>Creates a parameter instance populated with the specification defaults for <paramref name="type"/>.</summary>
        public static CoreCandlePatternParameter CreateFor(IndicatorType type) => type switch
        {
            IndicatorType.CandleBullishMarubozu or IndicatorType.CandleBearishMarubozu => new CoreCandlePatternParameter
            {
                RequirePriorTrend = false,
                RequirePriceZone = false,
                RequireVolumeConfirmation = true,
                VolumeMultiplier = PatternFlagParameterLimits.CandleVolumeMultiplierDefault,
            },
            IndicatorType.CandleHammer => new CoreUmbrellaPatternParameter
            {
                VolumeMultiplier = PatternSpecConstants.CandleHammerPiercingAvgVolumeMultiplier,
            },
            IndicatorType.CandleInvertedHammer or IndicatorType.CandleHangingMan or IndicatorType.CandleShootingStar => new CoreUmbrellaPatternParameter(),
            IndicatorType.CandleBullishEngulfing or IndicatorType.CandleBearishEngulfing => new CoreCandlePatternParameter
            {
                VolumeMultiplier = PatternSpecConstants.CandleBullishEngulfingAvgVolumeMultiplier,
            },
            IndicatorType.CandlePiercingLine or IndicatorType.CandleDarkCloudCover => new CoreCandlePatternParameter
            {
                VolumeMultiplier = PatternSpecConstants.CandleHammerPiercingAvgVolumeMultiplier,
            },
            IndicatorType.CandleBullishKicking or IndicatorType.CandleBearishKicking => new CoreGapCandlePatternParameter
            {
                RequirePriorTrend = false,
                RequirePriceZone = false,
                RequireWickToWickGap = true,
                VolumeMultiplier = PatternSpecConstants.CandleKickingAvgVolumeMultiplier,
            },
            IndicatorType.CandleBullishAbandonedBaby or IndicatorType.CandleBearishAbandonedBaby => new CoreGapCandlePatternParameter
            {
                RequireWickToWickGap = true,
                VolumeMultiplier = PatternSpecConstants.CandleAbandonedBabyAvgVolumeMultiplier,
            },
            IndicatorType.CandleBullishSideBySideWhiteLines or IndicatorType.CandleBearishSideBySideWhiteLines
                or IndicatorType.CandleBullishTasukiGap or IndicatorType.CandleBearishTasukiGap
                or IndicatorType.CandleBullishGapThreeMethods or IndicatorType.CandleBearishGapThreeMethods => new CoreGapCandlePatternParameter
            {
                PriorTrendAtrMultiplier = PatternFlagParameterLimits.CandleContinuationPriorTrendAtrDefault,
                PriceZoneThreshold = PatternSpecConstants.CandleContinuationBearZoneMax,
            },
            IndicatorType.CandleBullishBreakaway or IndicatorType.CandleBearishBreakaway => new CoreGapCandlePatternParameter(),
            IndicatorType.CandleThreeWhiteSoldiers => new CoreMultiBarCandlePatternParameter
            {
                PriceZoneLookbackBars = PatternSpecConstants.CandleLongZoneBars,
                PriorTrendAtrMultiplier = PatternFlagParameterLimits.CandlePriorTrendAtrDefault,
            },
            IndicatorType.CandleThreeBlackCrows
                or IndicatorType.CandleAdvanceBlock or IndicatorType.CandleDeliberation
                or IndicatorType.CandleStickSandwich or IndicatorType.CandleIdenticalThreeCrows
                or IndicatorType.CandleBullishThreeLineStrike or IndicatorType.CandleBearishThreeLineStrike
                or IndicatorType.CandleConcealingBabySwallow or IndicatorType.CandleLadderBottom => new CoreMultiBarCandlePatternParameter
            {
                PriorTrendAtrMultiplier = type is IndicatorType.CandleBullishThreeLineStrike or IndicatorType.CandleBearishThreeLineStrike
                    ? PatternFlagParameterLimits.CandleContinuationPriorTrendAtrDefault
                    : PatternFlagParameterLimits.CandlePriorTrendAtrDefault,
                PriceZoneThreshold = type is IndicatorType.CandleBullishThreeLineStrike or IndicatorType.CandleBearishThreeLineStrike
                    ? PatternSpecConstants.CandleContinuationBearZoneMax
                    : PatternFlagParameterLimits.CandlePriceZoneThresholdDefault,
            },
            IndicatorType.CandleRisingThreeMethods or IndicatorType.CandleFallingThreeMethods or IndicatorType.CandleMatHold => new CoreCandlePatternParameter
            {
                PriorTrendAtrMultiplier = PatternFlagParameterLimits.CandleContinuationPriorTrendAtrDefault,
                PriceZoneThreshold = PatternSpecConstants.CandleContinuationBearZoneMax,
            },
            IndicatorType.CandleCrossDoji => new CoreCandlePatternParameter
            {
                PriceZoneThreshold = PatternSpecConstants.CandleDojiLowZoneMax,
            },
            _ => new CoreCandlePatternParameter(),
        };

        [DisplayName("ATR Period")]
        [Description("Lookback period for context ATR and average body measured immediately before the pattern (e = t - k). Unit: bars.")]
        [Category("Context")]
        [CoreParameterRange(PatternFlagParameterLimits.CandleAtrPeriodMin, PatternFlagParameterLimits.CandleAtrPeriodMax)]
        public int AtrPeriod
        {
            get => _atrPeriod;
            set => SetProperty(ref _atrPeriod, value);
        }

        [DisplayName("Require Prior Trend")]
        [Description("When on, requires the specification's prior short-term trend immediately before the pattern.")]
        [Category("Trend & Zone")]
        public bool RequirePriorTrend
        {
            get => _requirePriorTrend;
            set => SetProperty(ref _requirePriorTrend, value);
        }

        [DisplayName("Prior Trend Lookback (bars)")]
        [Description("Window length over which the prior close-to-close move is measured (EMA10 stays fixed per U2-01). Unit: bars.")]
        [Category("Trend & Zone")]
        [CoreParameterRange(PatternFlagParameterLimits.CandlePriorTrendLookbackMin, PatternFlagParameterLimits.CandlePriorTrendLookbackMax)]
        public int PriorTrendLookbackBars
        {
            get => _priorTrendLookbackBars;
            set => SetProperty(ref _priorTrendLookbackBars, value);
        }

        [DisplayName("Prior Trend (ATR)")]
        [Description("Minimum prior move magnitude expressed in multiples of context ATR. Unit: multiples of ATR.")]
        [Category("Trend & Zone")]
        [CoreParameterRange(PatternFlagParameterLimits.CandlePriorTrendAtrMin, PatternFlagParameterLimits.CandlePriorTrendAtrMax)]
        public decimal PriorTrendAtrMultiplier
        {
            get => _priorTrendAtrMultiplier;
            set => SetProperty(ref _priorTrendAtrMultiplier, value);
        }

        [DisplayName("Require Price Zone")]
        [Description("When on, requires the pattern to form in the specification's bottom, top, continuation, or extreme price zone.")]
        [Category("Trend & Zone")]
        public bool RequirePriceZone
        {
            get => _requirePriceZone;
            set => SetProperty(ref _requirePriceZone, value);
        }

        [DisplayName("Price Zone Lookback (bars)")]
        [Description("Window length for the price-zone percentile range. Unit: bars.")]
        [Category("Trend & Zone")]
        [CoreParameterRange(PatternFlagParameterLimits.CandlePriceZoneLookbackMin, PatternFlagParameterLimits.CandlePriceZoneLookbackMax)]
        public int PriceZoneLookbackBars
        {
            get => _priceZoneLookbackBars;
            set => SetProperty(ref _priceZoneLookbackBars, value);
        }

        [DisplayName("Price Zone Threshold")]
        [Description("Bottom/bearish zone maximum percentile (top/bullish zone uses 1 - threshold). Unit: ratio 0..1.")]
        [Category("Trend & Zone")]
        [CoreParameterRange(PatternFlagParameterLimits.CandlePriceZoneThresholdMin, PatternFlagParameterLimits.CandlePriceZoneThresholdMax)]
        public decimal PriceZoneThreshold
        {
            get => _priceZoneThreshold;
            set => SetProperty(ref _priceZoneThreshold, value);
        }

        [DisplayName("Large Body / Avg Body")]
        [Description("Minimum ratio of body to context average body for IsLarge and IsMarubozu. Unit: ratio.")]
        [Category("Candles")]
        [CoreParameterRange(PatternFlagParameterLimits.CandleLargeBodyAvgRatioMin, PatternFlagParameterLimits.CandleLargeBodyAvgRatioMax)]
        public decimal LargeBodyAvgRatio
        {
            get => _largeBodyAvgRatio;
            set => SetProperty(ref _largeBodyAvgRatio, value);
        }

        [DisplayName("Large Body / ATR")]
        [Description("Minimum ratio of body to context ATR for IsLarge. Unit: ratio.")]
        [Category("Candles")]
        [CoreParameterRange(PatternFlagParameterLimits.CandleLargeBodyAtrRatioMin, PatternFlagParameterLimits.CandleLargeBodyAtrRatioMax)]
        public decimal LargeBodyAtrRatio
        {
            get => _largeBodyAtrRatio;
            set => SetProperty(ref _largeBodyAtrRatio, value);
        }

        [DisplayName("Large Body / Range")]
        [Description("Minimum body-to-range ratio required by IsLarge. Unit: ratio 0..1.")]
        [Category("Candles")]
        [CoreParameterRange(PatternFlagParameterLimits.CandleLargeRangeRatioMin, PatternFlagParameterLimits.CandleLargeRangeRatioMax)]
        public decimal LargeRangeRatio
        {
            get => _largeRangeRatio;
            set => SetProperty(ref _largeRangeRatio, value);
        }

        [DisplayName("Small Body / Avg Body")]
        [Description("Maximum ratio of body to context average body for IsSmall (and umbrella small-body checks). Unit: ratio.")]
        [Category("Candles")]
        [CoreParameterRange(PatternFlagParameterLimits.CandleSmallBodyAvgRatioMin, PatternFlagParameterLimits.CandleSmallBodyAvgRatioMax)]
        public decimal SmallBodyAvgRatio
        {
            get => _smallBodyAvgRatio;
            set => SetProperty(ref _smallBodyAvgRatio, value);
        }

        [DisplayName("Small Body / Range")]
        [Description("Maximum body-to-range ratio required by IsSmall. Unit: ratio 0..1.")]
        [Category("Candles")]
        [CoreParameterRange(PatternFlagParameterLimits.CandleSmallRangeRatioMin, PatternFlagParameterLimits.CandleSmallRangeRatioMax)]
        public decimal SmallRangeRatio
        {
            get => _smallRangeRatio;
            set => SetProperty(ref _smallRangeRatio, value);
        }

        [DisplayName("Doji Body / Range")]
        [Description("Maximum body-to-range ratio for Doji (and minimum non-doji threshold for umbrellas). Unit: ratio.")]
        [Category("Candles")]
        [CoreParameterRange(PatternFlagParameterLimits.CandleDojiBodyRangeRatioMin, PatternFlagParameterLimits.CandleDojiBodyRangeRatioMax)]
        public decimal DojiBodyRangeRatio
        {
            get => _dojiBodyRangeRatio;
            set => SetProperty(ref _dojiBodyRangeRatio, value);
        }

        [DisplayName("Wick Tolerance / Range")]
        [Description("Maximum allowed upper/lower shadow ratio for Marubozu and Dragonfly/Gravestone Doji. Unit: ratio.")]
        [Category("Candles")]
        [CoreParameterRange(PatternFlagParameterLimits.CandleWickToleranceRangeRatioMin, PatternFlagParameterLimits.CandleWickToleranceRangeRatioMax)]
        public decimal WickToleranceRangeRatio
        {
            get => _wickToleranceRangeRatio;
            set => SetProperty(ref _wickToleranceRangeRatio, value);
        }

        [DisplayName("Require Volume Confirmation")]
        [Description("When on, requires confirming bar volume to meet the Volume Multiplier against the 20-bar context average (bypassed if average volume is 0).")]
        [Category("Trigger")]
        public bool RequireVolumeConfirmation
        {
            get => _requireVolumeConfirmation;
            set => SetProperty(ref _requireVolumeConfirmation, value);
        }

        [DisplayName("Volume Multiplier")]
        [Description("Required confirming volume ratio relative to the 20-bar pre-pattern average volume. Unit: multiplier.")]
        [Category("Trigger")]
        [CoreParameterRange(PatternFlagParameterLimits.CandleVolumeMultiplierMin, PatternFlagParameterLimits.CandleVolumeMultiplierMax)]
        public decimal VolumeMultiplier
        {
            get => _volumeMultiplier;
            set => SetProperty(ref _volumeMultiplier, value);
        }

        /// <summary>Plan R-9: minimum bars needed for pattern length <paramref name="patternBars"/> under current parameter settings.</summary>
        public int ComputeMinBars(int patternBars)
            => patternBars + Math.Max(AtrPeriod + 1, Math.Max(PriorTrendLookbackBars + 1, Math.Max(PriceZoneLookbackBars, PatternSpecConstants.CandleMinBaseBars)));

        public override string GetDisplayName(string type) => type;

        /// <summary>Bars requested as warm-up (decision U2-16 A, plan R-28).</summary>
        public override int GetRequiredWarmupBars() => Math.Max(ComputeMinBars(5), PatternFlagParameterLimits.WarmupBars);

        public override void Validate()
        {
            ThrowIfOutOfRange(AtrPeriod, PatternFlagParameterLimits.CandleAtrPeriodMin, PatternFlagParameterLimits.CandleAtrPeriodMax, nameof(AtrPeriod));
            ThrowIfOutOfRange(PriorTrendLookbackBars, PatternFlagParameterLimits.CandlePriorTrendLookbackMin, PatternFlagParameterLimits.CandlePriorTrendLookbackMax, nameof(PriorTrendLookbackBars));
            ThrowIfOutOfRange(PriorTrendAtrMultiplier, PatternFlagParameterLimits.CandlePriorTrendAtrMin, PatternFlagParameterLimits.CandlePriorTrendAtrMax, nameof(PriorTrendAtrMultiplier));
            ThrowIfOutOfRange(PriceZoneLookbackBars, PatternFlagParameterLimits.CandlePriceZoneLookbackMin, PatternFlagParameterLimits.CandlePriceZoneLookbackMax, nameof(PriceZoneLookbackBars));
            ThrowIfOutOfRange(PriceZoneThreshold, PatternFlagParameterLimits.CandlePriceZoneThresholdMin, PatternFlagParameterLimits.CandlePriceZoneThresholdMax, nameof(PriceZoneThreshold));
            ThrowIfOutOfRange(LargeBodyAvgRatio, PatternFlagParameterLimits.CandleLargeBodyAvgRatioMin, PatternFlagParameterLimits.CandleLargeBodyAvgRatioMax, nameof(LargeBodyAvgRatio));
            ThrowIfOutOfRange(LargeBodyAtrRatio, PatternFlagParameterLimits.CandleLargeBodyAtrRatioMin, PatternFlagParameterLimits.CandleLargeBodyAtrRatioMax, nameof(LargeBodyAtrRatio));
            ThrowIfOutOfRange(LargeRangeRatio, PatternFlagParameterLimits.CandleLargeRangeRatioMin, PatternFlagParameterLimits.CandleLargeRangeRatioMax, nameof(LargeRangeRatio));
            ThrowIfOutOfRange(SmallBodyAvgRatio, PatternFlagParameterLimits.CandleSmallBodyAvgRatioMin, PatternFlagParameterLimits.CandleSmallBodyAvgRatioMax, nameof(SmallBodyAvgRatio));
            ThrowIfOutOfRange(SmallRangeRatio, PatternFlagParameterLimits.CandleSmallRangeRatioMin, PatternFlagParameterLimits.CandleSmallRangeRatioMax, nameof(SmallRangeRatio));
            ThrowIfOutOfRange(DojiBodyRangeRatio, PatternFlagParameterLimits.CandleDojiBodyRangeRatioMin, PatternFlagParameterLimits.CandleDojiBodyRangeRatioMax, nameof(DojiBodyRangeRatio));
            ThrowIfOutOfRange(WickToleranceRangeRatio, PatternFlagParameterLimits.CandleWickToleranceRangeRatioMin, PatternFlagParameterLimits.CandleWickToleranceRangeRatioMax, nameof(WickToleranceRangeRatio));
            ThrowIfOutOfRange(VolumeMultiplier, PatternFlagParameterLimits.CandleVolumeMultiplierMin, PatternFlagParameterLimits.CandleVolumeMultiplierMax, nameof(VolumeMultiplier));
        }
    }
}
