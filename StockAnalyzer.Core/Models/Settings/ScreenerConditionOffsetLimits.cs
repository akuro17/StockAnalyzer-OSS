using StockAnalyzer.Core.Services.Backtest.Engine;

namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// System contract of the Screener condition Offset (bars before the evaluated bar). The bands are code, never configuration: a configuration file
/// cannot widen them. <c>Screener:MaxConditionOffset</c> (the setting) must lie in the hard band or startup stops; a value above the standard band
/// still runs and is reported once as a warning.
/// </summary>
public static class ScreenerConditionOffsetLimits
{
    /// <summary>
    /// The largest accepted <c>Screener:MaxConditionOffset</c>: 2500 bars, a value the owner decided on 2026-10-16 (it is not derived from a
    /// measurement). The history a scan loads grows with the Offset (Offset + warm-up + margin bars per symbol), so the bound keeps one scan's
    /// data volume finite. The lower bound is the causality invariant <see cref="BacktestConditionOffsetRule.MinOffset"/> (a negative Offset would
    /// read a future bar). The standard band ends at the shipped default.
    /// </summary>
    public static readonly SettingBand MaxConditionOffset = new(BacktestConditionOffsetRule.MinOffset, 2500, BacktestConditionOffsetRule.MinOffset, DefaultMaxConditionOffset);

    /// <summary>
    /// The shipped value of <c>Screener:MaxConditionOffset</c> (the former fixed limit of the Offset input boxes, 500 bars). The one definition behind the
    /// settings class default, the <c>IStockAnalyzerSettings</c> default member and the end of the standard band.
    /// </summary>
    public const int DefaultMaxConditionOffset = 500;
}
