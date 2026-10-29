using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Evaluation;

namespace StockAnalyzer.Core.Tests.ReadOnly;

/// <summary>
/// Gateway fake for the ticker-row tests: serves registry, metadata and daily bars from dictionaries and records every read,
/// so tests can assert how often (and in which order) the filter code touches the gateway. Only the three read members used by
/// <see cref="TickerFilterEvaluator"/> and <see cref="TickerGroupResolver"/> work; everything else throws.
/// </summary>
internal sealed class CountingTickerGateway : IReadOnlyStockAnalyzerGateway
{
    public List<string> Registry { get; set; } = new();
    public Dictionary<string, TickerMetadata> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<CandleData>> Candles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When set and it returns an exception for a symbol, that exception is thrown by <see cref="GetCandlesAsync"/>.</summary>
    public Func<string, Exception?>? CandleFailure { get; set; }

    public int CandlesCallCount { get; private set; }
    public int MetadataCallCount { get; private set; }
    public List<string> CandleSymbols { get; } = new();
    public List<string> MetadataSymbols { get; } = new();

    /// <summary>The bar count the caller asked for in the most recent <see cref="GetCandlesAsync"/> call.</summary>
    public int LastRequestedBarCount { get; private set; }

    public Task<IReadOnlyList<string>> GetAvailableTickersAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(Registry);

    public Task<IReadOnlyList<CandleData>> GetCandlesAsync(string symbol, TimeFrame timeFrame, int count, CancellationToken cancellationToken = default)
    {
        CandlesCallCount++;
        CandleSymbols.Add(symbol);
        LastRequestedBarCount = count;
        Exception? failure = CandleFailure?.Invoke(symbol);
        if (failure != null)
        {
            throw failure;
        }

        return Task.FromResult<IReadOnlyList<CandleData>>(Candles.TryGetValue(symbol, out var list) ? list : Array.Empty<CandleData>());
    }

    public Task<TickerMetadata?> GetStoredTickerMetadataAsync(string symbol, CancellationToken cancellationToken = default)
    {
        MetadataCallCount++;
        MetadataSymbols.Add(symbol);
        Metadata.TryGetValue(symbol, out var meta);
        return Task.FromResult<TickerMetadata?>(meta);
    }

    public Task<IReadOnlyList<CandleData>> GetCandlesUpToAsync(string symbol, TimeFrame timeFrame, DateOnly endDate, int count, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<DateTimeOffset?> GetTimeSeriesLastUpdatedAsync(string symbol, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<Dictionary<string, IIndicatorResult>> CalculateIndicatorsAsync(IReadOnlyList<CoreCandleData> candles, IEnumerable<CoreIndicatorSettings> settings, IReadOnlyDictionary<string, IReadOnlyList<CoreCandleData?>>? secondaryCandlesBySymbol = null)
        => throw new NotImplementedException();
    public ReverseWatchCurveData? CalculateReverseWatch(IReadOnlyList<CoreCandleData> candles, int period, string symbol, bool isMaBased = true, bool isLogScaleVolume = false, int dataCount = 0)
        => throw new NotImplementedException();
    public Task<IReadOnlyList<string>> RunScreenerAsync(IReadOnlyList<string> symbols, IScreeningCondition condition, TimeFrame timeFrame, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<ScreenerTemplateListing> ListScreenerTemplatesAsync(CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<ScreenerIndicatorTemplate?> GetScreenerTemplateAsync(Guid id, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<BacktestConditionTemplateListing> ListBacktestConditionTemplatesAsync(CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<BacktestConditionTemplate?> GetBacktestConditionTemplateAsync(Guid id, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<IReadOnlyList<string>> RunScreenerTemplateAsync(IReadOnlyList<string> symbols, ScreenerIndicatorTemplate template, TimeFrame timeFrame, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<BacktestEvaluationArtifact> RunBacktestAsync(BacktestRunSnapshot snapshot, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}
