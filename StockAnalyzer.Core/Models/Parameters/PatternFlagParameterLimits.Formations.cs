using StockAnalyzer.Core.Services.Analysis.PatternEngines;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// Defaults and ranges of <c>CoreHeadAndShouldersParameter</c> and <c>CoreDoubleTopBottomParameter</c> (plan ChartPatterns56 v2, run V2-3; integration spec 5.1). The attribute ranges are <c>double</c> and the
/// defaults <c>decimal</c> / <c>int</c>, as in the rest of this class. A number that is a definition of the specification and not a setting is in <see cref="PatternSpecConstants"/>.
/// </summary>
public static partial class PatternFlagParameterLimits
{
    // ---------------------------------------------------------------- shared by both formations

    /// <summary>Minimum swing amplitude in context ATRs (theta_atr_piv): default is the spec value, 0 switches the test off.</summary>
    public const decimal FormationMinSwingAtrDefault = PatternSpecConstants.ZigZagMinAtrMultiplierDefault;
    public const double FormationMinSwingAtrMin = 0.0;
    public const double FormationMinSwingAtrMax = 5.0;

    /// <summary>Bars on each side of the fractal (k_L = k_R).</summary>
    public const int FormationFractalBarsDefault = PatternSpecConstants.FractalBarsDefault;
    public const int FormationFractalBarsMin = 1;
    public const int FormationFractalBarsMax = 10;

    /// <summary>Bars before the pattern in which the prior trend is measured (L_mt).</summary>
    public const int FormationPriorTrendLookbackDefault = PatternSpecConstants.MidTrendLookbackBars;
    public const int FormationPriorTrendLookbackMin = 10;
    public const int FormationPriorTrendLookbackMax = 120;

    /// <summary>Minimum prior move, percent of the extreme price (default per type below).</summary>
    public const double FormationPriorTrendMinPercentMin = 2.0;
    public const double FormationPriorTrendMinPercentMax = 100.0;

    /// <summary>Bars of the price-zone range (N_zone).</summary>
    public const int FormationZoneLookbackDefault = PatternSpecConstants.ZoneBarsPattern;
    public const int FormationZoneLookbackMin = 20;
    public const int FormationZoneLookbackMax = 250;

    /// <summary>High-zone threshold of a peak (a bottom uses 1 minus it).</summary>
    public const decimal FormationZoneThresholdDefault = 0.85m;
    public const decimal FormationSecondPeakZoneThresholdDefault = 0.80m;
    public const double FormationZoneThresholdMin = 0.50;
    public const double FormationZoneThresholdMax = 1.00;

    /// <summary>Minimum depth of the pattern, percent of the reference price (default per type below).</summary>
    public const double FormationNecklineDepthPercentMin = 1.0;
    public const double FormationNecklineDepthPercentMax = 30.0;

    /// <summary>Most bars from the last pivot to the break bar (default per type below).</summary>
    public const int FormationBreakoutWaitMin = 1;
    public const int FormationBreakoutWaitMax = 100;

    /// <summary>Break margin, percent of the level; 0 = no margin at all (strict inequality, plan R-20).</summary>
    public const decimal FormationBreakoutMarginPercentDefault = 0.5m;
    public const double FormationBreakoutMarginPercentMin = 0.0;
    public const double FormationBreakoutMarginPercentMax = 5.0;

    /// <summary>Minimum break-bar volume as a multiple of the 20-bar average (default per type below).</summary>
    public const double FormationBreakoutVolumeMultiplierMin = 1.0;
    public const double FormationBreakoutVolumeMultiplierMax = 5.0;

    /// <summary>Bars after the break bar on which the flag stays 1 (0 = the break bar only; spec 00 section 7.2 default 5, decision D-V2-12 A).</summary>
    public const int FormationSignalPersistenceDefault = 5;
    public const int FormationSignalPersistenceMin = 0;
    public const int FormationSignalPersistenceMax = 50;

    // ---------------------------------------------------------------- Head and Shoulders

    public const decimal HeadAndShouldersPriorTrendMinPercentDefault = 15.0m;
    public const int HeadAndShouldersMinTotalBarsDefault = 20;
    public const int HeadAndShouldersMinTotalBarsMin = 10;
    public const int HeadAndShouldersMinTotalBarsMax = 100;
    public const int HeadAndShouldersMaxTotalBarsDefault = 180;
    public const int HeadAndShouldersMaxTotalBarsMin = 30;
    public const int HeadAndShouldersMaxTotalBarsMax = 500;
    public const int HeadAndShouldersMinIntervalDefault = 5;
    public const int HeadAndShouldersMinIntervalMin = 2;
    public const int HeadAndShouldersMinIntervalMax = 30;
    public const int HeadAndShouldersMaxIntervalDefault = 60;
    public const int HeadAndShouldersMaxIntervalMin = 10;
    public const int HeadAndShouldersMaxIntervalMax = 150;
    public const int HeadAndShouldersBreakoutWaitDefault = 30;
    public const decimal HeadAndShouldersShoulderTolerancePercentDefault = 5.0m;
    public const double HeadAndShouldersShoulderTolerancePercentMin = 0.5;
    public const double HeadAndShouldersShoulderTolerancePercentMax = 20.0;
    public const decimal HeadAndShouldersHeadProminenceDefault = 0.15m;
    public const double HeadAndShouldersHeadProminenceMin = 0.01;
    public const double HeadAndShouldersHeadProminenceMax = 0.60;
    public const decimal HeadAndShouldersNecklineDepthPercentDefault = 5.0m;
    public const decimal HeadAndShouldersNecklineSlopeDefault = 0.35m;
    public const double HeadAndShouldersNecklineSlopeMin = 0.05;
    public const double HeadAndShouldersNecklineSlopeMax = 1.00;
    public const decimal HeadAndShouldersBreakoutVolumeMultiplierDefault = 1.25m;

    // ---------------------------------------------------------------- Double Top / Bottom

    public const decimal DoublePriorTrendMinPercentDefault = 10.0m;
    public const int DoubleMinIntervalDefault = 10;
    public const int DoubleMinIntervalMin = 3;
    public const int DoubleMinIntervalMax = 40;
    public const int DoubleMaxIntervalDefault = 65;
    public const int DoubleMaxIntervalMin = 15;
    public const int DoubleMaxIntervalMax = 200;
    public const int DoubleBreakoutWaitDefault = 25;
    public const decimal DoublePeakTolerancePercentDefault = 3.0m;
    public const double DoublePeakTolerancePercentMin = 0.5;
    public const double DoublePeakTolerancePercentMax = 10.0;
    public const decimal DoubleNecklineDepthPercentDefault = 6.0m;
    public const decimal DoubleMaxRetraceRatioDefault = 0.65m;
    public const double DoubleMaxRetraceRatioMin = 0.20;
    public const double DoubleMaxRetraceRatioMax = 0.95;
    public const decimal DoubleBreakoutVolumeMultiplierDefault = 1.20m;
}
