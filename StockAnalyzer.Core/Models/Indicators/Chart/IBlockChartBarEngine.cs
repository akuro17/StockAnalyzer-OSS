namespace StockAnalyzer.Core.Models.Indicators.Chart;

/// <summary>
/// The state of a block chart (Renko, Point and Figure, Kagi, Three Line Break) right after one time bar has been processed. All values are prices (decimal).
/// <list type="bullet">
/// <item><see cref="IsDefined"/> false: no block / column / segment is confirmed yet; the other members are 0 and must not be shown.</item>
/// <item><see cref="Close"/>: the current confirmed level of the chart (brick close, column end, segment end, block close).</item>
/// <item><see cref="Reversal"/>: the reference price whose break reverses the chart. <see cref="Update"/>: the reference price of the next same-direction step; null for charts without it (Kagi, Three Line Break).</item>
/// <item><see cref="IsUp"/>: the direction of the current block / column / segment.</item>
/// </list>
/// </summary>
public readonly record struct BlockChartBarState(bool IsDefined, decimal Close, decimal Reversal, decimal? Update, bool IsUp)
{
    /// <summary>Value of the IsUpTrend helper series for an up block / column / segment.</summary>
    public const decimal UpTrendFlag = 1m;

    /// <summary>Value of the IsUpTrend helper series for a down block / column / segment.</summary>
    public const decimal DownTrendFlag = 0m;

    /// <summary>The state before the first block is confirmed.</summary>
    public static BlockChartBarState Undefined => default;

    /// <summary>The IsUpTrend helper value of this state.</summary>
    public decimal TrendFlag => IsUp ? UpTrendFlag : DownTrendFlag;
}

/// <summary>The direction values the incremental engines keep: none before the first block, then up or down (the sign is what the engines test and negate).</summary>
internal static class BlockChartDirection
{
    public const int None = 0;
    public const int Up = 1;
    public const int Down = -1;
}

/// <summary>
/// An incremental, forward-only block chart calculation: it sees one valid time bar at a time, in chronological order, and never anything after it, so the state it reports is the same
/// as the one the existing batch calculator of the chart type reports for the bars fed so far (proved by the prefix parity tests). The caller (<see cref="BlockChartOverlayMapper"/>)
/// feeds valid bars only.
/// </summary>
public interface IBlockChartBarEngine
{
    void Feed(CoreCandleData bar);

    /// <summary>The state after the last fed bar.</summary>
    BlockChartBarState State { get; }
}
