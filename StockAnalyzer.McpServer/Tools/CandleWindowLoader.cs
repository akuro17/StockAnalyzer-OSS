using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>The one definition of how a window of bars is loaded for a tool (shared by sa_get_candles and sa_calculate_indicators).</summary>
internal sealed class CandleWindowLoader
{
    private readonly IReadOnlyStockAnalyzerGateway _gateway;

    public CandleWindowLoader(IReadOnlyStockAnalyzerGateway gateway)
    {
        _gateway = gateway;
    }

    /// <summary>
    /// Loads the latest <paramref name="count"/> bars. W1 and MN1 are aggregated from daily bars, so the oldest bar can cover
    /// a partial period: one extra bar is requested and the surplus oldest bar is dropped (reported as droppedLeadingBar).
    /// </summary>
    public async Task<(List<CandleData> Bars, bool DroppedLeadingBar)> LoadAsync(string symbol, TimeFrame timeFrame, int count, CancellationToken cancellationToken)
    {
        bool aggregated = TimeFrameAggregator.IsPeriodFrame(timeFrame);
        int request = aggregated ? count + 1 : count;
        List<CandleData> bars = (await _gateway.GetCandlesAsync(symbol, timeFrame, request, cancellationToken).ConfigureAwait(false)).ToList();
        if (bars.Count > count)
        {
            bars = bars.Skip(bars.Count - count).ToList();
            return (bars, true);
        }

        return (bars, false);
    }

    /// <summary>
    /// Loads the latest <paramref name="count"/> bars dated on or before <paramref name="endDate"/>, however old that date is (the gateway reads the whole
    /// stored history for the call, which <see cref="LoadAsync"/> does not).
    /// </summary>
    public async Task<List<CandleData>> LoadUpToAsync(string symbol, TimeFrame timeFrame, DateOnly endDate, int count, CancellationToken cancellationToken)
        => (await _gateway.GetCandlesUpToAsync(symbol, timeFrame, endDate, count, cancellationToken).ConfigureAwait(false)).ToList();

    /// <summary>
    /// The date the bars are known up to: <paramref name="end"/>, but never later than the last stored daily bar; null when nothing is stored.
    /// A weekly or monthly period is complete only once this date reaches its last calendar day. Call it BEFORE the bars are read: a Sync can replace the stored
    /// file between the two reads, and a reference date read after the bars could be later than the data behind them (a week would then look complete).
    /// Read first, it is never later, so the flag can only err towards "partial".
    /// </summary>
    public async Task<DateTime?> ReferenceDateAsync(string symbol, DateOnly? end, CancellationToken cancellationToken)
    {
        IReadOnlyList<CandleData> latest = await _gateway.GetCandlesAsync(symbol, TimeFrame.D1, 1, cancellationToken).ConfigureAwait(false);
        if (latest.Count == 0)
        {
            return null;
        }

        return TimeFrameAggregator.ReferenceDate(latest[^1].Timestamp, end);
    }

    /// <summary>True when the last of <paramref name="bars"/> is a W1 or MN1 bar whose period ends after <paramref name="referenceDate"/> (false for no bar or no reference date).</summary>
    public static bool IsLastBarPartial(IReadOnlyList<CandleData> bars, TimeFrame timeFrame, DateTime? referenceDate)
        => bars.Count > 0
            && referenceDate is { } reference
            && TimeFrameAggregator.IsPartialPeriod(bars[^1].Timestamp, timeFrame, reference);
}
