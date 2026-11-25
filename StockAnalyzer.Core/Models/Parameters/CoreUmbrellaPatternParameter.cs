using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters
{
    /// <summary>
    /// Parameters of the four Umbrella candlestick flags (Hammer, Inverted Hammer, Hanging Man, Shooting Star; integration spec 5.6, run V2-8).
    /// NEVER rename or move: the class name is the JSON type discriminator of the workspace.
    /// </summary>
    public class CoreUmbrellaPatternParameter : CoreCandlePatternParameter
    {
        private decimal _longShadowBodyMultiplier = PatternFlagParameterLimits.CandleUmbrellaLongShadowDefault;
        private decimal _oppositeShadowBodyMaxRatio = PatternFlagParameterLimits.CandleUmbrellaOppositeShadowDefault;
        private bool _strictColorMatch;

        [DisplayName("Long Shadow / Body")]
        [Description("Minimum length of the primary shadow relative to the candle body. Unit: multiplier.")]
        [Category("Candles")]
        [CoreParameterRange(PatternFlagParameterLimits.CandleUmbrellaLongShadowMin, PatternFlagParameterLimits.CandleUmbrellaLongShadowMax)]
        public decimal LongShadowBodyMultiplier
        {
            get => _longShadowBodyMultiplier;
            set => SetProperty(ref _longShadowBodyMultiplier, value);
        }

        [DisplayName("Opposite Shadow / Body")]
        [Description("Maximum length of the small opposite shadow relative to the candle body. Unit: ratio.")]
        [Category("Candles")]
        [CoreParameterRange(PatternFlagParameterLimits.CandleUmbrellaOppositeShadowMin, PatternFlagParameterLimits.CandleUmbrellaOppositeShadowMax)]
        public decimal OppositeShadowBodyMaxRatio
        {
            get => _oppositeShadowBodyMaxRatio;
            set => SetProperty(ref _oppositeShadowBodyMaxRatio, value);
        }

        [DisplayName("Strict Color Match")]
        [Description("Off (default) = body color does not matter. On = requires bullish body for bottom umbrellas or bearish body for top umbrellas.")]
        [Category("Candles")]
        public bool StrictColorMatch
        {
            get => _strictColorMatch;
            set => SetProperty(ref _strictColorMatch, value);
        }

        public override void Validate()
        {
            base.Validate();
            ThrowIfOutOfRange(LongShadowBodyMultiplier, PatternFlagParameterLimits.CandleUmbrellaLongShadowMin, PatternFlagParameterLimits.CandleUmbrellaLongShadowMax, nameof(LongShadowBodyMultiplier));
            ThrowIfOutOfRange(OppositeShadowBodyMaxRatio, PatternFlagParameterLimits.CandleUmbrellaOppositeShadowMin, PatternFlagParameterLimits.CandleUmbrellaOppositeShadowMax, nameof(OppositeShadowBodyMaxRatio));
        }
    }
}
