using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters
{
    /// <summary>
    /// Parameters of the Gap candlestick flags (Kicking, Abandoned Baby, Tasuki Gap, Gap Three Methods, Side-by-Side White Lines, Breakaway; integration spec 5.6, run V2-8).
    /// NEVER rename or move: the class name is the JSON type discriminator of the workspace.
    /// </summary>
    public class CoreGapCandlePatternParameter : CoreCandlePatternParameter
    {
        private decimal _gapMinBodyRatio = PatternFlagParameterLimits.CandleGapMinBodyRatioDefault;
        private bool _requireWickToWickGap;

        [DisplayName("Gap Min Body Ratio")]
        [Description("Minimum body-to-body gap measured as a multiple of context average body. Unit: ratio.")]
        [Category("Gap")]
        [CoreParameterRange(PatternFlagParameterLimits.CandleGapMinBodyRatioMin, PatternFlagParameterLimits.CandleGapMinBodyRatioMax)]
        public decimal GapMinBodyRatio
        {
            get => _gapMinBodyRatio;
            set => SetProperty(ref _gapMinBodyRatio, value);
        }

        [DisplayName("Require Wick-to-Wick Gap")]
        [Description("When on, requires non-overlapping high/low shadows (true by default for Kicking and Abandoned Baby).")]
        [Category("Gap")]
        public bool RequireWickToWickGap
        {
            get => _requireWickToWickGap;
            set => SetProperty(ref _requireWickToWickGap, value);
        }

        public override void Validate()
        {
            base.Validate();
            ThrowIfOutOfRange(GapMinBodyRatio, PatternFlagParameterLimits.CandleGapMinBodyRatioMin, PatternFlagParameterLimits.CandleGapMinBodyRatioMax, nameof(GapMinBodyRatio));
        }
    }
}
