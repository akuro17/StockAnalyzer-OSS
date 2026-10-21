#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Tests.TestHelpers;

/// <summary>
/// Shared <see cref="IDataService"/> test double for a fixed, in-memory symbol->candles map. A symbol that is not in the map yields an empty list;
/// a symbol in <paramref name="failing"/> throws. Every call records the requested bar count per symbol. Built from a symbol map, it answers every
/// time frame with the same candles; built from a (symbol, time frame) map, it answers each time frame with its own candles and an unknown pair with an
/// empty list (a ticker with no Weekly file).
/// </summary>
public sealed class FakeDataService : IDataService
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<CandleData>>? _bySymbol;
    private readonly IReadOnlyDictionary<(string Symbol, TimeFrame Frame), IReadOnlyList<CandleData>>? _byFrame;
    private readonly ISet<string> _failing;
    private readonly Func<string, Task>? _beforeLoad;
    private readonly ConcurrentDictionary<TimeFrame, byte> _requestedTimeFrames = new();

    public FakeDataService(
        IReadOnlyDictionary<string, IReadOnlyList<CandleData>> bySymbol,
        ISet<string>? failing = null,
        Func<string, Task>? beforeLoad = null)
    {
        _bySymbol = bySymbol;
        _failing = failing ?? new HashSet<string>();
        _beforeLoad = beforeLoad;
    }

    public FakeDataService(
        IReadOnlyDictionary<(string Symbol, TimeFrame Frame), IReadOnlyList<CandleData>> byFrame,
        ISet<string>? failing = null,
        Func<string, Task>? beforeLoad = null)
    {
        _byFrame = byFrame;
        _failing = failing ?? new HashSet<string>();
        _beforeLoad = beforeLoad;
    }

    /// <summary>The <c>count</c> argument of every <see cref="LoadCandlesAsync"/> call, by symbol (the last call of a symbol wins, whatever its time frame).</summary>
    public ConcurrentDictionary<string, int> RequestedCounts { get; } = new();

    /// <summary>The <c>count</c> argument of every <see cref="LoadCandlesAsync"/> call, by (symbol, time frame).</summary>
    public ConcurrentDictionary<(string Symbol, TimeFrame Frame), int> RequestedCountsByFrame { get; } = new();

    /// <summary>Every time frame any <see cref="LoadCandlesAsync"/> call asked for, in no particular order.</summary>
    public IReadOnlyCollection<TimeFrame> RequestedTimeFrames => _requestedTimeFrames.Keys.ToArray();

    public async Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count = 100)
    {
        RequestedCounts[symbol] = count;
        RequestedCountsByFrame[(symbol, timeFrame)] = count;
        _requestedTimeFrames[timeFrame] = 0;
        if (_beforeLoad is not null)
        {
            await _beforeLoad(symbol).ConfigureAwait(false);
        }

        if (_failing.Contains(symbol))
        {
            throw new InvalidOperationException($"FakeDataService: configured failure for '{symbol}'.");
        }

        IReadOnlyList<CandleData>? candles = null;
        bool found = _byFrame is not null
            ? _byFrame.TryGetValue((symbol, timeFrame), out candles)
            : _bySymbol!.TryGetValue(symbol, out candles);
        return found ? candles! : Array.Empty<CandleData>();
    }
}
