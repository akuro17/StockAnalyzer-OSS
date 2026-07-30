using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services;

public class ScreenerService
{
    private readonly IDataService _dataService;
    private readonly IStockAnalyzerSettings _settings;
    private readonly IEnumerable<IMarketDataProvider> _marketDataProviders;

    public ScreenerService(IDataService dataService, IStockAnalyzerSettings settings, IEnumerable<IMarketDataProvider> marketDataProviders)
    {
        _dataService = dataService;
        _settings = settings;
        _marketDataProviders = marketDataProviders;
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

    public async Task<List<string>> ScreenAsync(
        List<string> symbols,
        IScreeningCondition condition,
        TimeFrame timeFrame,
        IProgress<int> progress,
        CancellationToken ct)
    {
        if (symbols == null || !symbols.Any())
        {
            progress?.Report(100);
            return new List<string>();
        }

        var matchedSymbols = new ConcurrentBag<string>();
        int processedCount = 0;

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = _settings.ScreenerMaxParallelism
        };

        var dataService = _dataService;

        try
        {
            await Parallel.ForEachAsync(symbols, parallelOptions, async (symbol, cancellationToken) =>
            {
                // Inner cancellation check is good practice
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var candles = await dataService.LoadCandlesAsync(symbol, timeFrame, 200); // Load enough data
                    if (candles != null && await condition.IsMetAsync(candles))
                    {
                        matchedSymbols.Add(symbol);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Log and continue is a key requirement
                    Debug.WriteLine($"[ScreenerService] Error processing symbol '{symbol}': {ex.Message}");
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
            Debug.WriteLine("[ScreenerService] Screening operation was canceled by the user.");
            throw; // Re-throw to signal cancellation to the caller
        }

        // Ensure 100% is reported upon successful completion,
        // as the last report from the parallel loop might be delayed.
        if (!ct.IsCancellationRequested)
        {
            progress?.Report(100);
        }

        return matchedSymbols.ToList();
    }
}
