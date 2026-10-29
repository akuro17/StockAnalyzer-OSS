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

    public async Task<HashSet<string>> EvaluateWithSideFramesAsync(
        IReadOnlyList<string> symbols,
        ScreenerIndicatorTemplate template,
        ScreenerBarSelection selection,
        IProgress<int>? progress,
        CancellationToken ct)
        => (await EvaluateWithSideFramesReportAsync(symbols, template, selection, progress, ct).ConfigureAwait(false)).Matched.ToHashSet();

    public async Task<ScreenerSideFramesResult> EvaluateWithSideFramesReportAsync(
        IReadOnlyList<string> symbols,
        ScreenerIndicatorTemplate template,
        ScreenerBarSelection selection,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(selection);

        var entries = template.Entries.Where(e => e.IsEnabled).ToList();
        if (entries.Count == 0 || symbols.Count == 0)
        {
            return new ScreenerSideFramesResult(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<ScreenerFrameReport>());
        }

        // The same fold as EvaluateAsync (the preceding entry's LogicalOperator: Or unions, anything else intersects); only the scan of one entry differs.
        HashSet<string>? combined = null;
        var scans = new List<ScreenerSideFramesResult>(entries.Count);
        var symbolList = symbols.ToList();

        for (int i = 0; i < entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            ScreenerSideFramesResult scan = await _screenerService.ScreenWithSideFramesAsync(symbolList, entries[i], selection, progress!, ct);
            scans.Add(scan);
            var hitSet = new HashSet<string>(scan.Matched);

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

        return Merge(combined ?? new HashSet<string>(), scans);
    }

    /// <summary>
    /// The matched symbols with the reports of the entry scans merged. Every entry scans the same symbols over the same selection, so the symbols without bars and the partial
    /// periods of a frame are the same for each entry that reads it (united here), and so is the number of skipped partial bars (the largest is kept).
    /// </summary>
    private static ScreenerSideFramesResult Merge(HashSet<string> matched, IReadOnlyList<ScreenerSideFramesResult> scans)
    {
        var withoutDaily = new SortedSet<string>(StringComparer.Ordinal);
        var frames = new Dictionary<TimeFrame, (SortedSet<string> Without, SortedSet<DateOnly> Starts, int Skipped)>();
        foreach (ScreenerSideFramesResult scan in scans)
        {
            withoutDaily.UnionWith(scan.SymbolsWithoutDailyBars);
            foreach (ScreenerFrameReport report in scan.Frames)
            {
                if (!frames.TryGetValue(report.Frame, out var merged))
                {
                    merged = (new SortedSet<string>(StringComparer.Ordinal), new SortedSet<DateOnly>(), 0);
                }

                merged.Without.UnionWith(report.SymbolsWithoutBars);
                merged.Starts.UnionWith(report.PartialLastBarPeriodStarts);
                frames[report.Frame] = (merged.Without, merged.Starts, Math.Max(merged.Skipped, report.SkippedPartialBarCount));
            }
        }

        List<TimeFrame> order = DataServiceTimeFrames.Supported.ToList();
        var reports = frames
            .OrderBy(pair => order.IndexOf(pair.Key))
            .Select(pair => new ScreenerFrameReport(pair.Key, pair.Value.Without.ToList(), pair.Value.Starts.ToList(), pair.Value.Skipped))
            .ToList();
        return new ScreenerSideFramesResult(matched.ToList(), withoutDaily.ToList(), reports);
    }
}
