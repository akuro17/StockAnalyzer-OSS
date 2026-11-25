using StockAnalyzer.Core.MathUtils;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>Defaults and ranges of <c>CoreFlagPatternParameter</c> (plan ChartPatterns56 v2, run V2-4; integration spec 5.1 class 3).</summary>
public static partial class PatternFlagParameterLimits
{
    public const int FlagMinPoleBarsDefault = 3;
    public const int FlagMinPoleBarsMin = 2;
    public const int FlagMinPoleBarsMax = 10;
    public const int FlagMaxPoleBarsDefault = 15;
    public const int FlagMaxPoleBarsMin = 5;
    public const int FlagMaxPoleBarsMax = 40;

    public const decimal FlagPoleMovePercentDefault = 8.0m;
    public const double FlagPoleMovePercentMin = 2.0;
    public const double FlagPoleMovePercentMax = 50.0;

    public const decimal FlagPoleAtrMultiplierDefault = 3.5m;
    public const double FlagPoleAtrMultiplierMin = 1.0;
    public const double FlagPoleAtrMultiplierMax = 15.0;

    public const decimal FlagPoleBodyRatioDefault = 0.60m;
    public const double FlagPoleBodyRatioMin = 0.40;
    public const double FlagPoleBodyRatioMax = 1.00;

    public const int FlagMinFlagBarsDefault = 5;
    public const int FlagMinFlagBarsMin = 3;
    public const int FlagMinFlagBarsMax = 15;
    public const int FlagMaxFlagBarsDefault = 25;
    public const int FlagMaxFlagBarsMin = 10;

    /// <summary>
    /// Largest flag length: the integration spec table says 60, but the rails are fitted by <see cref="RobustRegressionMath.TryFit"/>, which refuses windows above <see cref="RobustRegressionMath.MaxWindowLength"/>;
    /// a longer setting could never fire (a silent loss), so the range ends there (deviation F-1 of ledger V2-4).
    /// </summary>
    public const int FlagMaxFlagBarsMax = RobustRegressionMath.MaxWindowLength;

    public const decimal FlagMinRSquaredDefault = 0.70m;
    public const double FlagMinRSquaredMin = 0.30;
    public const double FlagMinRSquaredMax = 0.99;

    public const decimal FlagMaxSlopeDiffAtrDefault = 0.15m;
    public const double FlagMaxSlopeDiffAtrMin = 0.02;
    public const double FlagMaxSlopeDiffAtrMax = 1.00;

    public const decimal FlagMinRetraceDefault = 0.10m;
    public const double FlagMinRetraceMin = 0.0;
    public const double FlagMinRetraceMax = 0.3;
    public const decimal FlagMaxRetraceDefault = 0.50m;
    public const double FlagMaxRetraceMin = 0.2;
    public const double FlagMaxRetraceMax = 0.7;

    public const decimal FlagMaxChannelWidthDefault = 0.35m;
    public const double FlagMaxChannelWidthMin = 0.10;
    public const double FlagMaxChannelWidthMax = 0.80;

    public const int FlagMinTouchCountDefault = 2;
    public const int FlagMinTouchCountMin = 2;
    public const int FlagMinTouchCountMax = 6;
}
