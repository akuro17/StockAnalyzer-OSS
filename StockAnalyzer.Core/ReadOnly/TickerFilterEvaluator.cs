using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services.Watchlist;

namespace StockAnalyzer.Core.ReadOnly;

/// <summary>
/// Evaluates a set of <see cref="FilterRule"/> (all rules AND-ed through <see cref="FilterRuleEvaluator"/>) over symbols, building each
/// <see cref="TickerRow"/> from stored metadata and, only when a rule needs it, the latest daily bars.
/// One instance is one evaluation session: it caches metadata and rows per symbol, so a symbol that is evaluated by several rule sets
/// (stacked filter nodes) is read from the gateway once. Not thread-safe.
/// </summary>
public sealed class TickerFilterEvaluator
{
    /// <summary>Daily bars read per symbol: the latest bar and the one before it, the input of Change and ChangePercent in <see cref="TickerRowCalculator.Compute"/>.</summary>
    private const int RowCandleCount = 2;

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly Dictionary<string, TickerMetadata> _metadataCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TickerRow> _rowWithoutCandlesCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TickerRow> _rowWithCandlesCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _hasBarsCache = new(StringComparer.OrdinalIgnoreCase);

    public TickerFilterEvaluator(IReadOnlyStockAnalyzerGateway gateway)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
    }

    /// <summary>
    /// True when any rule reads a candle-dependent <see cref="TickerRow"/> field, either as its <see cref="FilterRule.Field"/> or, for a
    /// field comparison, as its <see cref="FilterRule.Value"/>.
    /// </summary>
    public static bool NeedsCandles(IReadOnlyList<FilterRule>? rules)
    {
        if (rules == null || rules.Count == 0) return false;
        for (int i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (TickerRow.RequiresCandles(rule.Field))
            {
                return true;
            }
            if (rule.IsCompareToField && TickerRow.RequiresCandles(rule.Value))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The row of <paramref name="symbol"/>. A row that already holds candle data is reused even when <paramref name="needsCandles"/> is false;
    /// otherwise a metadata-only row is built when no candle field is needed, and the latest daily bars are read when it is.
    /// A bar read that fails with <see cref="ArgumentException"/> counts as "no bars"; <see cref="DataUnavailableException"/> propagates.
    /// </summary>
    public async Task<TickerRow> GetRowAsync(string symbol, bool needsCandles, CancellationToken cancellationToken = default)
    {
        if (_rowWithCandlesCache.TryGetValue(symbol, out var fullRow))
        {
            return fullRow;
        }

        if (!needsCandles)
        {
            if (!_rowWithoutCandlesCache.TryGetValue(symbol, out var metaRow))
            {
                var meta = await GetMetadataAsync(symbol, cancellationToken).ConfigureAwait(false);
                metaRow = TickerRowCalculator.Compute(symbol, meta, Array.Empty<CandleData>());
                _rowWithoutCandlesCache[symbol] = metaRow;
            }
            return metaRow;
        }

        var metadata = await GetMetadataAsync(symbol, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<CandleData> candles;
        try
        {
            candles = await _gateway.GetCandlesAsync(symbol, TimeFrame.D1, RowCandleCount, cancellationToken).ConfigureAwait(false);
        }
        catch (DataUnavailableException)
        {
            throw;
        }
        catch (ArgumentException)
        {
            candles = Array.Empty<CandleData>();
        }

        fullRow = TickerRowCalculator.Compute(symbol, metadata, candles);
        _rowWithCandlesCache[symbol] = fullRow;
        _hasBarsCache[symbol] = candles.Count > 0;
        return fullRow;
    }

    /// <summary>
    /// True when at least one daily bar of <paramref name="symbol"/> is stored. The bars are read the way <see cref="GetRowAsync"/> reads them
    /// for a candle-dependent rule and kept, so a symbol that is checked here and then evaluated with such a rule is read once.
    /// </summary>
    public async Task<bool> HasStoredBarsAsync(string symbol, CancellationToken cancellationToken = default)
    {
        if (!_hasBarsCache.TryGetValue(symbol, out bool hasBars))
        {
            await GetRowAsync(symbol, needsCandles: true, cancellationToken).ConfigureAwait(false);
            hasBars = _hasBarsCache[symbol];
        }

        return hasBars;
    }

    /// <summary>
    /// The symbols of <paramref name="symbols"/> for which every rule holds, in ordinal ascending order. Symbols are evaluated in input
    /// order and cancellation is checked before each one. An empty rule list matches every symbol but still builds each row, so a caller
    /// that wants to avoid the reads for "no rules" short-circuits before calling, as <see cref="TickerGroupResolver"/> does.
    /// </summary>
    public async Task<IReadOnlyList<string>> FilterAsync(IReadOnlyList<string> symbols, IReadOnlyList<FilterRule> rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(rules);

        var filterSettings = new FilterSettings { Rules = rules.ToList() };
        bool needsCandles = NeedsCandles(rules);
        var matched = new List<string>();

        foreach (var symbol in symbols)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var row = await GetRowAsync(symbol, needsCandles, cancellationToken).ConfigureAwait(false);
            if (FilterRuleEvaluator.Evaluate(row, filterSettings))
            {
                matched.Add(symbol);
            }
        }

        return matched.OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    private async Task<TickerMetadata> GetMetadataAsync(string symbol, CancellationToken cancellationToken)
    {
        if (!_metadataCache.TryGetValue(symbol, out var meta))
        {
            meta = await _gateway.GetStoredTickerMetadataAsync(symbol, cancellationToken).ConfigureAwait(false) ?? TickerMetadata.Unknown;
            _metadataCache[symbol] = meta;
        }
        return meta;
    }
}
