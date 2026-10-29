using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Screener;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace StockAnalyzer.Core.Services;

public class ScreenerService : IScreenerService, IScreenerServiceFactory
{
    private readonly IDataService _dataService;
    private readonly IStockAnalyzerSettings _settings;
    private readonly IEnumerable<IMarketDataProvider> _marketDataProviders;
    private readonly ILogger<ScreenerService> _logger;

    public ScreenerService(
        IDataService dataService,
        IStockAnalyzerSettings settings,
        IEnumerable<IMarketDataProvider> marketDataProviders,
        ILogger<ScreenerService>? logger = null)
    {
        _dataService = dataService;
        _settings = settings;
        _marketDataProviders = marketDataProviders;
        _logger = logger ?? NullLogger<ScreenerService>.Instance;
    }

    public IScreenerService CreateScreener(IDataService dataService)
        => new ScreenerService(dataService ?? throw new ArgumentNullException(nameof(dataService)), _settings, _marketDataProviders, _logger);

    public async Task<ScreenerBarsResult> ScreenOnBarsAsync(
        List<string> symbols,
        IScreeningCondition condition,
        TimeFrame timeFrame,
        ScreenerBarSelection selection,
        IProgress<int> progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(selection);
        RequireSupportedTimeFrame(timeFrame);
        var bars = new ScreenerBarsDataService(_dataService, selection);
        List<string> matched = await CreateScreener(bars).ScreenAsync(symbols, condition, timeFrame, progress, ct).ConfigureAwait(false);
        return bars.ToResult(matched, symbols);
    }

    /// <summary>A scan over the bars of a selection builds them from the daily history, so only the frames the data layer defines can be asked for (the single definition is <see cref="DataServiceTimeFrames"/>).</summary>
    private static void RequireSupportedTimeFrame(TimeFrame timeFrame)
    {
        if (!DataServiceTimeFrames.Supported.Contains(timeFrame))
        {
            throw new ArgumentOutOfRangeException(nameof(timeFrame), timeFrame, "Only the time frames of DataServiceTimeFrames.Supported can be scanned over the bars of a selection.");
        }
    }

    public Task<IReadOnlyList<CandleData>> LoadScreenerBarsAsync(
        string symbol,
        TimeFrame timeFrame,
        ScreenerBarSelection selection,
        int count,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        RequireSupportedTimeFrame(timeFrame);
        ct.ThrowIfCancellationRequested();
        return new ScreenerBarsDataService(_dataService, selection).LoadCandlesAsync(symbol, timeFrame, count);
    }

    /// <summary>
    /// The Offset contract is enforced here, once, for every caller (the Screener tab and the Tickers-tab template filter) and every scan form: a condition beyond the
    /// configured maximum is never evaluated and never sizes a history request; a condition tree is validated against the configured depth and size limits.
    /// </summary>
    private void EnforceConditionContract(IScreeningCondition condition)
    {
        int maxOffset = ScreenerHistoryRequirements.GetMaxConditionOffset(condition);
        int allowedOffset = _settings.ScreenerMaxConditionOffset;
        if (maxOffset > allowedOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(condition), maxOffset,
                $"The condition uses an Offset of {maxOffset} bars, above the configured Screener:MaxConditionOffset of {allowedOffset}.");
        }

        if (condition is ScreenerConditionTreeCondition treeCondition)
        {
            ScreenerConditionTreeValidator.Validate(
                treeCondition.Root,
                allowedOffset,
                _settings.ScreenerMaxFilterTreeDepth,
                _settings.ScreenerMaxFilterTreeNodes);
        }
    }

    public async Task<ScreenerSideFramesResult> ScreenWithSideFramesAsync(
        List<string> symbols,
        IScreeningCondition condition,
        ScreenerBarSelection selection,
        IProgress<int> progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(selection);
        ct.ThrowIfCancellationRequested();

        EnforceConditionContract(condition);

        var noMatch = new ScreenerSideFramesResult(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<ScreenerFrameReport>());
        if (condition is ScreenerConditionTreeCondition tree && !ScreenerConditionTreeEvaluator.HasActiveLeaf(tree.Root))
        {
            progress?.Report(100);
            return noMatch;
        }

        if (symbols.Count == 0)
        {
            progress?.Report(100);
            return noMatch;
        }

        // A frame the data layer does not define is refused before anything is read.
        IReadOnlyList<TimeFrame> frames = ScreenerSideFrames.Collect(condition);
        TimeFrame? unsupported = frames.Where(frame => !DataServiceTimeFrames.Supported.Contains(frame)).Cast<TimeFrame?>().FirstOrDefault();
        if (unsupported is { } refused)
        {
            throw new ArgumentOutOfRangeException(nameof(condition), refused, "Only the time frames of DataServiceTimeFrames.Supported can be read by a side.");
        }

        // Daily sides over the stored history: the plain scan, unchanged.
        if (frames.Count == 1 && selection.AsOfDate is null)
        {
            List<string> plain = await ScreenAsync(symbols, condition, TimeFrame.D1, progress, ct).ConfigureAwait(false);
            return noMatch with { Matched = plain };
        }

        var bars = new ScreenerBarsDataService(_dataService, selection);
        int requiredCandleCount = ScreenerHistoryRequirements.GetRequiredCandleCount(condition);

        List<string> matched = await ScanSymbolsAsync(symbols, progress, ct, async symbol =>
        {
            IScreenerBarSet barSet = await bars.LoadBarSetAsync(symbol, frames, requiredCandleCount).ConfigureAwait(false);
            TickerMetadata metadata = await LoadMetadataAsync(symbol).ConfigureAwait(false);
            return await condition.IsMetOnBarsAsync(barSet, metadata).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return bars.ToSideFramesResult(matched, symbols, frames);
    }

    public async Task<List<string>> ScreenAsync(
        ScreeningCriteria criteria,
        IProgress<int> progress,
        CancellationToken ct)
    {
        progress?.Report(0);
        
        // Try to find if we have a provider that can handle optimized screening
        var provider = _marketDataProviders.OfType<ParquetMarketDataProvider>().FirstOrDefault();
        if (provider != null)
        {
            var result = await provider.ScreenAsync(criteria);
            progress?.Report(100);
            return result.ToList();
        }

        // Fallback or multiple condition screening would go here
        // For now, if no parquet provider, we might need to fallback to something else
        // or just return empty if this specific "Fast" path is requested.
        
        return new List<string>();
    }

    public Task<List<string>> ScreenAsync(
        List<string> symbols,
        IScreeningCondition condition,
        TimeFrame timeFrame,
        IProgress<int> progress,
        CancellationToken ct)
        => ScreenAsync(symbols, condition, timeFrame, null, progress, ct);

    public async Task<List<string>> ScreenAsync(
        List<string> symbols,
        IScreeningCondition condition,
        TimeFrame timeFrame,
        IScreenerSecondaryCandles? secondary,
        IProgress<int> progress,
        CancellationToken ct)
    {
        EnforceConditionContract(condition);

        if (condition is ScreenerConditionTreeCondition treeCondition && !ScreenerConditionTreeEvaluator.HasActiveLeaf(treeCondition.Root))
        {
            progress?.Report(100);
            return new List<string>();
        }

        if (symbols == null || !symbols.Any())
        {
            progress?.Report(100);
            return new List<string>();
        }

        var dataService = _dataService;

        return await ScanSymbolsAsync(symbols, progress, ct, async symbol =>
        {
            int requiredCandleCount = ScreenerHistoryRequirements.GetRequiredCandleCount(condition);
            var candles = await dataService.LoadCandlesAsync(symbol, timeFrame, requiredCandleCount);
            TickerMetadata metadata = await LoadMetadataAsync(symbol);

            // Without comparison-symbol bars the original two-argument form runs, so a condition that overrides only that form is unaffected.
            return candles != null && await (secondary == null ? condition.IsMetAsync(candles, metadata) : condition.IsMetAsync(candles, metadata, secondary));
        });
    }

    /// <summary>The stored metadata of <paramref name="symbol"/> from the first market-data provider; best effort: no provider or a failing read gives the empty metadata.</summary>
    private async Task<TickerMetadata> LoadMetadataAsync(string symbol)
    {
        TickerMetadata metadata = default;
        if (_marketDataProviders != null)
        {
            var provider = _marketDataProviders.FirstOrDefault();
            if (provider != null)
            {
                try
                {
                    metadata = await provider.GetMetadataAsync(symbol);
                }
                catch
                {
                    // Best-effort metadata retrieval
                }
            }
        }

        return metadata;
    }

    /// <summary>
    /// The one scan loop of every scan form: <paramref name="isMetAsync"/> (load the symbol's bars and metadata, evaluate the condition) runs for each symbol in parallel
    /// (<c>Screener:MaxParallelism</c>); a symbol whose evaluation throws is logged and counted as not met; progress is reported as symbols finish; a cancellation resets the fault
    /// snapshot and is rethrown; otherwise the evaluation faults are logged and 100 % is reported. The matches are returned in the order of <paramref name="symbols"/>.
    /// </summary>
    private async Task<List<string>> ScanSymbolsAsync(List<string> symbols, IProgress<int> progress, CancellationToken ct, Func<string, Task<bool>> isMetAsync)
    {
        // Each hit keeps the position of its symbol in the input, so the result order does not depend on thread scheduling.
        var matchedSymbols = new ConcurrentBag<(int Index, string Symbol)>();
        int processedCount = 0;

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = _settings.ScreenerMaxParallelism
        };

        try
        {
            await Parallel.ForEachAsync(symbols.Select((symbol, index) => (Symbol: symbol, Index: index)), parallelOptions, async (item, cancellationToken) =>
            {
                var (symbol, index) = (item.Symbol, item.Index);
                // Inner cancellation check is good practice
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (await isMetAsync(symbol))
                    {
                        matchedSymbols.Add((index, symbol));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Log and continue is a key requirement
                    _logger.LogError(ex, "[ScreenerService] Error processing symbol '{Symbol}'", symbol);
                }
                finally
                {
                    // Thread-safe progress update
                    int currentCount = Interlocked.Increment(ref processedCount);
                    progress?.Report((int)((double)currentCount / symbols.Count * 100));
                }
            });
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[ScreenerService] Screening operation was canceled by the user.");
            ScreenerValueExtractor.Default.Faults.TakeSnapshotAndReset();
            throw; // Re-throw to signal cancellation to the caller
        }

        // Ensure 100% is reported upon successful completion,
        // as the last report from the parallel loop might be delayed.
        if (!ct.IsCancellationRequested)
        {
            var faults = ScreenerValueExtractor.Default.Faults.TakeSnapshotAndReset();
            foreach (var fault in faults)
            {
                if (fault.Count > 0)
                {
                    _logger.LogWarning("[ScreenerService] {Count} evaluation fault(s) at {Site}; first: {FirstError}", fault.Count, fault.Site, fault.FirstError);
                }
            }

            progress?.Report(100);
        }

        return matchedSymbols.OrderBy(hit => hit.Index).Select(hit => hit.Symbol).ToList();
    }
}
