namespace StockAnalyzer.Core.Models.Backtest.Engine;

/// <summary>
/// Mode of trailing stop offset calculation.
/// </summary>
public enum TrailingExitMode
{
    AtrMultiple = 0,
    Ratio = 1,
}

/// <summary>
/// Parameters for an ATR-based exit threshold (stop-loss or take-profit).
/// </summary>
public sealed record AtrExitParameters
{
    public int Period { get; init; }
    public decimal Multiple { get; init; }
}

/// <summary>
/// Parameters for a ratcheting trailing stop.
/// </summary>
public sealed record TrailingExitParameters
{
    public TrailingExitMode Mode { get; init; }
    public int? Period { get; init; }
    public decimal? Multiple { get; init; }
    public decimal? Ratio { get; init; }
}

/// <summary>
/// Optional dynamic exits configuration (Time stop, ATR stop/target, Trailing stop).
/// SSoT model shared between BacktestEngine, ConditionBasedBacktestStrategy, and MCP server.
/// </summary>
public sealed class DynamicExitConfiguration
{
    /// <summary>
    /// Maximum holding period in closed bars after the entry bar.
    /// When held bars reach this value at bar t Close, exit signal is emitted for fill at bar t+1 Open.
    /// </summary>
    public int? MaxHoldingBars { get; init; }

    /// <summary>
    /// ATR-based stop loss anchored to the entry signal bar's ATR.
    /// </summary>
    public AtrExitParameters? AtrStop { get; init; }

    /// <summary>
    /// ATR-based profit target anchored to the entry signal bar's ATR.
    /// </summary>
    public AtrExitParameters? AtrTarget { get; init; }

    /// <summary>
    /// Trailing stop level that ratchets only in the tightening direction starting bar t+1.
    /// </summary>
    public TrailingExitParameters? TrailingStop { get; init; }

    /// <summary>
    /// True when at least one dynamic exit threshold is configured.
    /// </summary>
    public bool IsEnabled => MaxHoldingBars.HasValue || AtrStop is not null || AtrTarget is not null || TrailingStop is not null;
}
