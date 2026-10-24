using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Templates;

namespace StockAnalyzer.Core.Services.Screener;

/// <summary>
/// New, standalone evaluator (not a modification of <see cref="IScreenerService"/> or the Screener
/// tab's own <c>ScreenerViewModel.ScanAsync</c>): reuses the same entry-combination convention those
/// already establish - evaluate each enabled entry via <see cref="IScreenerService.ScreenAsync"/> in
/// order, then union or intersect it into the running result according to the PRECEDING entry's
/// <see cref="LogicalOperator"/> (Or unions, And/anything else intersects) - without changing either
/// existing implementation's behavior.
/// </summary>
public sealed class ScreenerTemplateEvaluator : IScreenerTemplateEvaluator
{
    private readonly IScreenerService _screenerService;

    public ScreenerTemplateEvaluator(IScreenerService screenerService)
    {
        _screenerService = screenerService;
    }

    public Task<HashSet<string>> EvaluateAsync(
        IReadOnlyList<string> symbols,
        ScreenerIndicatorTemplate template,
        TimeFrame timeFrame,
        IProgress<int>? progress,
        CancellationToken ct)
        => EvaluateAsync(symbols, template, timeFrame, null, progress, ct);

    public async Task<HashSet<string>> EvaluateAsync(
        IReadOnlyList<string> symbols,
        ScreenerIndicatorTemplate template,
        TimeFrame timeFrame,
        IScreenerSecondaryCandles? secondary,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        var entries = template.Entries.Where(e => e.IsEnabled).ToList();
        if (entries.Count == 0 || symbols.Count == 0)
        {
            return new HashSet<string>();
        }

        HashSet<string>? combined = null;
        var symbolList = symbols.ToList();

        for (int i = 0; i < entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var entry = entries[i];
            // Without comparison-symbol bars the original overload runs, so an IScreenerService that implements only that one is unaffected.
            var hits = secondary == null
                ? await _screenerService.ScreenAsync(symbolList, entry, timeFrame, progress!, ct)
                : await _screenerService.ScreenAsync(symbolList, entry, timeFrame, secondary, progress!, ct);
            var hitSet = new HashSet<string>(hits);

            if (combined == null)
            {
                combined = hitSet;
            }
            else if (entries[i - 1].LogicalOperator == LogicalOperator.Or)
            {
                combined.UnionWith(hitSet);
            }
            else
            {
                combined.IntersectWith(hitSet);
            }
        }

        return combined ?? new HashSet<string>();
    }
}
