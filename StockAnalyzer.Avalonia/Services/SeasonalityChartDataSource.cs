using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Owns a dedicated daily-candle fetch for the seasonality polar chart. Unlike the phase-space
/// spiral source it does not reuse the chart's visible candles: a multi-year seasonality overlay
/// needs several years of daily bars regardless of the active chart timeframe (decision D9), so it
/// pulls its own <see cref="TimeFrame.D1"/> history through <see cref="IDataService"/> and caches it
/// per symbol. The chart draws the active symbol alone as series 0; comparison symbols and technical
/// indicators are no longer overlaid here.
/// </summary>
public sealed class SeasonalityChartDataSource : ISeasonalityChartDataSource
{
    private const int TradingDaysPerYearEstimate = 260;
    private const int FetchMarginBars = 40;

    private readonly IDataService _dataService;
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, CachedHistory> _historyCache = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyDictionary<int, SeasonalityRadiusMode> _radiusModeOverrides =
        new Dictionary<int, SeasonalityRadiusMode>();

    public SeasonalityChartDataSource(IDataService dataService)
    {
        _dataService = dataService ?? throw new ArgumentNullException(nameof(dataService));
    }

    public SeasonalityChartResult? Current { get; private set; }

    public string? ActiveSymbol { get; private set; }

    public bool HasActiveSymbol => !string.IsNullOrWhiteSpace(ActiveSymbol);

    public IReadOnlyDictionary<int, SeasonalityRadiusMode> RadiusModeOverrides => _radiusModeOverrides;

    public event EventHandler? Changed;

    public void SetActiveSymbol(string? symbol)
    {
        string? normalized = string.IsNullOrWhiteSpace(symbol) ? null : symbol.Trim();
        if (string.Equals(normalized, ActiveSymbol, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ActiveSymbol = normalized;
        Current = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetRadiusModeOverrides(IReadOnlyDictionary<int, SeasonalityRadiusMode>? overridesBySeriesId)
    {
        _radiusModeOverrides = overridesBySeriesId is { Count: > 0 }
            ? new Dictionary<int, SeasonalityRadiusMode>(overridesBySeriesId)
            : new Dictionary<int, SeasonalityRadiusMode>();
    }

    /// <summary>
    /// Returns the radius mode actually used for the base symbol: the caller's default unless the
    /// view supplied an override for series 0. A <see cref="SeasonalityRadiusMode.SignedUnitFromBounds"/>
    /// override is dropped for the price series (it carries no usable fixed scale), so the engine's
    /// bounds precondition is never violated.
    /// </summary>
    private (SeasonalityRadiusMode Mode, decimal? Low, decimal? High) ResolveRadiusMode(
        int seriesId,
        SeasonalityRadiusMode defaultMode)
    {
        if (!_radiusModeOverrides.TryGetValue(seriesId, out SeasonalityRadiusMode requested) || requested == defaultMode)
        {
            return (defaultMode, null, null);
        }

        if (requested == SeasonalityRadiusMode.SignedUnitFromBounds)
        {
            return (defaultMode, null, null);
        }

        return (requested, null, null);
    }

    public async Task AnalyzeAsync(SeasonalityChartParameters parameters, CancellationToken cancellationToken = default)
    {
        parameters.Validate();

        string? symbol = ActiveSymbol;
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new InvalidOperationException("No active symbol is available for seasonality analysis.");
        }

        int requiredBars = ((int)parameters.YearsToOverlay * TradingDaysPerYearEstimate) + FetchMarginBars;

        // Resume on the caller's context (the UI thread, via SeasonalityChartViewModel.RunAnalysisAsync)
        // so the Current write and the Changed fan-out below happen on the same thread that raised the
        // request. Publishing them from a thread-pool thread lets a subscriber's UI-thread handler read
        // a stale Current == null after the analysis finished and schedule yet another re-analysis,
        // which republishes and repeats: a 350 ms self-feeding loop that flickers the status text and,
        // with a second live view (a re-docked tab), saturates the dispatcher and freezes the app. The
        // engine work itself still runs off the UI thread on the explicit Task.Run below.
        IReadOnlyList<CoreCandleData> activeCandles =
            await GetCandlesAsync(symbol, requiredBars, cancellationToken).ConfigureAwait(true);

        (SeasonalityRadiusMode activeMode, decimal? activeLow, decimal? activeHigh) =
            ResolveRadiusMode(0, SeasonalityRadiusMode.PercentVsYearStart);

        var inputs = new List<SeasonalitySeriesInput>
        {
            new(
                seriesId: 0,
                label: symbol,
                radiusMode: activeMode,
                points: ToClosePoints(activeCandles),
                boundsLow: activeLow,
                boundsHigh: activeHigh),
        };

        cancellationToken.ThrowIfCancellationRequested();

        SeasonalityChartResult result = await Task.Run(
            () => SeasonalityChartEngine.Analyze(inputs, parameters),
            cancellationToken).ConfigureAwait(true);

        Current = result;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<IReadOnlyList<CoreCandleData>> GetCandlesAsync(string symbol, int requiredBars, CancellationToken cancellationToken)
    {
        lock (_cacheGate)
        {
            if (_historyCache.TryGetValue(symbol, out CachedHistory cached) && cached.BarCount >= requiredBars)
            {
                return cached.Candles;
            }
        }

        IReadOnlyList<CandleData> candles = await _dataService
            .LoadCandlesAsync(symbol, TimeFrame.D1, requiredBars)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<CoreCandleData> chronological = ToChronologicalCandles(candles);

        lock (_cacheGate)
        {
            _historyCache[symbol] = new CachedHistory(requiredBars, chronological);
        }

        return chronological;
    }

    /// <summary>
    /// Collapses the daily feed to one candle per calendar day (last one wins) and orders it strictly
    /// ascending, so <see cref="SeasonalityChartEngine"/>'s chronological guard never trips on a
    /// duplicated or out-of-order timestamp from the provider. The retained timestamp is normalized to
    /// the calendar date.
    /// </summary>
    private static IReadOnlyList<CoreCandleData> ToChronologicalCandles(IReadOnlyList<CandleData> candles)
    {
        if (candles is null || candles.Count == 0)
        {
            return Array.Empty<CoreCandleData>();
        }

        var byDay = new SortedDictionary<DateTime, CandleData>();
        foreach (CandleData candle in candles)
        {
            byDay[candle.Timestamp.Date] = candle;
        }

        var result = new CoreCandleData[byDay.Count];
        int index = 0;
        foreach (KeyValuePair<DateTime, CandleData> day in byDay)
        {
            CandleData c = day.Value;
            result[index++] = new CoreCandleData(day.Key, c.Open, c.High, c.Low, c.Close, c.Volume);
        }

        return result;
    }

    private static IReadOnlyList<SeasonalityPoint> ToClosePoints(IReadOnlyList<CoreCandleData> candles)
    {
        if (candles.Count == 0)
        {
            return Array.Empty<SeasonalityPoint>();
        }

        var points = new SeasonalityPoint[candles.Count];
        for (int i = 0; i < candles.Count; i++)
        {
            points[i] = new SeasonalityPoint(candles[i].Timestamp, candles[i].Close);
        }

        return points;
    }

    private readonly record struct CachedHistory(int BarCount, IReadOnlyList<CoreCandleData> Candles);
}
