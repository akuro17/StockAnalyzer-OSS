using StockAnalyzer.Core.Services.Analysis.PatternEngines;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// Defaults and ranges of <c>CoreStructureShiftParameter</c> (plan ChartPatterns56 v2, run V2-5; integration spec 5.2 class 4). The attribute ranges are <c>double</c>, the defaults <c>decimal</c> / <c>int</c>.
/// </summary>
public static partial class PatternFlagParameterLimits
{
    /// <summary>Minimum swing amplitude in context ATRs (theta_atr_piv); 0 switches the test off.</summary>
    public const decimal StructureShiftMinSwingAtrDefault = PatternSpecConstants.ZigZagMinAtrMultiplierDefault;
    public const double StructureShiftMinSwingAtrMin = 0.0;
    public const double StructureShiftMinSwingAtrMax = 5.0;

    /// <summary>Most bars from the confirmation of the reference swing to the break bar (T_max); BOS only (plan R-24).</summary>
    public const int StructureShiftMaxPivotAgeDefault = 100;
    public const int StructureShiftMaxPivotAgeMin = 10;
    public const int StructureShiftMaxPivotAgeMax = 500;

    /// <summary>Close margin, percent of the level; 0 = no margin at all (strict inequality, plan R-20).</summary>
    public const decimal StructureShiftBreakoutMarginPercentDefault = 0.0m;
    public const double StructureShiftBreakoutMarginPercentMin = 0.0;
    public const double StructureShiftBreakoutMarginPercentMax = 3.0;

    /// <summary>Optional volume confirmation: default off, the multiple of the 20-bar average volume (fallback default for parameterless ctor, and spec defaults 1.15m for BOS / 1.25m for CHoCH).</summary>
    public const bool StructureShiftRequireVolumeDefault = false;
    public const decimal StructureShiftBreakoutVolumeMultiplierDefault = 1.20m;
    public const decimal StructureShiftBosBreakoutVolumeMultiplierDefault = 1.15m;
    public const decimal StructureShiftChochBreakoutVolumeMultiplierDefault = 1.25m;
    public const double StructureShiftBreakoutVolumeMultiplierMin = 1.0;
    public const double StructureShiftBreakoutVolumeMultiplierMax = 5.0;

    /// <summary>Bars after the break bar on which the flag stays 1 (0 = the break bar only; decision U2-17 A).</summary>
    public const int StructureShiftSignalPersistenceDefault = 0;
    public const int StructureShiftSignalPersistenceMin = 0;
    public const int StructureShiftSignalPersistenceMax = 50;

    // ---------------------------------------------------------------- Higher High / Higher Low (CoreStructureTrendParameter, spec §5.2 #5)

    /// <summary>Minimum swing amplitude in context ATRs; 0 switches the test off.</summary>
    public const decimal StructureTrendMinSwingAtrDefault = PatternSpecConstants.ZigZagMinAtrMultiplierDefault;
    public const double StructureTrendMinSwingAtrMin = 0.0;
    public const double StructureTrendMinSwingAtrMax = 5.0;

    /// <summary>Minimum percentage step-up for HH/HL (combined with &gt;= 0.50 * ATR_ctx via AND; 0 disables only the percentage floor).</summary>
    public const decimal StructureTrendMinStepUpPercentDefault = 0.5m;
    public const double StructureTrendMinStepUpPercentMin = 0.0;
    public const double StructureTrendMinStepUpPercentMax = 10.0;

    /// <summary>Minimum bar interval between consecutive confirmed highs (H1..H2) and lows (L1..L2).</summary>
    public const int StructureTrendMinPivotIntervalDefault = 4;
    public const int StructureTrendMinPivotIntervalMin = 2;
    public const int StructureTrendMinPivotIntervalMax = 20;

    /// <summary>Maximum bar interval between consecutive confirmed highs (H1..H2) and lows (L1..L2).</summary>
    public const int StructureTrendMaxPivotIntervalDefault = 60;
    public const int StructureTrendMaxPivotIntervalMin = 15;
    public const int StructureTrendMaxPivotIntervalMax = 200;

    /// <summary>Maximum bars elapsed since the latest of the four confirmed pivots (t - maxConf).</summary>
    public const int StructureTrendMaxLastPivotAgeDefault = 40;
    public const int StructureTrendMaxLastPivotAgeMin = 5;
    public const int StructureTrendMaxLastPivotAgeMax = 200;

    /// <summary>Requires current close strictly above latest confirmed swing low (C_t &gt; L_2).</summary>
    public const bool StructureTrendRequireCloseAboveLastLowDefault = true;
}
