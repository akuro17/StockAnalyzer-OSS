using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Models.Indicators.Trend;

[StockAnalyzerIndicator(IndicatorType.SSA)]
public class CoreSSAIndicator : CoreIndicatorBase
{
    public int WindowSize { get; set; } = IndicatorDefaultConstants.SsaDefaultWindowSize;
    public int EmbeddingDimension { get; set; } = IndicatorDefaultConstants.SsaDefaultEmbeddingDimension;
    public int NumComponents { get; set; } = IndicatorDefaultConstants.SsaDefaultNumComponents;
    public override string Name => $"SSA ({WindowSize}, {EmbeddingDimension}, {NumComponents})";
    public override bool IsOverlay => true;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSSAParameter p)
        {
            WindowSize = p.WindowSize;
            EmbeddingDimension = p.EmbeddingDimension;
            NumComponents = p.NumComponents;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();

        if (candles == null || candles.Count == 0)
        {
            return IndicatorResult.Success(_values);
        }

        int n = candles.Count;
        int window = Math.Max(SsaDecompositionEngine.MinSampleCount, WindowSize);
        if (n < window)
        {
            _values.AddRange(Enumerable.Repeat<decimal?>(null, n));
            return IndicatorResult.Success(_values);
        }

        var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
        double[] doublePrices = new double[n];
        for (int i = 0; i < n; i++)
        {
            doublePrices[i] = (double)(priceSeries[i] ?? 0m);
        }

        for (int i = 0; i < window - 1; i++)
        {
            _values.Add(null);
        }

        for (int i = window - 1; i < n; i++)
        {
            ReadOnlySpan<double> windowSpan = doublePrices.AsSpan(i - window + 1, window);
            double endpoint = SsaDecompositionEngine.ComputeCausalEndpoint(
                windowSpan,
                EmbeddingDimension,
                NumComponents,
                SsaDetrendMode.LeastSquaresLinear);

            if (double.IsNaN(endpoint) || double.IsInfinity(endpoint))
            {
                _values.Add(null);
            }
            else
            {
                _values.Add(decimal.Round((decimal)endpoint, 8, MidpointRounding.AwayFromZero));
            }
        }

        return IndicatorResult.Success(_values);
    }

    public override Task<IIndicatorResult> CalculateAsync(IReadOnlyList<CoreCandleData> candles, IExecutionContext context)
    {
        return Task.FromResult(Calculate(candles));
    }
}
