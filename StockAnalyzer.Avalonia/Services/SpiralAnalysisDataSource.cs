using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>Stores the active chart candles and publishes explicitly requested analysis results.</summary>
public sealed class SpiralAnalysisDataSource : ISpiralAnalysisDataSource, ISpiralAnalysisCandleRangeSource,
    ISpiralAnalysisPriceRangeSource, ISpiralAnalysisDataRevisionSource, ISpiralAnalysisComputationSource
{
    private readonly SemaphoreSlim _computationGate = new(1, 1);
    private IReadOnlyList<CoreCandleData> _candles = Array.Empty<CoreCandleData>();

    public SpiralAnalysisResult? Current { get; private set; }
    public bool HasCandles => _candles.Count > 0;
    public int CandleCount => _candles.Count;
    public long DataRevision { get; private set; }
    public event EventHandler? Changed;

    public void SetCandles(IReadOnlyList<CoreCandleData> candles)
    {
        _candles = candles ?? Array.Empty<CoreCandleData>();
        Current = null;
        DataRevision++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Analyze(SpiralAnalysisParameters parameters)
    {
        Current = SpiralPriceModelEngine.Analyze(_candles, parameters);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<SpiralAnalysisResult> ComputeAsync(SpiralAnalysisParameters parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<CoreCandleData> candleSnapshot = _candles;
        await _computationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () => SpiralPriceModelEngine.Analyze(candleSnapshot, parameters, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _computationGate.Release();
        }
    }

    public bool TryGetFirstPositiveClose(uint startIndex, uint endIndex, out decimal close)
    {
        close = 0m;
        if (startIndex > endIndex || endIndex >= (uint)_candles.Count) return false;

        for (uint index = startIndex; index <= endIndex; index++)
        {
            decimal candidate = _candles[(int)index].Close;
            if (candidate > 0m)
            {
                close = candidate;
                return true;
            }
        }

        return false;
    }

    public bool TryGetFirstPositivePrice(uint startIndex, uint endIndex, PriceType priceType, out decimal price)
    {
        price = 0m;
        if (startIndex > endIndex || endIndex >= (uint)_candles.Count) return false;

        IReadOnlyList<decimal> prices = PriceDataHelper.ExtractNonNullablePriceSeries(_candles, priceType);
        for (uint index = startIndex; index <= endIndex; index++)
        {
            decimal candidate = prices[(int)index];
            if (candidate > 0m)
            {
                price = candidate;
                return true;
            }
        }

        return false;
    }
}
