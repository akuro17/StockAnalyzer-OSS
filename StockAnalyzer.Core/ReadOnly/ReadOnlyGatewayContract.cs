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
    public static IReadOnlyList<TimeFrame> SupportedTimeFrames { get; } = new[] { TimeFrame.D1, TimeFrame.W1, TimeFrame.MN1 };

    /// <summary>
    /// The time frame a backtest can run on. The preparation service reads the stored bars of the frame directly; only the daily
    /// folder is read by this host (the weekly and monthly series are aggregated by <see cref="Services.TimeFrameManager"/>, not stored).
    /// </summary>
    public const TimeFrame BacktestTimeFrame = TimeFrame.D1;
}
