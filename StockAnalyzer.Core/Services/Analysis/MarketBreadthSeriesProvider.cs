using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.ReadOnly;

namespace StockAnalyzer.Core.Services.Analysis;

/// <summary>
/// Loads constituent candles for a ticker group, runs <see cref="MarketBreadthCalculator"/>
/// over the full available history, and caches results per group, timeframe, and parameters.
/// </summary>
public sealed class MarketBreadthSeriesProvider : IMarketBreadthSeriesProvider
{
    /// <summary>Default maximum universe size, aligned with <c>McpLimits.MaxBreadthSymbols</c>.</summary>
    public const int DefaultMaxBreadthSymbols = 500;

    /// <summary>Default maximum symbols inspected when resolving filter groups, aligned with <c>McpLimits.MaxFilterEvaluationSymbols</c>.</summary>
    public const int DefaultMaxFilterEvaluationSymbols = 1000;

    /// <summary>Default LRU capacity for computed market breadth results.</summary>
    public const int DefaultCacheCapacity = 8;

    private readonly ITickerGroupResolver _groupResolver;
    private readonly IReadOnlyWorkspaceSource? _workspaceSource;
    private readonly IDataService _dataService;
    private readonly ITimeFrameManager? _timeFrameManager;
    private readonly ILogger<MarketBreadthSeriesProvider> _logger;
    private readonly int _maxBreadthSymbols;
    private readonly int _maxFilterEvaluationSymbols;
    private readonly BoundedLruCache<MarketBreadthCacheKey, MarketBreadthResult> _cache;

    public MarketBreadthSeriesProvider(
        ITickerGroupResolver groupResolver,
        IDataService dataService,
        ITimeFrameManager? timeFrameManager = null,
        ILogger<MarketBreadthSeriesProvider>? logger = null,
        int maxBreadthSymbols = DefaultMaxBreadthSymbols,
        int maxFilterEvaluationSymbols = DefaultMaxFilterEvaluationSymbols,
        int cacheCapacity = DefaultCacheCapacity,
        IReadOnlyWorkspaceSource? workspaceSource = null)
    {
        _groupResolver = groupResolver ?? throw new ArgumentNullException(nameof(groupResolver));
        _dataService = dataService ?? throw new ArgumentNullException(nameof(dataService));
        _timeFrameManager = timeFrameManager;
        _logger = logger ?? NullLogger<MarketBreadthSeriesProvider>.Instance;
        _workspaceSource = workspaceSource;

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBreadthSymbols);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFilterEvaluationSymbols);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cacheCapacity);

        _maxBreadthSymbols = maxBreadthSymbols;
        _maxFilterEvaluationSymbols = maxFilterEvaluationSymbols;
        _cache = new BoundedLruCache<MarketBreadthCacheKey, MarketBreadthResult>(cacheCapacity);
    }

    public async Task<MarketBreadthResult?> GetAsync(
        Guid groupId,
        TimeFrame timeFrame,
        MarketBreadthParameters parameters,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ct.ThrowIfCancellationRequested();

        if (groupId == Guid.Empty)
        {
            return null;
        }

        // The members of a group come from the saved workspace, so the version of the workspace read here (before the members are resolved) keys the result:
        // an edit of a watchlist or of a filter rule, once saved, misses the cache instead of returning the old members' breadth. A newer file than the version
        // can only cost one redundant recomputation. Changes of the stored prices are dropped by ClearCache() after a sync.
        string? workspaceVersion = _workspaceSource?.GetVersion();

        var cacheKey = new MarketBreadthCacheKey(
            groupId,
            timeFrame,
            parameters.AdRatioBars,
            parameters.McClellanFast,
            parameters.McClellanSlow,
            parameters.MaBars,
            parameters.HighLowBars,
            parameters.EndDate,
            workspaceVersion);

        if (_cache.TryGet(cacheKey, out MarketBreadthResult cached))
        {
            return cached;
        }

        int symbolCount = 0;
        try
        {
            int resolveCap = Math.Max(_maxBreadthSymbols, _maxFilterEvaluationSymbols);
            GroupMembers members = await _groupResolver.ResolveAsync(groupId, resolveCap, ct).ConfigureAwait(false);
            symbolCount = members.Symbols.Count;
            if (symbolCount == 0)
            {
                return null;
            }

            if (symbolCount > _maxBreadthSymbols)
            {
                _logger.LogWarning(
                    "Skipped market breadth calculation for group {GroupId} ({TimeFrame}): {SymbolCount} symbols exceeds limit {MaxBreadthSymbols}.",
                    groupId,
                    timeFrame,
                    symbolCount,
                    _maxBreadthSymbols);
                return null;
            }

            var universeCandles = new Dictionary<string, IReadOnlyList<CandleData>>(symbolCount, StringComparer.OrdinalIgnoreCase);
            foreach (string symbol in members.Symbols)
            {
                ct.ThrowIfCancellationRequested();
                universeCandles[symbol] = await LoadSymbolCandlesAsync(symbol, timeFrame, parameters.EndDate, ct).ConfigureAwait(false);
            }

            var effectiveParameters = new MarketBreadthParameters
            {
                AdRatioBars = parameters.AdRatioBars,
                McClellanFast = parameters.McClellanFast,
                McClellanSlow = parameters.McClellanSlow,
                MaBars = parameters.MaBars,
                HighLowBars = parameters.HighLowBars,
                OutputTailCount = null,
                IncludeSeries = null,
                TimeFrame = timeFrame,
                EndDate = parameters.EndDate
            };

            MarketBreadthResult result = MarketBreadthCalculator.Calculate(universeCandles, effectiveParameters);
            _cache.TryAdd(cacheKey, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to calculate market breadth series for group {GroupId} ({TimeFrame}, symbolCount={SymbolCount}, exceptionType={ExceptionType}).",
                groupId,
                timeFrame,
                symbolCount,
                ex.GetType().Name);
            return null;
        }
    }

    public void ClearCache() => _cache.Clear();

    private async Task<IReadOnlyList<CandleData>> LoadSymbolCandlesAsync(
        string symbol,
        TimeFrame timeFrame,
        DateOnly? endDate,
        CancellationToken ct)
    {
        if (endDate is { } cutoff)
        {
            IReadOnlyList<CandleData> cutoffDaily = await _dataService
                .LoadCandlesAsync(symbol, TimeFrame.D1, count: 0, includePartialBar: true, cancellationToken: ct)
                .ConfigureAwait(false);
            if (cutoffDaily.Count == 0 && _timeFrameManager is not null)
            {
                cutoffDaily = await _timeFrameManager
                    .GetCandlesAsync(symbol, TimeFrame.D1, count: 0, cancellationToken: ct)
                    .ConfigureAwait(false);
            }

            return cutoffDaily.Count == 0
                ? Array.Empty<CandleData>()
                : AsOfCandleWindow.Build(cutoffDaily, cutoff, timeFrame, count: 0);
        }

        IReadOnlyList<CandleData> direct = await _dataService
            .LoadCandlesAsync(symbol, timeFrame, count: 0, includePartialBar: true, cancellationToken: ct)
            .ConfigureAwait(false);

        if (direct.Count > 0 || !TimeFrameAggregator.IsPeriodFrame(timeFrame))
        {
            return direct;
        }

        if (_timeFrameManager is not null)
        {
            IReadOnlyList<CandleData> managed = await _timeFrameManager
                .GetCandlesAsync(symbol, timeFrame, count: 0, cancellationToken: ct)
                .ConfigureAwait(false);
            if (managed.Count > 0)
            {
                return managed;
            }
        }

        IReadOnlyList<CandleData> daily = await _dataService
            .LoadCandlesAsync(symbol, TimeFrame.D1, count: 0, includePartialBar: true, cancellationToken: ct)
            .ConfigureAwait(false);

        return daily.Count == 0
            ? Array.Empty<CandleData>()
            : TimeFrameAggregator.AggregateToPeriodStart(daily, timeFrame);
    }

    private readonly record struct MarketBreadthCacheKey(
        Guid GroupId,
        TimeFrame TimeFrame,
        int AdRatioBars,
        int McClellanFast,
        int McClellanSlow,
        int MaBars,
        int HighLowBars,
        DateOnly? EndDate,
        string? WorkspaceVersion);
}
