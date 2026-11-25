using StockAnalyzer.Core.Services.Analysis.PatternEngines;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// Defaults and ranges of <c>CoreElliottImpulseParameter</c>, <c>CoreElliottCorrectiveParameter</c>, and <c>CoreGranvilleSignalParameter</c>
/// (plan ChartPatterns56 v2, run V2-7; integration spec 5.4, 5.5).
/// </summary>
public static partial class PatternFlagParameterLimits
{
    // ---------------------------------------------------------------- Elliott Wave (integration spec 5.5)

    public const decimal ElliottZigZagDefault = 5.0m;
    public const double ElliottZigZagMax = 25.0;

    public const decimal ElliottMinSwingAtrDefault = PatternSpecConstants.ZigZagMinAtrMultiplierDefault;
    public const double ElliottMinSwingAtrMin = 0.0;
    public const double ElliottMinSwingAtrMax = 5.0;

    public const int ElliottMinSpanBarsMin = 5;
    public const int ElliottMinSpanBarsMax = 60;
    public const int ElliottMaxSpanBarsMin = 30;
    public const int ElliottMaxSpanBarsMax = 500;

    public const int ElliottImpulseMinSpanDefault = 15;
    public const int ElliottImpulseMaxSpanDefault = 200;
    public const int ElliottCorrectiveMinSpanDefault = 9;
    public const int ElliottCorrectiveMaxSpanDefault = 120;

    public const decimal ElliottMaxTimeSymmetryRatioDefault = 6.0m;
    public const double ElliottMaxTimeSymmetryRatioMin = 2.0;
    public const double ElliottMaxTimeSymmetryRatioMax = 20.0;

    public const decimal ElliottOverlapToleranceDefault = 0.05m;
    public const double ElliottOverlapToleranceMin = 0.00;
    public const double ElliottOverlapToleranceMax = 0.25;

    public const decimal ElliottFibonacciToleranceDefault = 0.20m;
    public const double ElliottFibonacciToleranceMin = 0.05;
    public const double ElliottFibonacciToleranceMax = 0.40;

    public const decimal ElliottMinConfidenceScoreDefault = 0.35m;
    public const double ElliottMinConfidenceScoreMin = 0.10;
    public const double ElliottMinConfidenceScoreMax = 1.00;

    public const int ElliottMaxBarsSinceConfirmationDefault = 5;
    public const int ElliottMaxBarsSinceConfirmationMin = 0;
    public const int ElliottMaxBarsSinceConfirmationMax = 50;

    // ---------------------------------------------------------------- Granville's Law (integration spec 5.4)

    public const int GranvilleSignalMaPeriodDefault = 200;
    public const int GranvilleSignalMaPeriodMin = 5;
    public const int GranvilleSignalMaPeriodMax = 500;

    public const int GranvilleSignalSlopePeriodDefault = 5;
    public const int GranvilleSignalSlopePeriodMin = 2;
    public const int GranvilleSignalSlopePeriodMax = 50;

    public const decimal GranvilleSignalDeviationThresholdDefault = 10.0m;
    public const double GranvilleSignalDeviationThresholdMin = 1.0;
    public const double GranvilleSignalDeviationThresholdMax = 50.0;

    public const decimal GranvilleSignalBounceToleranceDefault = 0.5m;
    public const double GranvilleSignalBounceToleranceMin = 0.1;
    public const double GranvilleSignalBounceToleranceMax = 10.0;

    public const decimal GranvilleSignalFlatThresholdDefault = 0.05m;
    public const double GranvilleSignalFlatThresholdMin = 0.01;
    public const double GranvilleSignalFlatThresholdMax = 1.0;
}
