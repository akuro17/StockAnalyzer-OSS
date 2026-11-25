namespace StockAnalyzer.Core.Services.Analysis.PatternEngines;

/// <summary>
/// The numbers of the Flag specification (<c>01_Pattern_Recognition\03_bullish_bearish_flag.md</c>) that are definitions and not user settings (plan ChartPatterns56 v2, run V2-4; ledger
/// <c>docs/sa_pattern_literal_ledger_V2-4_Flag</c>). The numbers a user may tune are parameters of <c>CoreFlagPatternParameter</c>; their defaults are in <c>PatternFlagParameterLimits</c>.
/// </summary>
public static partial class PatternSpecConstants
{
    /// <summary>Spec header: the minimum history (bars) of the flag (ATR before the pole, the pole and the flag).</summary>
    public const int FlagMinBars = 35;

    /// <summary>Spec 2: bars of the price-zone range of the pole end.</summary>
    public const int FlagZoneBars = 20;

    /// <summary>Spec 2: a bullish pole must end at or above this position of the zone range (a bearish one at or below 1 minus it).</summary>
    public const decimal FlagZoneThreshold = 0.90m;

    /// <summary>Spec 3: smallest total length (bars) from the pole start to the break bar.</summary>
    public const int FlagTotalBarsMin = 8;

    /// <summary>Spec 3: largest total length (bars) from the pole start to the break bar.</summary>
    public const int FlagTotalBarsMax = 40;

    /// <summary>Spec 3: the flag is at most this many times as long as the pole.</summary>
    public const decimal FlagToPoleRatioMax = 3.0m;

    /// <summary>Spec 4: the residual floor of the normalised R-squared is <c>n (0.5 ATR)^2</c>.</summary>
    public const decimal FlagR2FloorAtrMultiplier = 0.50m;

    /// <summary>Spec 4: a touch is a bar within this fraction of the rail ...</summary>
    public const decimal FlagTouchBandFraction = 0.01m;

    /// <summary>Spec 4: ... or within this many context ATRs (the larger band counts).</summary>
    public const decimal FlagTouchBandAtr = 0.35m;

    /// <summary>Spec 4: a new touch counts only this many bars after the previous counted one.</summary>
    public const int FlagTouchMinGapBars = 2;

    /// <summary>Spec 4: a bullish flag's upper rail may rise at most this many ATRs per bar (a bearish flag's lower rail may fall at most this much).</summary>
    public const decimal FlagRailSlopeLimitAtr = 0.05m;

    /// <summary>Spec 5: the break must pass the rail by this fraction of the rail price ...</summary>
    public const decimal FlagBreakoutFraction = 0.003m;

    /// <summary>Spec 5: ... or by this many ATRs measured at the bar before the break (the larger margin counts).</summary>
    public const decimal FlagBreakoutAtrMultiplier = 0.20m;

    /// <summary>Spec 5: the mean volume of the flag must be below this fraction of the mean volume of the pole.</summary>
    public const decimal FlagVolumeContractionRatio = 0.85m;

    /// <summary>Spec 5: the break bar needs at least this multiple of the mean volume of the flag.</summary>
    public const decimal FlagBreakoutVolumeRatio = 1.30m;
}
