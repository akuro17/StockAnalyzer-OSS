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

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        UpperBand.Clear();
        LowerBand.Clear();
        BandWidth.Clear();

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
                UpperBand.Add(null);
                LowerBand.Add(null);
                BandWidth.Add(null);
            }

            return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
            {
                { CenterSeriesName, _values },
                { UpperSeriesName, UpperBand },
                { LowerSeriesName, LowerBand }
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
            UpperBand.Add(null);
            LowerBand.Add(null);
            BandWidth.Add(null);
        }

        int l = Math.Clamp(EmbeddingDimension, 2, Math.Max(2, window / 2));
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

            for (int t = window - 1; t < n; t++)
            {
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
                    sigmaRes = Math.Sqrt(Math.Max(0.0, lambdaResidualSum / (window * k)));
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

                if (!double.IsFinite(centerVal) || !double.IsFinite(sigmaRes))
                {
                    _values.Add(null);
                    UpperBand.Add(null);
                    LowerBand.Add(null);
                    BandWidth.Add(null);
                }
                else
                {
                    decimal center = decimal.Round((decimal)centerVal, 8, MidpointRounding.AwayFromZero);
                    double mult = (double)Multiplier;
                    decimal upper = decimal.Round((decimal)(centerVal + mult * sigmaRes), 8, MidpointRounding.AwayFromZero);
                    decimal lower = decimal.Round((decimal)(centerVal - mult * sigmaRes), 8, MidpointRounding.AwayFromZero);

                    decimal? bw = (Math.Abs(centerVal) <= FloorEpsilon)
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

        return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
        {
            { CenterSeriesName, _values },
            { UpperSeriesName, UpperBand },
            { LowerSeriesName, LowerBand }
        });
    }
}
