using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters
{
    /// <summary>
    /// Parameters of the Multi-Bar candlestick flags (Three Soldiers/Crows, Advance Block, Deliberation, Stick Sandwich, Ladder Bottom, Three-Line Strike, Concealing Baby Swallow; integration spec 5.6, run V2-8).
    /// NEVER rename or move: the class name is the JSON type discriminator of the workspace.
    /// </summary>
    public class CoreMultiBarCandlePatternParameter : CoreCandlePatternParameter
    {
        private decimal _noiseBodyMinRatio = PatternFlagParameterLimits.CandleMultiBarNoiseBodyMinRatioDefault;
        private decimal _maxUpperOrLowerShadowRatio = PatternFlagParameterLimits.CandleMultiBarMaxShadowRatioDefault;

        [DisplayName("Noise Body Min Ratio")]
        [Description("Minimum body-to-average-body ratio required to filter out noise bars in multi-bar patterns. Unit: ratio.")]
        [Category("Candles")]
        [CoreParameterRange(PatternFlagParameterLimits.CandleMultiBarNoiseBodyMinRatioMin, PatternFlagParameterLimits.CandleMultiBarNoiseBodyMinRatioMax)]
        public decimal NoiseBodyMinRatio
        {
            get => _noiseBodyMinRatio;
            set => SetProperty(ref _noiseBodyMinRatio, value);
        }

        [DisplayName("Max Shadow / Range")]
        [Description("Maximum allowed upper or lower shadow relative to bar range in multi-bar patterns. Unit: ratio.")]
        [Category("Candles")]
        [CoreParameterRange(PatternFlagParameterLimits.CandleMultiBarMaxShadowRatioMin, PatternFlagParameterLimits.CandleMultiBarMaxShadowRatioMax)]
        public decimal MaxUpperOrLowerShadowRatio
        {
            get => _maxUpperOrLowerShadowRatio;
            set => SetProperty(ref _maxUpperOrLowerShadowRatio, value);
        }

        public override void Validate()
        {
            base.Validate();
            ThrowIfOutOfRange(NoiseBodyMinRatio, PatternFlagParameterLimits.CandleMultiBarNoiseBodyMinRatioMin, PatternFlagParameterLimits.CandleMultiBarNoiseBodyMinRatioMax, nameof(NoiseBodyMinRatio));
            ThrowIfOutOfRange(MaxUpperOrLowerShadowRatio, PatternFlagParameterLimits.CandleMultiBarMaxShadowRatioMin, PatternFlagParameterLimits.CandleMultiBarMaxShadowRatioMax, nameof(MaxUpperOrLowerShadowRatio));
        }
    }
}
