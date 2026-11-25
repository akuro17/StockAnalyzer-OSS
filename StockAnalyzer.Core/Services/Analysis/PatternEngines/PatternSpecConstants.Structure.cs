namespace StockAnalyzer.Core.Services.Analysis.PatternEngines;

/// <summary>
/// The numbers of the Break of Structure and Change of Character specifications (<c>02_Market_Structure\01_break_of_structure_bos.md</c>, <c>02_change_of_character_choch.md</c>) that are definitions and not
/// user settings (plan ChartPatterns56 v2, run V2-5; ledger <c>docs/sa_pattern_literal_ledger_V2-5_BosChoch</c>). The numbers a user may tune are parameters of
/// <c>CoreStructureShiftParameter</c>; their defaults are in <c>PatternFlagParameterLimits</c>.
/// </summary>
public static partial class PatternSpecConstants
{
    /// <summary>Spec header of both files: the minimum history (bars) of BOS and CHoCH (ATR 14 and the smallest structure).</summary>
    public const int StructureShiftMinBars = 20;

    /// <summary>
    /// Bars of the fractal delay of a swing. The BOS / CHoCH files define a swing as a ZigZag pivot with the ATR amplitude and name no fractal, so a swing is confirmed on the ZigZag reversal bar
    /// (interpretation S-1 of the ledger).
    /// </summary>
    public const int StructureShiftFractalBars = 0;

    /// <summary>Spec 01 / 02 section 4: the ATR term of the close margin (<c>0.10 ATR_ctx(t-1, 14)</c>) of BOS and of CHoCH, used when the margin percent is above 0 (plan R-20).</summary>
    public const decimal StructureShiftBreakoutAtrMultiplier = 0.10m;

    /// <summary>Spec 02 section 4: the structure width of the protected swing must be at least this many context ATRs.</summary>
    public const decimal StructureShiftChochWidthAtrMultiplier = 2.0m;

    // ---------------------------------------------------------------- Higher High / Higher Low (02/03)

    /// <summary>Spec 02/03 header: minimum history (bars) of Higher High / Higher Low (ATR14 + at least 4 confirmed ZigZag pivots).</summary>
    public const int StructureTrendMinBars = 20;

    /// <summary>Spec 02/03 §2: ZigZag fractal delay (bars); 0 = confirmed on the ZigZag reversal bar.</summary>
    public const int StructureTrendFractalBars = 0;

    /// <summary>Spec 02/03 §3: default minimum bar span from earliest to latest of the four pivots (H1, L1, H2, L2) when MinPivotIntervalBars = 4.</summary>
    public const int StructureTrendMinSpanBars = 10;

    /// <summary>Spec 02/03 §3: default maximum bar span from earliest to latest of the four pivots (H1, L1, H2, L2) when MaxPivotIntervalBars = 60.</summary>
    public const int StructureTrendMaxSpanBars = 120;

    /// <summary>Spec 02/03 §3: multiplier converting pivot pair interval limits (Min/MaxPivotIntervalBars) to 4-pivot span bounds.</summary>
    public const int StructureTrendSpanIntervalMultiplier = 2;

    /// <summary>Spec 02/03 §3: additive bar offset for dynamic 4-pivot minimum span (<c>min(2 * MinPivotIntervalBars + 2, MaxSpan)</c>).</summary>
    public const int StructureTrendMinSpanOffsetBars = 2;

    /// <summary>Spec 02/03 §3: safety warmup bar margin added on top of dynamic max structure span and max pivot age.</summary>
    public const int StructureTrendWarmupMarginBars = 20;

    /// <summary>Spec 02/03 §4: ATR multiplier of the HH/HL step-up threshold (<c>0.50 * ATR_ctx(i - 1, 14)</c>), combined with MinStepUpPercent via AND.</summary>
    public const decimal StructureTrendStepAtrMultiplier = 0.50m;
}
