using System.Collections.Concurrent;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// An <see cref="IDataService"/> for a call that aligns the same comparison symbols against many primary symbols (sa_calculate_indicators_batch): each comparison symbol is read
/// from the wrapped service once, at the call's window length, and every later request for it is served from memory as the latest bars of that window. A request for any other
/// symbol (the primary symbol), another time frame or a longer window goes to the wrapped service unchanged, so the bars an indicator sees are the bars the single-symbol call reads.
/// </summary>
internal sealed class CachedComparisonDataService : IDataService
{
    private readonly IDataService _source;
    private readonly TimeFrame _timeFrame;
    private readonly int _cachedCount;
    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<CandleData>>> _cached;

    /// <param name="source">The service the bars are read through (it knows the window end).</param>
    /// <param name="timeFrame">The only time frame that is cached.</param>
    /// <param name="cachedCount">How many of the latest bars of each comparison symbol are read; a request for no more than this many is served from memory.</param>
    /// <param name="comparisonSymbols">The symbols to cache (canonical form).</param>
    public CachedComparisonDataService(IDataService source, TimeFrame timeFrame, int cachedCount, IEnumerable<string> comparisonSymbols)
    {
        _source = source;
        _timeFrame = timeFrame;
        _cachedCount = cachedCount;
        _cached = new ConcurrentDictionary<string, Task<IReadOnlyList<CandleData>>>(StringComparer.OrdinalIgnoreCase);
        foreach (string symbol in comparisonSymbols)
        {
            _cached.TryAdd(symbol, null!);
        }
    }

    /// <summary>Reads every comparison symbol now, so that the first primary symbol does not pay for it and a missing comparison symbol shows up before any symbol is evaluated.</summary>
    public async Task PreloadAsync()
    {
        foreach (string symbol in _cached.Keys.ToList())
        {
            await BarsAsync(symbol).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count = 100)
    {
        if (timeFrame != _timeFrame || count > _cachedCount || !_cached.ContainsKey(symbol))
        {
            return await _source.LoadCandlesAsync(symbol, timeFrame, count).ConfigureAwait(false);
        }

        IReadOnlyList<CandleData> bars = await BarsAsync(symbol).ConfigureAwait(false);
        return bars.Count <= count ? bars : bars.Skip(bars.Count - count).ToList();
    }

    private async Task<IReadOnlyList<CandleData>> BarsAsync(string symbol)
    {
        Task<IReadOnlyList<CandleData>> loading = _cached.AddOrUpdate(
            symbol,
            _ => throw new InvalidOperationException("The symbol is not cached."),
            (_, existing) => existing ?? _source.LoadCandlesAsync(symbol, _timeFrame, _cachedCount));
        try
        {
            return await loading.ConfigureAwait(false);
        }
        catch
        {
            // A failed read is not kept: the next request reads again (the aligner retries a failed read).
            _cached.TryUpdate(symbol, null!, loading);
            throw;
        }
    }
}
