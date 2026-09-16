using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators;

/// <summary>
/// Singular Spectrum Analysis (SSA) Spectral Concentration / Eigenvalue Entropy Indicator.
/// Evaluates the normalized Shannon entropy of the trajectory covariance eigenspectrum:
/// H(p) = -sum(p_m * ln(p_m)) / ln(L), where p_m = lambda_m / sum(lambda).
/// Values near 0.0 indicate high energy concentration in few components (strong deterministic trend / harmonic cycle),
/// while values near 1.0 indicate evenly dispersed noise / random walk.
/// Pure C# implementation (Zero-Dependency, Zero-Allocation).
/// </summary>
[StockAnalyzerIndicator(IndicatorType.SSAEntropy)]
public class CoreSSAEntropyIndicator : CoreIndicatorBase
{
    public int WindowSize { get; set; } = IndicatorDefaultConstants.SsaEntropyDefaultWindowSize;
    public int EmbeddingDimension { get; set; } = IndicatorDefaultConstants.SsaEntropyDefaultEmbeddingDimension;
    public SsaDetrendMode DetrendMode { get; set; } = SsaDetrendMode.LeastSquaresLinear;

    public override string Name => $"SSA Entropy ({WindowSize}, {EmbeddingDimension})";
    public override bool IsOverlay => false;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSSAEntropyParameter p)
        {
            WindowSize = p.WindowSize;
            EmbeddingDimension = p.EmbeddingDimension;
            DetrendMode = p.DetrendMode;
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

        int l = Math.Clamp(EmbeddingDimension, 2, Math.Max(2, window / 2));
        int k = window - l + 1;
        if (l < 2 || k < 2)
        {
            _values.AddRange(Enumerable.Repeat<decimal?>(null, window - 1));
            _values.AddRange(Enumerable.Repeat<decimal?>(0.0m, n - (window - 1)));
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

        double lnL = Math.Log(l);
        double[,] sMatrix = new double[l, l];
        double[] eigenvalues = new double[l];
        double[,] eigenvectors = new double[l, l];

        Span<double> stackProcessed = stackalloc double[Math.Min(window, 512)];
        double[]? pooledProcessed = null;
        if (window > 512)
        {
            pooledProcessed = ArrayPool<double>.Shared.Rent(window);
        }

        try
        {
            Span<double> processed = pooledProcessed != null
                ? pooledProcessed.AsSpan(0, window)
                : stackProcessed.Slice(0, window);

            for (int i = window - 1; i < n; i++)
            {
                ReadOnlySpan<double> windowSpan = doublePrices.AsSpan(i - window + 1, window);
                SsaDecompositionEngine.Detrend(windowSpan, processed, DetrendMode, out _, out _);

                SsaDecompositionEngine.BuildLagCovarianceMatrix(processed, l, k, sMatrix);
                SsaDecompositionEngine.ComputeJacobiEigensystem(sMatrix, l, eigenvalues, eigenvectors);

                double sumEigenvalues = 0.0;
                for (int j = 0; j < l; j++)
                {
                    sumEigenvalues += eigenvalues[j];
                }

                if (sumEigenvalues <= SsaDecompositionEngine.JacobiConvergenceTolerance)
                {
                    _values.Add(0.0m);
                    continue;
                }

                double entropy = 0.0;
                for (int j = 0; j < l; j++)
                {
                    double p = eigenvalues[j] / sumEigenvalues;
                    if (p > 1e-12)
                    {
                        entropy -= p * Math.Log(p);
                    }
                }

                double normEntropy = (lnL > 1e-12) ? Math.Clamp(entropy / lnL, 0.0, 1.0) : 0.0;
                _values.Add(decimal.Round((decimal)normEntropy, 4, MidpointRounding.AwayFromZero));
            }
        }
        finally
        {
            if (pooledProcessed != null)
            {
                ArrayPool<double>.Shared.Return(pooledProcessed);
            }
        }

        return IndicatorResult.Success(_values);
    }

    public override Task<IIndicatorResult> CalculateAsync(IReadOnlyList<CoreCandleData> candles, IExecutionContext context)
    {
        return Task.FromResult(Calculate(candles));
    }
}
