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

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace StockAnalyzer.Core.Services;

public class ScreenerService : IScreenerService
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
        // The Offset contract is enforced here, once, for every caller (the Screener tab and the Tickers-tab template filter): a condition beyond the
        // configured maximum is never evaluated and never sizes a history request.
        int maxOffset = ScreenerHistoryRequirements.GetMaxConditionOffset(condition);
        int allowedOffset = _settings.ScreenerMaxConditionOffset;
        if (maxOffset > allowedOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(condition), maxOffset,
                $"The condition uses an Offset of {maxOffset} bars, above the configured Screener:MaxConditionOffset of {allowedOffset}.");
        }

        if (symbols == null || !symbols.Any())
        {
            progress?.Report(100);
            return new List<string>();
        }

        // Each hit keeps the position of its symbol in the input, so the result order does not depend on thread scheduling.
        var matchedSymbols = new ConcurrentBag<(int Index, string Symbol)>();
        int processedCount = 0;

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = _settings.ScreenerMaxParallelism
        };

        var dataService = _dataService;

        try
        {
            await Parallel.ForEachAsync(symbols.Select((symbol, index) => (Symbol: symbol, Index: index)), parallelOptions, async (item, cancellationToken) =>
            {
                var (symbol, index) = (item.Symbol, item.Index);
                // Inner cancellation check is good practice
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    int requiredCandleCount = ScreenerHistoryRequirements.GetRequiredCandleCount(condition);
                    var candles = await dataService.LoadCandlesAsync(symbol, timeFrame, requiredCandleCount);
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

                    // Without comparison-symbol bars the original two-argument form runs, so a condition that overrides only that form is unaffected.
                    if (candles != null && await (secondary == null ? condition.IsMetAsync(candles, metadata) : condition.IsMetAsync(candles, metadata, secondary)))
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
