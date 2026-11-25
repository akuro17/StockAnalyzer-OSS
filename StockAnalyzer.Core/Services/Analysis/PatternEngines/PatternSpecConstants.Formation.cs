namespace StockAnalyzer.Core.Services.Analysis.PatternEngines;

/// <summary>
/// The numbers of the Head and Shoulders and Double Top / Bottom specifications (<c>01_Pattern_Recognition\01_head_and_shoulders.md</c>, <c>02_double_top_bottom.md</c>) that are definitions and not user settings
/// (plan ChartPatterns56 v2, run V2-3; ledger <c>docs/sa_pattern_literal_ledger_V2-3_HeadShouldersDouble</c>). The numbers a user may tune are parameters; their defaults are in <c>PatternFlagParameterLimits</c>.
/// </summary>
public static partial class PatternSpecConstants
{
    /// <summary>Spec header of both files: the minimum history (bars) of the formation indicators (mid-term trend, SMA50 and the 60-bar price-zone range).</summary>
    public const int FormationMinBars = 60;

    /// <summary>Spec 5 of both files: the break bar is at least this many bars after the last pivot (<c>1 &lt;= i - i_last</c>).</summary>
    public const int FormationBreakoutMinBarsAfterLastPivot = 1;

    /// <summary>Spec 4 of both files: the alternative depth rule needs the depth to be at least this many context ATRs ...</summary>
    public const decimal FormationDepthAtrMultiplier = 2.5m;

    /// <summary>Spec 4 of both files: ... and at least this fraction of the reference price (0.02 = 2 percent).</summary>
    public const decimal FormationDepthAtrFloorFraction = 0.02m;

    /// <summary>Spec 01 section 2: the prior move before the left shoulder may alternatively be this many context ATRs (<c>4.0</c>).</summary>
    public const decimal HeadAndShouldersPriorTrendAtrMultiplier = 4.0m;

    /// <summary>Spec 01 section 3: lower bound of <c>(i_RS - i_H) / (i_H - i_LS)</c>.</summary>
    public const decimal HeadAndShouldersTimeSymmetryMin = 0.40m;

    /// <summary>Spec 01 section 3: upper bound of <c>(i_RS - i_H) / (i_H - i_LS)</c>.</summary>
    public const decimal HeadAndShouldersTimeSymmetryMax = 2.50m;

    /// <summary>Spec 01 section 4: the shoulder difference may alternatively be this many context ATRs measured at the bar before the right shoulder.</summary>
    public const decimal HeadAndShouldersShoulderAtrMultiplier = 0.50m;

    /// <summary>Spec 01 section 5: ATR multiple of the break margin <c>m = max(pct * level, 0.25 ATR)</c> (decision U2-08 A).</summary>
    public const decimal HeadAndShouldersBreakoutAtrMultiplier = 0.25m;

    /// <summary>Spec 02 section 3: smallest total length (bars) of a Double Top / Bottom, from the first peak to the break bar.</summary>
    public const int DoubleTotalBarsMin = 12;

    /// <summary>Spec 02 section 3: largest total length (bars).</summary>
    public const int DoubleTotalBarsMax = 90;

    /// <summary>Spec 02 section 3: smallest distance (bars) from a peak to the valley between the peaks.</summary>
    public const int DoubleLegBarsMin = 3;

    /// <summary>Spec 02 section 3: largest distance (bars) from a peak to the valley between the peaks.</summary>
    public const int DoubleLegBarsMax = 40;

    /// <summary>Spec 02 section 4: the peak difference may alternatively be this many context ATRs measured at the bar before the second peak.</summary>
    public const decimal DoublePeakAtrMultiplier = 0.50m;

    /// <summary>Spec 02 section 5: ATR multiple of the break margin (decision U2-08 A).</summary>
    public const decimal DoubleBreakoutAtrMultiplier = 0.25m;

    /// <summary>Spec 02 section 5: the pattern is invalid when a close passes the higher peak (lower bottom) by this fraction (0.01 = 1 percent).</summary>
    public const decimal DoubleInvalidationFraction = 0.01m;
}
