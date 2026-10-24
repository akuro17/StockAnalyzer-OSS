using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.ReadOnly;

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
        bool aggregated = timeFrame is TimeFrame.W1 or TimeFrame.MN1;
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
}
