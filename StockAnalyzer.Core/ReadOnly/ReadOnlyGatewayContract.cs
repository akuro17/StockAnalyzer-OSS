using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.ReadOnly;

/// <summary>Facts about <see cref="IReadOnlyStockAnalyzerGateway"/> that hosts and validators share (single definitions).</summary>
public static class ReadOnlyGatewayContract
{
    /// <summary>
    /// Time frames that can be loaded: D1 directly from the stored daily files, W1 and MN1 aggregated from D1.
    /// The data layer does not support the intraday frames.
    /// </summary>
    public static IReadOnlyList<TimeFrame> SupportedTimeFrames { get; } = DataServiceTimeFrames.Supported;

    /// <summary>
    /// The default time frame a backtest runs on when omitted.
    /// </summary>
    public const TimeFrame BacktestTimeFrame = TimeFrame.D1;

    /// <summary>
    /// The time frames a backtest can run on: D1 directly or W1/MN1 aggregated from daily bars.
    /// </summary>
    public static IReadOnlyList<TimeFrame> SupportedBacktestTimeFrames { get; } = DataServiceTimeFrames.Supported;
}
