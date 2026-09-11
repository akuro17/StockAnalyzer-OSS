using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>DI boundary between the active chart data and the phase-space analysis view.</summary>
public interface ISpiralAnalysisDataSource
{
    SpiralAnalysisResult? Current { get; }
    bool HasCandles { get; }
    event EventHandler? Changed;
    void SetCandles(IReadOnlyList<CoreCandleData> candles);
    void Analyze(SpiralAnalysisParameters parameters);
}

/// <summary>Optional range operations for clients that expose P0 analysis controls.</summary>
public interface ISpiralAnalysisCandleRangeSource
{
    int CandleCount { get; }
    bool TryGetFirstPositiveClose(uint startIndex, uint endIndex, out decimal close);
}

/// <summary>Optional applied-price range operations backed by the shared indicator price definitions.</summary>
public interface ISpiralAnalysisPriceRangeSource
{
    bool TryGetFirstPositivePrice(uint startIndex, uint endIndex, PriceType priceType, out decimal price);
}

/// <summary>Optional revision marker that changes only when the chart candle set is replaced.</summary>
public interface ISpiralAnalysisDataRevisionSource
{
    long DataRevision { get; }
}

/// <summary>Optional asynchronous calculation boundary for latest-only reactive consumers.</summary>
public interface ISpiralAnalysisComputationSource
{
    Task<SpiralAnalysisResult> ComputeAsync(SpiralAnalysisParameters parameters, CancellationToken cancellationToken);
}
