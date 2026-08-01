using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.Retry;
using StockAnalyzer.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Service responsible for fetching and synchronizing historical data across multiple symbols.
/// </summary>
public class ComparisonDataAligner : IComparisonDataAligner
{
    private const int MaxForwardFillDays = 30;
    private const double MaxNullRatio = 0.50;
    
    public int MaxComparisonSymbols { get; set; } = 5;

    private readonly IDataService _dataService;
    private readonly ILogger<ComparisonDataAligner> _logger;
    private readonly ResiliencePipeline _resiliencePipeline;

    public ComparisonDataAligner(IDataService dataService, ILogger<ComparisonDataAligner>? logger = null)
    {
        _dataService = dataService;
        _logger = logger ?? NullLogger<ComparisonDataAligner>.Instance;
        _resiliencePipeline = BuildResiliencePipeline();
    }

    private ResiliencePipeline BuildResiliencePipeline()
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(200),
                OnRetry = args =>
                {
                    _logger.LogWarning(args.Outcome.Exception, "Data fetch failed. Retrying ({AttemptNumber}/3)...", args.AttemptNumber);
                    return default;
                },
                ShouldHandle = new PredicateBuilder().Handle<Exception>()
            })
            .Build();
    }

    public async Task<ComparisonAlignedData> AlignAsync(
        string primarySymbol, 
        IReadOnlyList<string> comparisonSymbols, 
        TimeFrame timeFrame, 
        int candleCount,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Starting data alignment for {PrimarySymbol} with {Count} comparison targets.", primarySymbol, comparisonSymbols.Count);

        // 1. Fetch Primary Symbol Data (Base Timeline)
        IReadOnlyList<CandleData> primaryCandles;
        try
        {
            primaryCandles = await _resiliencePipeline.ExecuteAsync(
                async t => await _dataService.LoadCandlesAsync(primarySymbol, timeFrame, candleCount), ct);
            
            if (primaryCandles.Count == 0)
            {
                throw new InvalidOperationException($"No data found for primary symbol: {primarySymbol}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load primary symbol data: {PrimarySymbol}", primarySymbol);
            throw; // Re-throw for base symbol as it is required
        }

        var timestamps = primaryCandles.Select(c => c.Timestamp).ToArray();
        var warnings = new List<string>();
        var series = new Dictionary<string, CandleData?[]>();

        // 2. Fetch Comparison Symbols in Parallel
        // De-duplicate comparison symbols and remove primary symbol if present
        var uniqueSymbols = comparisonSymbols
            .Where(s => !string.Equals(s, primarySymbol, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxComparisonSymbols)
            .ToList();

        var fetchTasks = uniqueSymbols.ToDictionary(
            s => s,
            s => _resiliencePipeline.ExecuteAsync(async t => await _dataService.LoadCandlesAsync(s, timeFrame, candleCount), ct).AsTask()
        );

        try
        {
            await Task.WhenAll(fetchTasks.Values);
        }
        catch
        {
            // Individual task exceptions are handled in the alignment loop below
            _logger.LogDebug("One or more comparison ticker fetches failed. Handling individually.");
        }

        // 3. Align Each Series
        foreach (var symbol in uniqueSymbols)
        {
            try
            {
                var candles = await fetchTasks[symbol];
                if (candles == null || candles.Count == 0)
                {
                    warnings.Add($"No data found for {symbol}. Excluded from comparison.");
                    continue;
                }

                var aligned = AlignSeries(symbol, timestamps, candles);
                
                // Validate quality
                int nullCount = aligned.Count(c => c == null);
                double nullRatio = (double)nullCount / timestamps.Length;

                if (nullRatio > MaxNullRatio)
                {
                    warnings.Add($"{symbol} excluded due to high data fragmentation ({(nullRatio * 100):F1}% missing).");
                    continue;
                }

                series[symbol] = aligned;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to align comparison symbol: {Symbol}", symbol);
                warnings.Add($"Failed to load {symbol}. Comparison skipped for this ticker.");
            }
        }

        // 4. Include Primary Symbol in Series (for consistent rendering and scaling)
        var primaryAligned = new CandleData?[primaryCandles.Count];
        for (int i = 0; i < primaryCandles.Count; i++) primaryAligned[i] = primaryCandles[i];
        series[primarySymbol] = primaryAligned;

        return new ComparisonAlignedData(primarySymbol, timestamps, series, warnings);
    }

    private CandleData?[] AlignSeries(string symbol, DateTime[] baseTimeline, IReadOnlyList<CandleData> rawData)
    {
        var result = new CandleData?[baseTimeline.Length];
        
        // Dictionary for O(1) lookup of existing data - Safe against duplicate timestamps
        var lookup = new Dictionary<DateTime, CandleData>();
        foreach (var candle in rawData)
        {
            lookup.TryAdd(candle.Timestamp, candle);
        }
        
        CandleData? lastKnown = null;
        int gapCount = 0;

        for (int i = 0; i < baseTimeline.Length; i++)
        {
            var ts = baseTimeline[i];
            
            if (lookup.TryGetValue(ts, out var candle))
            {
                result[i] = candle;
                lastKnown = candle;
                gapCount = 0;
            }
            else
            {
                // LOCF (Last Observation Carried Forward)
                if (lastKnown.HasValue && gapCount < MaxForwardFillDays)
                {
                    var prev = lastKnown.Value;
                    // Forward-fill by carrying over the Close price. Volume is set to 0.
                    result[i] = new CandleData(ts, prev.Close, prev.Close, prev.Close, prev.Close, 0);
                    gapCount++;
                }
                else
                {
                    // Lead data missing or gap exceeds limit
                    result[i] = null;
                }
            }
        }

        return result;
    }
}
