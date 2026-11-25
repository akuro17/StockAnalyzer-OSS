namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// The defaults and ranges of the parameter classes of the chart-pattern flag indicators (plan ChartPatterns56, S-5 and section 5.7): the single place of these numbers. The attribute
/// ranges are <c>double</c> because an attribute argument cannot be a <c>decimal</c> constant; <c>Validate()</c> of each class reads the same constants.
/// </summary>
public static partial class PatternFlagParameterLimits
{
    /// <summary>Smallest ZigZag threshold (percent) of any flag indicator: below it nearly every bar is a pivot.</summary>
    public const double ZigZagMin = 0.5;

    /// <summary>Head and Shoulders / Double Top and Bottom: default ZigZag threshold (percent), the default of the detector predicates.</summary>
    public const decimal FormationZigZagDefault = 3.0m;
    public const double FormationZigZagMax = 20.0;

    /// <summary>BOS / CHoCH: default ZigZag threshold (percent), the default of <c>MarketStructureDetector.DetectCausal</c>.</summary>
    public const decimal StructureShiftZigZagDefault = 5.0m;
    public const double StructureShiftZigZagMax = 30.0;

    /// <summary>Higher High / Higher Low: default ZigZag threshold (percent), the value the Screener Chart Patterns criterion used.</summary>
    public const decimal StructureTrendZigZagDefault = 3.0m;
    public const double StructureTrendZigZagMax = 30.0;

    /// <summary>
    /// Bars every flag indicator asks for as warm-up (decision U2-16 A, plan R-28; unit: bars): the value of a whole-prefix engine depends on how many bars were loaded, and 500 is the history length the
    /// Screener already uses by default. It is a REQUEST for extra bars, never a minimum: a series with fewer bars (a new listing) is still calculated from <c>MinBars</c> on. It must not be below the
    /// first-value bar count of any flag type (a test pins this).
    /// </summary>
    public const int WarmupBars = 500;

    /// <summary>Freshness (unit: bars after the confirming bar; 0 = that bar only): default, minimum, maximum.</summary>
    public const int MaxBarsSinceConfirmationDefault = ChartConstants.DefaultPatternMaxBarsSinceConfirmation;
    public const int MaxBarsSinceConfirmationMin = 0;
    public const int MaxBarsSinceConfirmationMax = 200;
}
