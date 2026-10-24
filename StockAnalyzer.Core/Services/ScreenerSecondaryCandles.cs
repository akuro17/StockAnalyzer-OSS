using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Analysis;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// The comparison-symbol bars of one scan, loaded once and shared by the evaluation of every symbol (read only).
/// A symbol is looked up the way the chart looks up the secondary series of an indicator (<c>AnalysisPipelineService.ResolveSecondaryCandles</c>),
/// so the same comparison symbol text resolves to the same series on both paths.
/// </summary>
public sealed class ScreenerSecondaryCandles : IScreenerSecondaryCandles
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<CoreCandleData?>> _bySymbol;

    public ScreenerSecondaryCandles(IReadOnlyDictionary<string, IReadOnlyList<CoreCandleData?>> bySymbol)
    {
        _bySymbol = bySymbol ?? throw new ArgumentNullException(nameof(bySymbol));
    }

    public IReadOnlyList<CoreCandleData?>? Get(string comparisonSymbol)
        => AnalysisPipelineService.ResolveSecondaryCandles(_bySymbol, comparisonSymbol);

    /// <summary>
    /// Loads the bars of every symbol in <paramref name="symbols"/> (each once) with <paramref name="requiredCount"/> bars (0 = the whole history),
    /// the way the Screener loads the bars of the symbols it evaluates. A symbol with no bars is left out, so its sides have no value.
    /// </summary>
    public static async Task<ScreenerSecondaryCandles> LoadAsync(
        IDataService dataService,
        IEnumerable<string> symbols,
        TimeFrame timeFrame,
        int requiredCount,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dataService);
        ArgumentNullException.ThrowIfNull(symbols);

        var loaded = new Dictionary<string, IReadOnlyList<CoreCandleData?>>(StringComparer.OrdinalIgnoreCase);
        foreach (string symbol in symbols.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<CandleData> bars = await dataService.LoadCandlesAsync(symbol, timeFrame, requiredCount).ConfigureAwait(false);
            if (bars is { Count: > 0 })
            {
                loaded[symbol] = bars.Select(bar => (CoreCandleData?)new CoreCandleData(bar.Timestamp, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume)).ToList();
            }
        }

        return new ScreenerSecondaryCandles(loaded);
    }
}

/// <summary>The comparison symbols a screening condition names: the <c>ComparisonSymbol</c> parameter of its indicator sides.</summary>
public static class ScreenerComparisonSymbols
{
    private static readonly string ParameterName = nameof(ICrossTickerParameter.ComparisonSymbol);

    /// <summary>The comparison symbol the indicator <paramref name="side"/> names, or null when it names none.</summary>
    public static string? Of(ScreenerIndicatorSideConfig? side)
    {
        if (side == null || side.CategoryType != ScreenerItemCategoryType.Indicator || side.Parameters == null)
        {
            return null;
        }

        foreach (KeyValuePair<string, object> parameter in side.Parameters)
        {
            if (!string.Equals(parameter.Key, ParameterName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? text = parameter.Value switch
            {
                string value => value,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                _ => null
            };
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        return null;
    }
}
