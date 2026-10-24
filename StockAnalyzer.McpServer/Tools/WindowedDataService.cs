using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// A one-method <see cref="IDataService"/> that loads through <see cref="CandleWindowLoader"/>, so the GUI's
/// <c>ComparisonDataAligner</c> can align comparison symbols for W1 and MN1 too (those frames are aggregated from daily bars
/// by the loader, not stored). The monitoring-observation member keeps its default (no observation).
/// </summary>
internal sealed class WindowedDataService : IDataService
{
    private readonly CandleWindowLoader _loader;

    public WindowedDataService(CandleWindowLoader loader)
    {
        _loader = loader;
    }

    public async Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count = 100)
        => (await _loader.LoadAsync(symbol, timeFrame, count, CancellationToken.None).ConfigureAwait(false)).Bars;
}
