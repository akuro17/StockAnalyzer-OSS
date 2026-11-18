namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// How <see cref="ReturnStatistics.Calculate"/> treats a common return date whose returns do not cover the same holding period in every series
/// (a market traded on a day another market was closed, so its return spans fewer bars; see <see cref="CrossAssetStatsResult.MultiBarReturnCount"/>).
/// The names are the text a caller that exposes the mode (a tool argument, a setting) uses for it.
/// </summary>
public enum HoldingPeriodAlignmentMode
{
    /// <summary>Every common return date is used; the dates with different holding periods are only counted (<see cref="CrossAssetStatsResult.MultiBarReturnCount"/>). The standard.</summary>
    IncludeAll = 0,

    /// <summary>
    /// Only the common return dates on which every series' return covers the same holding period are used: the dates counted by
    /// <see cref="CrossAssetStatsResult.MultiBarReturnCount"/> are excluded (<see cref="CrossAssetStatsResult.ExcludedMultiBarDateCount"/>) and the statistics are computed on the rest.
    /// </summary>
    StrictEqualHoldingPeriod = 1,
}
