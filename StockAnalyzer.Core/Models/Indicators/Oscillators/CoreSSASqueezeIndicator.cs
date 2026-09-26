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
/// Singular Spectrum Analysis (SSA) Squeeze and Breakout Indicator.
/// Measures volatility compression by comparing SSA Hankelization residual band width
/// against True Range exponential channel width (ATR), and computes causal linear regression
/// momentum from the extracted SSA trend center line.
/// Pure C# implementation (Zero-Dependency, Zero-Allocation).
/// </summary>
[StockAnalyzerIndicator(IndicatorType.SSASqueeze)]
public class CoreSSASqueezeIndicator : CoreIndicatorBase
{
    public const double FloorEpsilon = 1e-12;

    public int WindowSize { get; set; } = IndicatorDefaultConstants.SsaSqueezeDefaultWindowSize;
    public int EmbeddingDimension { get; set; } = IndicatorDefaultConstants.SsaSqueezeDefaultEmbeddingDimension;
    public int NumComponents { get; set; } = IndicatorDefaultConstants.SsaSqueezeDefaultNumComponents;
    public decimal SsaMultiplier { get; set; } = IndicatorDefaultConstants.SsaSqueezeDefaultSsaMultiplier;
    public int AtrPeriod { get; set; } = IndicatorDefaultConstants.SsaSqueezeDefaultAtrPeriod;
    public decimal AtrMultiplier { get; set; } = IndicatorDefaultConstants.SsaSqueezeDefaultAtrMultiplier;
    public int MomentumPeriod { get; set; } = IndicatorDefaultConstants.SsaSqueezeDefaultMomentumPeriod;
    public decimal SqueezeThreshold { get; set; } = IndicatorDefaultConstants.SsaSqueezeDefaultSqueezeThreshold;

    public override string Name => $"SSA Squeeze ({WindowSize}, {EmbeddingDimension}, {NumComponents}, {MomentumPeriod})";
    public override bool IsOverlay => false;

    // Series Names
    public const string MomentumSeriesName = IndicatorResult.MainSeriesName;
    public const string SqueezeStatusSeriesName = "SqueezeStatus";
    public const string SqueezeRatioSeriesName = "SqueezeRatio";

    public IReadOnlyList<decimal?> Momentum => _values;
    public List<decimal?> SqueezeStatus { get; } = new();
    public List<decimal?> SqueezeRatio { get; } = new();

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSSASqueezeParameter p)
        {
            WindowSize = p.WindowSize;
            EmbeddingDimension = p.EmbeddingDimension;
            NumComponents = p.NumComponents;
            SsaMultiplier = p.SsaMultiplier;
            AtrPeriod = p.AtrPeriod;
            AtrMultiplier = p.AtrMultiplier;
            MomentumPeriod = p.MomentumPeriod;
            SqueezeThreshold = p.SqueezeThreshold;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        SqueezeStatus.Clear();
        SqueezeRatio.Clear();

        if (candles == null || candles.Count == 0)
        {
            return IndicatorResult.Success(_values);
        }

        int n = candles.Count;
        int window = Math.Max(SsaDecompositionEngine.MinSampleCount, WindowSize);
        int atrPeriod = Math.Max(2, AtrPeriod);
        int momPeriod = Math.Max(2, MomentumPeriod);

        if (n < window)
        {
            for (int i = 0; i < n; i++)
            {
                _values.Add(null);
                SqueezeStatus.Add(null);
                SqueezeRatio.Add(null);
            }

            return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
            {
                { MomentumSeriesName, _values },
                { "Momentum", _values },
                { SqueezeStatusSeriesName, SqueezeStatus },
                { SqueezeRatioSeriesName, SqueezeRatio }
            });
        }

        // 1. Calculate True Range & Wilder ATR
        double[] atrValues = new double[n];
        double[] trValues = new double[n];
        for (int i = 0; i < n; i++)
        {
            if (i == 0)
            {
                trValues[i] = (double)(candles[i].High - candles[i].Low);
            }
            else
            {
                decimal tr = Math.Max(candles[i].High - candles[i].Low,
                             Math.Max(Math.Abs(candles[i].High - candles[i - 1].Close),
                                      Math.Abs(candles[i].Low - candles[i - 1].Close)));
                trValues[i] = (double)tr;
            }
        }

        double atrRunning = 0.0;
        for (int i = 0; i < n; i++)
        {
            if (i < atrPeriod)
            {
                atrRunning += trValues[i];
                if (i == atrPeriod - 1)
                {
                    atrValues[i] = atrRunning / atrPeriod;
                }
                else
                {
                    atrValues[i] = 0.0;
                }
            }
            else
            {
                atrValues[i] = (atrValues[i - 1] * (atrPeriod - 1) + trValues[i]) / atrPeriod;
            }
        }

        // 2. Extract Price Series
        var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
        double[] doublePrices = new double[n];
        for (int i = 0; i < n; i++)
        {
            doublePrices[i] = (double)(priceSeries[i] ?? 0m);
        }

        // 3. Compute Rolling SSA Center & Residual StdDev
        double[] ssaCenters = new double[n];
        double[] ssaSigmaRes = new double[n];
        bool[] ssaValid = new bool[n];

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

                // Causal endpoint
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

                // Residual standard deviation
                SsaDecompositionEngine.ReconstructGroup(processed, l, k, sortedIndices.AsSpan(0, r), eigenvectors, reconstructed);
                double sumSqErr = 0.0;
                for (int tau = 0; tau < window; tau++)
                {
                    double diff = processed[tau] - reconstructed[tau];
                    sumSqErr += diff * diff;
                }
                double sigmaRes = Math.Sqrt(Math.Max(0.0, sumSqErr / window));

                if (double.IsFinite(centerVal) && double.IsFinite(sigmaRes))
                {
                    ssaCenters[t] = centerVal;
                    ssaSigmaRes[t] = sigmaRes;
                    ssaValid[t] = true;
                }
            }
        }
        finally
        {
            if (pooledProcessed != null) ArrayPool<double>.Shared.Return(pooledProcessed);
            if (pooledReconstructed != null) ArrayPool<double>.Shared.Return(pooledReconstructed);
        }

        // 4. Calculate Squeeze Ratio, Squeeze Status, and Causal Momentum
        double ssaMult = (double)SsaMultiplier;
        double atrMult = (double)AtrMultiplier;
        double sqThresh = (double)SqueezeThreshold;

        // OLS Slope denominator for Momentum regression: K^2(K^2 - 1) / 12
        double momDenom = (double)momPeriod * (momPeriod * (double)momPeriod - 1.0) / 12.0;
        double momMeanI = (momPeriod - 1) * 0.5;

        for (int t = 0; t < n; t++)
        {
            // Squeeze calculation
            if (t < window - 1 || t < atrPeriod - 1 || !ssaValid[t])
            {
                SqueezeRatio.Add(null);
                SqueezeStatus.Add(null);
            }
            else
            {
                double bandWidthSsa = 2.0 * ssaMult * ssaSigmaRes[t];
                double closeScale = Math.Abs(doublePrices[t]) * 1e-6;
                double safeChannelWidthAtr = Math.Max(2.0 * atrMult * atrValues[t], Math.Max(1e-12, closeScale));

                double ratio = bandWidthSsa / safeChannelWidthAtr;
                decimal ratioDec = decimal.Round((decimal)ratio, 4, MidpointRounding.AwayFromZero);
                SqueezeRatio.Add(ratioDec);

                decimal statusDec = (ratio < sqThresh) ? 1.0m : 0.0m;
                SqueezeStatus.Add(statusDec);
            }

            // Momentum calculation: requires momPeriod consecutive valid SSA center values
            int startMom = t - momPeriod + 1;
            if (startMom < window - 1 || !ssaValid[t])
            {
                _values.Add(null);
            }
            else
            {
                bool allValid = true;
                for (int j = startMom; j <= t; j++)
                {
                    if (!ssaValid[j])
                    {
                        allValid = false;
                        break;
                    }
                }

                if (!allValid || momDenom <= FloorEpsilon)
                {
                    _values.Add(null);
                }
                else
                {
                    double sumY = 0.0;
                    double sumTiYi = 0.0;
                    for (int i = 0; i < momPeriod; i++)
                    {
                        double val = ssaCenters[startMom + i];
                        sumY += val;
                        sumTiYi += i * val;
                    }

                    double slope = (sumTiYi - momMeanI * sumY) / momDenom;
                    double momentumVal = slope * momPeriod;
                    _values.Add(decimal.Round((decimal)momentumVal, 6, MidpointRounding.AwayFromZero));
                }
            }
        }

        return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
        {
            { MomentumSeriesName, _values },
            { "Momentum", _values },
            { SqueezeStatusSeriesName, SqueezeStatus },
            { SqueezeRatioSeriesName, SqueezeRatio }
        });
    }

    public override Task<IIndicatorResult> CalculateAsync(IReadOnlyList<CoreCandleData> candles, IExecutionContext context)
    {
        return Task.FromResult(Calculate(candles));
    }
}
