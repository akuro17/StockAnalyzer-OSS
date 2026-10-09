using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.ReinforcementLearning;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// In-memory real <see cref="CandleData"/> bars for the portfolio tests: one price rule per symbol, so every expected number in a test can be
/// computed by hand from the rule. Records every read so a test can also prove which indices were touched.
/// </summary>
internal sealed class RlPanelBarsFixture : IRlPanelBars
{
    private readonly Func<int, int, (decimal Open, decimal Close)?> _prices;
    public List<(int Symbol, int Index)> Reads { get; } = new();
    public int SymbolCount { get; }

    public RlPanelBarsFixture(int symbols, Func<int, int, (decimal Open, decimal Close)?> prices) { SymbolCount = symbols; _prices = prices; }

    /// <summary>Flat prices per symbol with optional (symbol, index) overrides of open and close.</summary>
    public static RlPanelBarsFixture Flat(decimal[] prices, IReadOnlyDictionary<(int Symbol, int Index), (decimal Open, decimal Close)>? overrides = null)
        => new(prices.Length, (s, t) => overrides is not null && overrides.TryGetValue((s, t), out var o) ? o : (prices[s], prices[s]));

    public bool TryGetBar(int symbolIndex, int calendarIndex, out CandleData bar)
    {
        Reads.Add((symbolIndex, calendarIndex));
        var price = _prices(symbolIndex, calendarIndex);
        if (price is null) { bar = default; return false; }
        var (open, close) = price.Value;
        bar = new CandleData(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(calendarIndex), open,
            Math.Max(open, close) + 1m, Math.Max(Math.Min(open, close) - 1m, 0.01m), close, 1_000);
        return true;
    }

    /// <summary>A block starting at calendar index 0 (first decision at index 63) whose eligible symbols are the given ones.</summary>
    public static RlPanelBlock Block(params int[] eligible)
        => new("train:0:192", RlPortfolioContract.TrainPartition, 0, RlContract.SupportLength, eligible.ToImmutableArray());
}
