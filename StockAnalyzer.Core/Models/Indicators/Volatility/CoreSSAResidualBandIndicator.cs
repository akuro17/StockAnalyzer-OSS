using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility;

/// <summary>
/// Singular Spectrum Analysis (SSA) Residual Volatility Band Indicator.
/// Decomposes price series into principal trend components via rolling lag-covariance eigensystem (Jacobi method),
/// computes the causal endpoint as the center line, and constructs adaptive volatility bands using exact diagonal
/// averaging (Hankelization) residual standard deviation.
/// Pure C# implementation (Zero-Dependency, Zero-Allocation).
/// </summary>
[StockAnalyzerIndicator(IndicatorType.SSAResidualBand)]
public class CoreSSAResidualBandIndicator : CoreIndicatorBase
{
    public const double FloorEpsilon = 1e-12;
    private const decimal FloorEpsilonDecimal = 1e-12m;

    public int WindowSize { get; set; } = IndicatorDefaultConstants.SsaResidualBandDefaultWindowSize;
    public int EmbeddingDimension { get; set; } = IndicatorDefaultConstants.SsaResidualBandDefaultEmbeddingDimension;
    public int NumComponents { get; set; } = IndicatorDefaultConstants.SsaResidualBandDefaultNumComponents;
    public decimal Multiplier { get; set; } = IndicatorDefaultConstants.SsaResidualBandDefaultMultiplier;
    public SsaResidualBandSigmaMode SigmaMode { get; set; } = SsaResidualBandSigmaMode.ExactDiagonalAverage;

    public override string Name => $"SSA Residual Band ({WindowSize}, {EmbeddingDimension}, {NumComponents}, {Multiplier:F1})";
    public override bool IsOverlay => true;

    // Series Names
    public const string CenterSeriesName = IndicatorResult.MainSeriesName;
    public const string UpperSeriesName = "Upper";
    public const string LowerSeriesName = "Lower";
    public const string BandWidthSeriesName = "BandWidth";

    public IReadOnlyList<decimal?> CenterBand => _values;
    public List<decimal?> UpperBand { get; } = new();
    public List<decimal?> LowerBand { get; } = new();
    public List<decimal?> BandWidth { get; } = new();

    public IReadOnlyList<decimal?> Upper => UpperBand;
    public IReadOnlyList<decimal?> Lower => LowerBand;
    public IReadOnlyList<decimal?> Center => CenterBand;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSSAResidualBandParameter p)
        {
            WindowSize = p.WindowSize;
            EmbeddingDimension = p.EmbeddingDimension;
            NumComponents = p.NumComponents;
            Multiplier = p.Multiplier;
            SigmaMode = p.SigmaMode;
        }
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        _values.Clear();
        UpperBand.Clear();
        LowerBand.Clear();
        BandWidth.Clear();

        if (series == null || series.Count == 0)
        {
            return CreateAutomaticResult();
        }

        int n = series.Count;
        int window = Math.Max(SsaDecompositionEngine.MinSampleCount, WindowSize);
        if (n < window)
        {
            for (int i = 0; i < n; i++)
            {
                _values.Add(null);
                UpperBand.Add(null);
                LowerBand.Add(null);
                BandWidth.Add(null);
            }

            return CreateAutomaticResult();
        }

        double[] doublePrices = new double[n];
        int l = SsaDecompositionEngine.ClampEmbeddingDimension(EmbeddingDimension, window);
        int k = window - l + 1;
        int r = Math.Clamp(NumComponents, 1, Math.Min(l, k));

        double[]? pooledProcessed = null;
        double[]? pooledReconstructed = null;
        Span<double> processed = (window <= 256) ? stackalloc double[window] : (pooledProcessed = ArrayPool<double>.Shared.Rent(window)).AsSpan(0, window);
        Span<double> reconstructed = (window <= 256) ? stackalloc double[window] : (pooledReconstructed = ArrayPool<double>.Shared.Rent(window)).AsSpan(0, window);

        try
        {
            double[,] sMatrix = new double[l, l];
            double[] eigenvalues = new double[l];
            double[,] eigenvectors = new double[l, l];
            int[] sortedIndices = new int[l];
            int contiguousValid = 0;

            for (int t = 0; t < n; t++)
            {
                if (!series[t].HasValue)
                {
                    contiguousValid = 0;
                    _values.Add(null);
                    UpperBand.Add(null);
                    LowerBand.Add(null);
                    BandWidth.Add(null);
                    continue;
                }

                double val = (double)series[t]!.Value;
                if (!double.IsFinite(val))
                {
                    contiguousValid = 0;
                    _values.Add(null);
                    UpperBand.Add(null);
                    LowerBand.Add(null);
                    BandWidth.Add(null);
                    continue;
                }

                doublePrices[t] = val;
                contiguousValid++;

                if (contiguousValid < window)
                {
                    _values.Add(null);
                    UpperBand.Add(null);
                    LowerBand.Add(null);
                    BandWidth.Add(null);
                    continue;
                }

                ReadOnlySpan<double> windowSpan = doublePrices.AsSpan(t - window + 1, window);
                SsaDecompositionEngine.Detrend(windowSpan, processed, SsaDetrendMode.LeastSquaresLinear, out double slope, out double intercept);

                SsaDecompositionEngine.BuildLagCovarianceMatrix(processed, l, k, sMatrix);
                SsaDecompositionEngine.ComputeJacobiEigensystem(sMatrix, l, eigenvalues, eigenvectors);

                for (int i = 0; i < l; i++) sortedIndices[i] = i;
                Array.Sort(sortedIndices, (a, b) => eigenvalues[b].CompareTo(eigenvalues[a]));

                // 1. Center value: causal endpoint at t = window - 1
                double endpointDetrended = 0.0;
                for (int m = 0; m < r; m++)
                {
                    int eigIdx = sortedIndices[m];
                    double uLast = eigenvectors[l - 1, eigIdx];
                    double vLast = 0.0;
                    for (int row = 0; row < l; row++)
                    {
                        vLast += processed[row + k - 1] * eigenvectors[row, eigIdx];
                    }
                    endpointDetrended += uLast * vLast;
                }
                double centerVal = endpointDetrended + (intercept + slope * (window - 1));

                // 2. Residual standard deviation computation
                double sigmaRes;
                if (SigmaMode == SsaResidualBandSigmaMode.FastEigenEnergy)
                {
                    double lambdaResidualSum = 0.0;
                    for (int m = r; m < l; m++)
                    {
                        lambdaResidualSum += eigenvalues[sortedIndices[m]];
                    }
                    sigmaRes = Math.Sqrt(Math.Max(0.0, lambdaResidualSum / (l * k)));
                }
                else
                {
                    // Exact diagonal averaging reconstruction on window to compute sigma_res
                    SsaDecompositionEngine.ReconstructGroup(processed, l, k, sortedIndices.AsSpan(0, r), eigenvectors, reconstructed);

                    double sumSqErr = 0.0;
                    for (int tau = 0; tau < window; tau++)
                    {
                        double diff = processed[tau] - reconstructed[tau];
                        sumSqErr += diff * diff;
                    }
                    sigmaRes = Math.Sqrt(Math.Max(0.0, sumSqErr / window));
                }

                double mult = (double)Multiplier;
                decimal? centerDec = IndicatorCalculationHelper.ToDecimalOrNull(centerVal);
                decimal? upperDec = IndicatorCalculationHelper.ToDecimalOrNull(centerVal + mult * sigmaRes);
                decimal? lowerDec = IndicatorCalculationHelper.ToDecimalOrNull(centerVal - mult * sigmaRes);

                if (!centerDec.HasValue || !upperDec.HasValue || !lowerDec.HasValue || !double.IsFinite(sigmaRes))
                {
                    _values.Add(null);
                    UpperBand.Add(null);
                    LowerBand.Add(null);
                    BandWidth.Add(null);
                }
                else
                {
                    decimal center = decimal.Round(centerDec.Value, 8, MidpointRounding.AwayFromZero);
                    decimal upper = decimal.Round(upperDec.Value, 8, MidpointRounding.AwayFromZero);
                    decimal lower = decimal.Round(lowerDec.Value, 8, MidpointRounding.AwayFromZero);

                    decimal? bw = (Math.Abs(center) <= FloorEpsilonDecimal)
                        ? null
                        : decimal.Round(((upper - lower) / Math.Abs(center)) * 100m, 8, MidpointRounding.AwayFromZero);

                    _values.Add(center);
                    UpperBand.Add(upper);
                    LowerBand.Add(lower);
                    BandWidth.Add(bw);
                }
            }
        }
        finally
        {
            if (pooledProcessed != null) ArrayPool<double>.Shared.Return(pooledProcessed);
            if (pooledReconstructed != null) ArrayPool<double>.Shared.Return(pooledReconstructed);
        }

        return CreateAutomaticResult();
    }
}
