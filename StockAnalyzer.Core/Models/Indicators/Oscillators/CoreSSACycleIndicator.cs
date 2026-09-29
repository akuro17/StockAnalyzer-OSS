using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators;

/// <summary>
/// Singular Spectrum Analysis (SSA) Dominant Cycle Extractor.
/// Identifies quasi-degenerate orthogonal quadrature eigenvector pairs in the trajectory lag-covariance eigenspace,
/// estimates the dominant cycle period via 1D discrete Fourier power peak search, and extracts instant in-phase (I),
/// quadrature (Q), cycle oscillation, and phase angle without future repainting.
/// Pure C# implementation (Zero-Dependency, Zero-Allocation).
/// </summary>
[StockAnalyzerIndicator(IndicatorType.SSACycle)]
public class CoreSSACycleIndicator : CoreIndicatorBase
{
    public const double EigenvalueEpsilon = 1e-12;
    public const double QuadraturePhaseThreshold = 0.05;

    public int WindowSize { get; set; } = IndicatorDefaultConstants.SsaCycleDefaultWindowSize;
    public int EmbeddingDimension { get; set; } = IndicatorDefaultConstants.SsaCycleDefaultEmbeddingDimension;
    public double DeltaPair { get; set; } = IndicatorDefaultConstants.SsaCycleDefaultDeltaPair;

    public override string Name => $"SSA Cycle ({WindowSize}, {EmbeddingDimension}, {DeltaPair:F2})";
    public override bool IsOverlay => false;

    // Series Names
    public const string CycleSeriesName = IndicatorResult.MainSeriesName;
    public const string InPhaseSeriesName = "InPhase";
    public const string QuadratureSeriesName = "Quadrature";
    public const string PhaseSeriesName = "Phase";
    public const string DominantPeriodSeriesName = "DominantPeriod";

    public IReadOnlyList<decimal?> Cycle => _values;
    public List<decimal?> InPhase { get; } = new();
    public List<decimal?> Quadrature { get; } = new();
    public List<decimal?> Phase { get; } = new();
    public List<decimal?> DominantPeriod { get; } = new();

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSSACycleParameter p)
        {
            WindowSize = p.WindowSize;
            EmbeddingDimension = p.EmbeddingDimension;
            DeltaPair = p.DeltaPair;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        InPhase.Clear();
        Quadrature.Clear();
        Phase.Clear();
        DominantPeriod.Clear();

        if (candles == null || candles.Count == 0)
        {
            return IndicatorResult.Success(_values);
        }

        int n = candles.Count;
        int window = Math.Max(SsaDecompositionEngine.MinSampleCount, WindowSize);
        if (n < window)
        {
            for (int i = 0; i < n; i++)
            {
                _values.Add(null);
                InPhase.Add(null);
                Quadrature.Add(null);
                Phase.Add(null);
                DominantPeriod.Add(null);
            }

            return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
            {
                { CycleSeriesName, _values },
                { "Cycle", _values },
                { InPhaseSeriesName, InPhase },
                { QuadratureSeriesName, Quadrature },
                { PhaseSeriesName, Phase },
                { DominantPeriodSeriesName, DominantPeriod }
            });
        }

        var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
        double[] doublePrices = new double[n];
        for (int i = 0; i < n; i++)
        {
            doublePrices[i] = (double)(priceSeries[i] ?? 0m);
        }

        // Pre-fill nulls for warmup bars
        for (int i = 0; i < window - 1; i++)
        {
            _values.Add(null);
            InPhase.Add(null);
            Quadrature.Add(null);
            Phase.Add(null);
            DominantPeriod.Add(null);
        }

        int l = Math.Clamp(EmbeddingDimension, 2, Math.Max(2, window / 2));
        int k = window - l + 1;

        double[]? pooledProcessed = null;
        Span<double> processed = (window <= 256) ? stackalloc double[window] : (pooledProcessed = ArrayPool<double>.Shared.Rent(window)).AsSpan(0, window);

        try
        {
            double[,] sMatrix = new double[l, l];
            double[] eigenvalues = new double[l];
            double[,] eigenvectors = new double[l, l];
            int[] sortedIndices = new int[l];

            for (int t = window - 1; t < n; t++)
            {
                ReadOnlySpan<double> windowSpan = doublePrices.AsSpan(t - window + 1, window);
                SsaDecompositionEngine.Detrend(windowSpan, processed, SsaDetrendMode.LeastSquaresLinear, out _, out _);

                SsaDecompositionEngine.BuildLagCovarianceMatrix(processed, l, k, sMatrix);
                SsaDecompositionEngine.ComputeJacobiEigensystem(sMatrix, l, eigenvalues, eigenvectors);

                for (int i = 0; i < l; i++) sortedIndices[i] = i;
                Array.Sort(sortedIndices, (a, b) => eigenvalues[b].CompareTo(eigenvalues[a]));

                // Search for dominant harmonic quadrature pair (m*, m*+1)
                int bestM = -1;
                double maxPairEnergy = -1.0;

                for (int m = 0; m < l - 1; m++)
                {
                    int idx1 = sortedIndices[m];
                    int idx2 = sortedIndices[m + 1];

                    double lambda1 = eigenvalues[idx1];
                    double lambda2 = eigenvalues[idx2];

                    if (lambda1 <= EigenvalueEpsilon || lambda2 <= EigenvalueEpsilon)
                        continue;

                    // 1. Degeneracy ratio check
                    double ratio = lambda2 / lambda1;
                    if (ratio < 1.0 - DeltaPair)
                        continue;

                    // 2. Lag-1 cross-difference inner product for quadrature phase relation
                    double psiSum = 0.0;
                    for (int j = 0; j < l - 1; j++)
                    {
                        double u1_j = eigenvectors[j, idx1];
                        double u1_next = eigenvectors[j + 1, idx1];
                        double u2_j = eigenvectors[j, idx2];
                        double u2_next = eigenvectors[j + 1, idx2];

                        psiSum += (u1_j * u2_next - u2_j * u1_next);
                    }
                    double psi = psiSum;

                    if (Math.Abs(psi) >= QuadraturePhaseThreshold)
                    {
                        double pairEnergy = lambda1 + lambda2;
                        if (pairEnergy > maxPairEnergy)
                        {
                            maxPairEnergy = pairEnergy;
                            bestM = m;
                        }
                    }
                }

                if (bestM < 0)
                {
                    _values.Add(null);
                    InPhase.Add(null);
                    Quadrature.Add(null);
                    Phase.Add(null);
                    DominantPeriod.Add(null);
                    continue;
                }

                int eig1 = sortedIndices[bestM];
                int eig2 = sortedIndices[bestM + 1];

                // Discrete 1D DFT peak search with sub-bin log-parabolic interpolation on leading eigenvector eig1
                int maxK = l / 2;
                Span<double> dftPowers = (maxK + 1 <= 128) ? stackalloc double[maxK + 1] : new double[maxK + 1];

                // Compute DC bin k=0 for sub-bin interpolation when peak is at k=1
                double dcSum = 0.0;
                for (int j = 0; j < l; j++) dcSum += eigenvectors[j, eig1];
                dftPowers[0] = dcSum * dcSum;

                int bestK = 1;
                double maxDftEnergy = -1.0;

                for (int binK = 1; binK <= maxK; binK++)
                {
                    double omega = 2.0 * Math.PI * binK / l;
                    double cosSum = 0.0;
                    double sinSum = 0.0;

                    for (int j = 0; j < l; j++)
                    {
                        double uVal = eigenvectors[j, eig1];
                        cosSum += uVal * Math.Cos(omega * j);
                        sinSum += uVal * Math.Sin(omega * j);
                    }

                    double dftEnergy = cosSum * cosSum + sinSum * sinSum;
                    dftPowers[binK] = dftEnergy;
                    if (dftEnergy > maxDftEnergy)
                    {
                        maxDftEnergy = dftEnergy;
                        bestK = binK;
                    }
                }

                // Sub-bin 3-point parabolic peak interpolation (supports 1 <= bestK < maxK)
                double kContinuous = bestK;
                if (bestK >= 1 && bestK < maxK && maxDftEnergy > 1e-12)
                {
                    double pPrev = dftPowers[bestK - 1];
                    double pCurr = dftPowers[bestK];
                    double pNext = dftPowers[bestK + 1];

                    double denomP = 2.0 * (2.0 * pCurr - pPrev - pNext);
                    if (denomP > 1e-20)
                    {
                        double deltaK = Math.Clamp((pNext - pPrev) / denomP, -0.5, 0.5);
                        kContinuous = Math.Max(0.5, bestK + deltaK);
                    }
                }

                double dominantPeriodVal = (kContinuous > 0.0) ? ((double)l / kContinuous) : (double)l;

                // Causal endpoint factor dot products
                double q1 = 0.0;
                double q2 = 0.0;
                for (int row = 0; row < l; row++)
                {
                    double pVal = processed[row + k - 1];
                    q1 += pVal * eigenvectors[row, eig1];
                    q2 += pVal * eigenvectors[row, eig2];
                }

                double iVal = eigenvectors[l - 1, eig1] * q1;
                double qVal = eigenvectors[l - 1, eig2] * q2;
                double cycleVal = iVal + qVal;
                double ampSq = iVal * iVal + qVal * qVal;
                double? phaseVal = (ampSq <= 1e-24) ? null : Math.Atan2(qVal, iVal);

                if (!double.IsFinite(cycleVal) || !double.IsFinite(dominantPeriodVal))
                {
                    _values.Add(null);
                    InPhase.Add(null);
                    Quadrature.Add(null);
                    Phase.Add(null);
                    DominantPeriod.Add(null);
                }
                else
                {
                    _values.Add(decimal.Round((decimal)cycleVal, 8, MidpointRounding.AwayFromZero));
                    InPhase.Add(decimal.Round((decimal)iVal, 8, MidpointRounding.AwayFromZero));
                    Quadrature.Add(decimal.Round((decimal)qVal, 8, MidpointRounding.AwayFromZero));
                    Phase.Add(phaseVal.HasValue && double.IsFinite(phaseVal.Value)
                        ? decimal.Round((decimal)phaseVal.Value, 8, MidpointRounding.AwayFromZero)
                        : null);
                    DominantPeriod.Add(decimal.Round((decimal)dominantPeriodVal, 8, MidpointRounding.AwayFromZero));
                }
            }
        }
        finally
        {
            if (pooledProcessed != null) ArrayPool<double>.Shared.Return(pooledProcessed);
        }

        return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
        {
            { CycleSeriesName, _values },
            { "Cycle", _values },
            { InPhaseSeriesName, InPhase },
            { QuadratureSeriesName, Quadrature },
            { PhaseSeriesName, Phase },
            { DominantPeriodSeriesName, DominantPeriod }
        });
    }
}
