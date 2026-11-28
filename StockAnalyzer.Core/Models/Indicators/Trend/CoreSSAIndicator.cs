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
    public bool AutoRank { get; set; }
    public SsaRankSelectionMethod RankMethod { get; set; } = SsaRankSelectionMethod.CumulativeEnergy;
    public override string Name => $"SSA ({WindowSize}, {EmbeddingDimension}, {NumComponents})";
    public override bool IsOverlay => true;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSSAParameter p)
        {
            WindowSize = p.WindowSize;
            EmbeddingDimension = p.EmbeddingDimension;
            NumComponents = p.NumComponents;
            AutoRank = p.AutoRank;
            RankMethod = p.RankMethod;
        }
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        _values.Clear();

        if (series == null || series.Count == 0)
        {
            return IndicatorResult.Success(_values);
        }

        int n = series.Count;
        int window = Math.Max(SsaDecompositionEngine.MinSampleCount, WindowSize);
        if (n < window)
        {
            _values.AddRange(Enumerable.Repeat<decimal?>(null, n));
            return IndicatorResult.Success(_values);
        }

        double[] doublePrices = new double[n];
        int contiguousValid = 0;

        for (int i = 0; i < n; i++)
        {
            if (!series[i].HasValue)
            {
                contiguousValid = 0;
                _values.Add(null);
                continue;
            }

            double val = (double)series[i]!.Value;
            if (!double.IsFinite(val))
            {
                contiguousValid = 0;
                _values.Add(null);
                continue;
            }

            doublePrices[i] = val;
            contiguousValid++;

            if (contiguousValid < window)
            {
                _values.Add(null);
                continue;
            }

            ReadOnlySpan<double> windowSpan = doublePrices.AsSpan(i - window + 1, window);
            double endpoint = AutoRank
                ? ComputeAutoRankEndpoint(windowSpan)
                : SsaDecompositionEngine.ComputeCausalEndpoint(
                    windowSpan,
                    EmbeddingDimension,
                    NumComponents,
                    SsaDetrendMode.LeastSquaresLinear);

            decimal? decEndpoint = IndicatorCalculationHelper.ToDecimalOrNull(endpoint);
            if (!decEndpoint.HasValue)
            {
                _values.Add(null);
            }
            else
            {
                _values.Add(decimal.Round(decEndpoint.Value, 8, MidpointRounding.AwayFromZero));
            }
        }

        return IndicatorResult.Success(_values);
    }

    /// <summary>
    /// Causal endpoint with the signal rank estimated from the window's own spectrum (same Decompose + EstimateSignalRank + decomposition-overload
    /// pattern as <c>CoreSSAAnomalyIndicator</c>). The rank only uses the bars inside the window, so it stays causal.
    /// </summary>
    private double ComputeAutoRankEndpoint(ReadOnlySpan<double> window)
    {
        int l = SsaDecompositionEngine.ClampEmbeddingDimension(EmbeddingDimension, window.Length);
        int k = window.Length - l + 1;
        var decomp = SsaDecompositionEngine.Decompose(window, l, SsaDetrendMode.LeastSquaresLinear);
        if (decomp.SortedIndices.Length < l || k < 2)
        {
            return double.NaN;
        }

        int r = SsaRankSelector.EstimateSignalRank(decomp, RankMethod, Math.Min(l - 1, k));
        return SsaDecompositionEngine.ComputeCausalEndpoint(window, decomp, l, r, SsaDetrendMode.LeastSquaresLinear);
    }

    public override Task<IIndicatorResult> CalculateAsync(IReadOnlyList<CoreCandleData> candles, IExecutionContext context)
    {
        return Task.FromResult(Calculate(candles));
    }
}
