using StockAnalyzer.Core.Services.Analysis.PatternEngines;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// Defaults and ranges of <c>CoreHarmonicPatternParameter</c> (plan ChartPatterns56 v2, run V2-6; integration spec 5.3). The attribute ranges are <c>double</c> and the
/// defaults <c>decimal</c> / <c>int</c>. Spec definitions that are not user settings live in <see cref="PatternSpecConstants"/>.
/// </summary>
public static partial class PatternFlagParameterLimits
{
    public const decimal HarmonicMinLegSwingPercentDefault = 2.0m;
    public const double HarmonicMinLegSwingPercentMin = 0.5;
    public const double HarmonicMinLegSwingPercentMax = 20.0;

    public const decimal HarmonicMinLegAtrMultiplierDefault = 0.50m;
    public const double HarmonicMinLegAtrMultiplierMin = 0.0;
    public const double HarmonicMinLegAtrMultiplierMax = 3.0;

    public const int HarmonicMinSpanBarsDefault = 12;
    public const int HarmonicMinSpanBarsMin = 6;
    public const int HarmonicMinSpanBarsMax = 50;

    public const int HarmonicMaxSpanBarsDefault = 120;
    public const int HarmonicMaxSpanBarsMin = 30;
    public const int HarmonicMaxSpanBarsMax = 300;

    public const int HarmonicMinLegBarsDefault = 2;
    public const int HarmonicMinLegBarsMin = 1;
    public const int HarmonicMinLegBarsMax = 15;

    public const int HarmonicMaxLegBarsDefault = 45;
    public const int HarmonicMaxLegBarsMin = 10;
    public const int HarmonicMaxLegBarsMax = 120;

    public const decimal HarmonicMaxTimeSymmetryRatioDefault = 6.0m;
    public const double HarmonicMaxTimeSymmetryRatioMin = 2.0;
    public const double HarmonicMaxTimeSymmetryRatioMax = 20.0;

    public const double HarmonicAbXaRangeMin = 0.10;
    public const double HarmonicAbXaRangeMax = 1.50;
    public const double HarmonicBcAbRangeMin = 0.10;
    public const double HarmonicBcAbRangeMax = 1.50;
    public const double HarmonicCdBcRangeMin = 0.50;
    public const double HarmonicCdBcRangeMax = 4.00;
    public const double HarmonicAdXaRangeMin = 0.30;
    public const double HarmonicAdXaRangeMax = 3.00;

    // Shared BC/AB allowed range across all three patterns.
    public const decimal HarmonicBcAbMinDefault = 0.33m;
    public const decimal HarmonicBcAbMaxDefault = 0.93m;

    // Gartley defaults
    public const decimal HarmonicGartleyAbXaMinDefault = 0.55m;
    public const decimal HarmonicGartleyAbXaMaxDefault = 0.68m;
    public const decimal HarmonicGartleyCdBcMinDefault = 1.15m;
    public const decimal HarmonicGartleyCdBcMaxDefault = 1.75m;
    public const decimal HarmonicGartleyAdXaMinDefault = 0.74m;
    public const decimal HarmonicGartleyAdXaMaxDefault = 0.84m;

    // Butterfly defaults
    public const decimal HarmonicButterflyAbXaMinDefault = 0.72m;
    public const decimal HarmonicButterflyAbXaMaxDefault = 0.85m;
    public const decimal HarmonicButterflyCdBcMinDefault = 1.50m;
    public const decimal HarmonicButterflyCdBcMaxDefault = 2.75m;
    public const decimal HarmonicButterflyAdXaMinDefault = 1.20m;
    public const decimal HarmonicButterflyAdXaMaxDefault = 1.68m;

    // Bat defaults
    public const decimal HarmonicBatAbXaMinDefault = 0.33m;
    public const decimal HarmonicBatAbXaMaxDefault = 0.55m;
    public const decimal HarmonicBatCdBcMinDefault = 1.50m;
    public const decimal HarmonicBatCdBcMaxDefault = 2.75m;
    public const decimal HarmonicBatAdXaMinDefault = 0.84m;
    public const decimal HarmonicBatAdXaMaxDefault = 0.95m;

    public const decimal HarmonicMinConfidenceScoreDefault = 0.50m;
    public const double HarmonicMinConfidenceScoreMin = 0.10;
    public const double HarmonicMinConfidenceScoreMax = 1.00;

    public const int HarmonicMaxBarsSinceConfirmationDefault = 5;
    public const int HarmonicMaxBarsSinceConfirmationMin = 0;
    public const int HarmonicMaxBarsSinceConfirmationMax = 50;
}
