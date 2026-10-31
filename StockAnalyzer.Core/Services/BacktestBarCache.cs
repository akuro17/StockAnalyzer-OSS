using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Loads each (symbol, time frame) bar series at most once for the backtests of one batch, so every run of the batch reads the same
/// stored bars even when the files change meanwhile. The cached list is only read (preparation copies it), never modified.
/// A load that failed stays failed for its key, exactly as every individual run of that key would have failed.
/// </summary>
public sealed class BacktestBarCache
{
    private readonly ConcurrentDictionary<(string Symbol, TimeFrame Frame), Lazy<Task<IReadOnlyList<CandleData>>>> _loads = new();

    public Task<IReadOnlyList<CandleData>> GetOrLoadAsync(string symbol, TimeFrame frame, Func<Task<IReadOnlyList<CandleData>>> load)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(load);
        return _loads.GetOrAdd((symbol, frame), _ => new Lazy<Task<IReadOnlyList<CandleData>>>(load)).Value;
    }
}
