using StockAnalyzer.Core.Services.Analysis.PatternEngines;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// Defaults and ranges of the Candlestick Pattern parameter hierarchy (<c>CoreCandlePatternParameter</c>, <c>CoreUmbrellaPatternParameter</c>,
/// <c>CoreGapCandlePatternParameter</c>, <c>CoreMultiBarCandlePatternParameter</c>; plan ChartPatterns56 v2, integration spec 5.6, run V2-8).
/// </summary>
public static partial class PatternFlagParameterLimits
{
    public const int CandleAtrPeriodDefault = PatternSpecConstants.AtrPeriod;
    public const int CandleAtrPeriodMin = 2;
    public const int CandleAtrPeriodMax = 200;

    public const int CandlePriorTrendLookbackDefault = PatternSpecConstants.ShortTrendLookbackBars;
    public const int CandlePriorTrendLookbackMin = 3;
    public const int CandlePriorTrendLookbackMax = 50;

    public const decimal CandlePriorTrendAtrDefault = PatternSpecConstants.ShortTrendAtrMultiplier;
    public const decimal CandleContinuationPriorTrendAtrDefault = 1.0m;
    public const double CandlePriorTrendAtrMin = 0.0;
    public const double CandlePriorTrendAtrMax = 5.0;

    public const int CandlePriceZoneLookbackDefault = PatternSpecConstants.ZoneBarsReversalCandle;
    public const int CandlePriceZoneLookbackMin = 5;
    public const int CandlePriceZoneLookbackMax = 100;

    public const decimal CandlePriceZoneThresholdDefault = 0.25m;
    public const double CandlePriceZoneThresholdMin = 0.05;
    public const double CandlePriceZoneThresholdMax = 0.50;

    public const decimal CandleLargeBodyAvgRatioDefault = 1.50m;
    public const double CandleLargeBodyAvgRatioMin = 0.50;
    public const double CandleLargeBodyAvgRatioMax = 5.00;

    public const decimal CandleLargeBodyAtrRatioDefault = 0.60m;
    public const double CandleLargeBodyAtrRatioMin = 0.10;
    public const double CandleLargeBodyAtrRatioMax = 3.00;

    public const decimal CandleLargeRangeRatioDefault = 0.60m;
    public const double CandleLargeRangeRatioMin = 0.20;
    public const double CandleLargeRangeRatioMax = 1.00;

    public const decimal CandleSmallBodyAvgRatioDefault = 0.80m;
    public const double CandleSmallBodyAvgRatioMin = 0.10;
    public const double CandleSmallBodyAvgRatioMax = 2.00;

    public const decimal CandleSmallRangeRatioDefault = 0.50m;
    public const double CandleSmallRangeRatioMin = 0.10;
    public const double CandleSmallRangeRatioMax = 0.90;

    public const decimal CandleDojiBodyRangeRatioDefault = 0.05m;
    public const double CandleDojiBodyRangeRatioMin = 0.00;
    public const double CandleDojiBodyRangeRatioMax = 0.20;

    public const decimal CandleWickToleranceRangeRatioDefault = 0.05m;
    public const double CandleWickToleranceRangeRatioMin = 0.00;
    public const double CandleWickToleranceRangeRatioMax = 0.20;

    public const decimal CandleVolumeMultiplierDefault = 1.20m;
    public const double CandleVolumeMultiplierMin = 1.00;
    public const double CandleVolumeMultiplierMax = 5.00;

    // ---------------------------------------------------------------- Umbrella derived parameters
    public const decimal CandleUmbrellaLongShadowDefault = 2.0m;
    public const double CandleUmbrellaLongShadowMin = 1.0;
    public const double CandleUmbrellaLongShadowMax = 5.0;

    public const decimal CandleUmbrellaOppositeShadowDefault = 0.50m;
    public const double CandleUmbrellaOppositeShadowMin = 0.0;
    public const double CandleUmbrellaOppositeShadowMax = 1.0;

    // ---------------------------------------------------------------- Gap derived parameters
    public const decimal CandleGapMinBodyRatioDefault = 0.10m;
    public const double CandleGapMinBodyRatioMin = 0.0;
    public const double CandleGapMinBodyRatioMax = 1.0;

    // ---------------------------------------------------------------- MultiBar derived parameters
    public const decimal CandleMultiBarNoiseBodyMinRatioDefault = 0.50m;
    public const double CandleMultiBarNoiseBodyMinRatioMin = 0.10;
    public const double CandleMultiBarNoiseBodyMinRatioMax = 1.50;

    public const decimal CandleMultiBarMaxShadowRatioDefault = 0.30m;
    public const double CandleMultiBarMaxShadowRatioMin = 0.05;
    public const double CandleMultiBarMaxShadowRatioMax = 1.00;
}
