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
/// Singular Spectrum Analysis (SSA) Signal-to-Noise Ratio (SNR) Indicator.
/// Measures the spectral concentration of signal subspace eigenspectrum vs noise subspace eigenspectrum
/// in decibels (dB) with relative epsilon protection and bounded output [-20 dB, +40 dB],
/// along with percentage Signal Purity.
/// Pure C# implementation (Zero-Dependency, Zero-Allocation).
/// </summary>
[StockAnalyzerIndicator(IndicatorType.SSASNR)]
public class CoreSSASNRIndicator : CoreIndicatorBase
{
    public const double FloorEpsilon = 1e-12;
    public const double MinSnrDb = -20.0;
    public const double MaxSnrDb = 40.0;

    public int WindowSize { get; set; } = IndicatorDefaultConstants.SsaSnrDefaultWindowSize;
    public int EmbeddingDimension { get; set; } = IndicatorDefaultConstants.SsaSnrDefaultEmbeddingDimension;
    public int NumComponents { get; set; } = IndicatorDefaultConstants.SsaSnrDefaultNumComponents;
    public decimal ThresholdHigh { get; set; } = IndicatorDefaultConstants.SsaSnrDefaultThresholdHigh;
    public decimal ThresholdLow { get; set; } = IndicatorDefaultConstants.SsaSnrDefaultThresholdLow;

    public override string Name => $"SSA SNR ({WindowSize}, {EmbeddingDimension}, {NumComponents})";
    public override bool IsOverlay => false;

    // Series Names
    public const string SnrSeriesName = IndicatorResult.MainSeriesName;
    public const string SignalPuritySeriesName = "SignalPurity";
    public const string ThresholdHighScoreSeriesName = "ThresholdHigh";
    public const string ThresholdLowScoreSeriesName = "ThresholdLow";

    public IReadOnlyList<decimal?> SNR => _values;
    public List<decimal?> SignalPurity { get; } = new();
    public List<decimal?> ThresholdHighSeries { get; } = new();
    public List<decimal?> ThresholdLowSeries { get; } = new();

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSSASNRParameter p)
        {
            WindowSize = p.WindowSize;
            EmbeddingDimension = p.EmbeddingDimension;
            NumComponents = p.NumComponents;
            ThresholdHigh = p.ThresholdHigh;
            ThresholdLow = p.ThresholdLow;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        SignalPurity.Clear();
        ThresholdHighSeries.Clear();
        ThresholdLowSeries.Clear();

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
                SignalPurity.Add(null);
                ThresholdHighSeries.Add(null);
                ThresholdLowSeries.Add(null);
            }

            return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
            {
                { SnrSeriesName, _values },
                { "SNR_dB", _values },
                { SignalPuritySeriesName, SignalPurity },
                { ThresholdHighScoreSeriesName, ThresholdHighSeries },
                { ThresholdLowScoreSeriesName, ThresholdLowSeries }
            });
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
            SignalPurity.Add(null);
            ThresholdHighSeries.Add(null);
            ThresholdLowSeries.Add(null);
        }

        int l = Math.Clamp(EmbeddingDimension, 2, Math.Max(2, window / 2));
        int k = window - l + 1;
        int r = Math.Clamp(NumComponents, 1, Math.Min(l, k));

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

            for (int t = window - 1; t < n; t++)
            {
                ReadOnlySpan<double> windowSpan = doublePrices.AsSpan(t - window + 1, window);
                SsaDecompositionEngine.Detrend(windowSpan, processed, SsaDetrendMode.LeastSquaresLinear, out _, out _);

                SsaDecompositionEngine.BuildLagCovarianceMatrix(processed, l, k, sMatrix);
                SsaDecompositionEngine.ComputeJacobiEigensystem(sMatrix, l, eigenvalues, eigenvectors);

                // Sort eigenvalues descending
                Array.Sort(eigenvalues);
                Array.Reverse(eigenvalues);

                double sSignal = 0.0;
                for (int m = 0; m < r; m++)
                {
                    sSignal += Math.Max(0.0, eigenvalues[m]);
                }

                double sNoise = 0.0;
                for (int m = r; m < l; m++)
                {
                    sNoise += Math.Max(0.0, eigenvalues[m]);
                }

                double sTotal = sSignal + sNoise;

                double snrDb;
                double purity;

                if (sTotal <= FloorEpsilon)
                {
                    snrDb = 0.0;
                    purity = 0.0;
                }
                else
                {
                    double relEps = sTotal * 1e-12;
                    double effNoise = Math.Max(sNoise, relEps);
                    double effSignal = Math.Max(sSignal, relEps);
                    double ratio = effSignal / effNoise;
                    snrDb = Math.Clamp(10.0 * Math.Log10(ratio), MinSnrDb, MaxSnrDb);
                    purity = Math.Clamp((sSignal / sTotal) * 100.0, 0.0, 100.0);
                }

                _values.Add(decimal.Round((decimal)snrDb, 4, MidpointRounding.AwayFromZero));
                SignalPurity.Add(decimal.Round((decimal)purity, 4, MidpointRounding.AwayFromZero));
                ThresholdHighSeries.Add(ThresholdHigh);
                ThresholdLowSeries.Add(ThresholdLow);
            }
        }
        finally
        {
            if (pooledProcessed != null)
            {
                ArrayPool<double>.Shared.Return(pooledProcessed);
            }
        }

        return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
        {
            { SnrSeriesName, _values },
            { "SNR_dB", _values },
            { SignalPuritySeriesName, SignalPurity },
            { ThresholdHighScoreSeriesName, ThresholdHighSeries },
            { ThresholdLowScoreSeriesName, ThresholdLowSeries }
        });
    }

    public override Task<IIndicatorResult> CalculateAsync(IReadOnlyList<CoreCandleData> candles, IExecutionContext context)
    {
        return Task.FromResult(Calculate(candles));
    }
}
