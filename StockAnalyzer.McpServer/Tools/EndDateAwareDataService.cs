using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// A one-method <see cref="IDataService"/> for the GUI's <c>ComparisonDataAligner</c> that honours an optional end date: without one it loads the latest bars
/// (like <see cref="WindowedDataService"/>), with one it loads the latest bars dated on or before that date for every symbol, so the primary and the comparison
/// symbols are cut at the same day.
/// </summary>
internal sealed class EndDateAwareDataService : IDataService
{
    private readonly CandleWindowLoader _loader;
    private readonly DateOnly? _endDate;

    public EndDateAwareDataService(CandleWindowLoader loader, DateOnly? endDate)
    {
        _loader = loader;
        _endDate = endDate;
    }

    public async Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count = 100)
        => _endDate is { } endDate
            ? await _loader.LoadUpToAsync(symbol, timeFrame, endDate, count, CancellationToken.None).ConfigureAwait(false)
            : (await _loader.LoadAsync(symbol, timeFrame, count, CancellationToken.None).ConfigureAwait(false)).Bars;
}
