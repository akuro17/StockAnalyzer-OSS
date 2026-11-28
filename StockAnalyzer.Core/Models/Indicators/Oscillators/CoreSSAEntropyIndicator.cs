using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.MathUtils;
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

        int l = SsaDecompositionEngine.ClampEmbeddingDimension(EmbeddingDimension, window);
        int k = window - l + 1;

        double[] doublePrices = new double[n];
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

                if (l < 2 || k < 2)
                {
                    _values.Add(0.0m);
                    continue;
                }

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

                double entropy = ShannonEntropyMath.Nats(eigenvalues, sumEigenvalues, ShannonEntropyMath.NegligibleShare);

                double normEntropy = (lnL > 1e-12) ? Math.Clamp(entropy / lnL, 0.0, 1.0) : 0.0;
                decimal? decEntropy = IndicatorCalculationHelper.ToDecimalOrNull(normEntropy);
                _values.Add(decEntropy.HasValue
                    ? decimal.Round(decEntropy.Value, 4, MidpointRounding.AwayFromZero)
                    : null);
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
