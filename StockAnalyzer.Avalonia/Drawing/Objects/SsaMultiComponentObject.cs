using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Drawing.Objects;

public class SsaMultiComponentObject : IChartObject, IDrawingCalculatedValuesProvider
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.SsaMultiComponent;

    // Points[0] = Start Point (Time/Price) of selection
    // Points[1] = End Point (Time/Price) of selection
    public List<ChartPoint> Points { get; } = new(2);

    // Core visual properties
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int ZIndex { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;

    public int FillOpacity { get; set; } = 10;
    public Color FillColor { get; set; } = DrawingThemeContext.DefaultColor;

    // SSA Parameters
    public int EmbeddingDimension { get; set; } = 20;
    public int NumComponents { get; set; } = 2;
    public SsaDetrendMode DetrendMethod { get; set; } = SsaDetrendMode.LeastSquaresLinear;
    public PriceType PriceSource { get; set; } = PriceType.Median;

    // Layer Visibility
    public bool ShowTrendLayer { get; set; } = true;
    public bool ShowPrimaryCycleLayer { get; set; } = true;
    public bool ShowCompositeLayer { get; set; } = true;
    public bool ShowNoiseBand { get; set; } = true;

    // Layer Colors
    public Color TrendColor { get; set; } = Color.FromRgb(41, 98, 255);       // Blue #2962FF
    public Color PrimaryCycleColor { get; set; } = Color.FromRgb(171, 71, 188); // Purple/Violet #AB47BC
    public Color CompositeColor { get; set; } = Color.FromRgb(255, 152, 0);    // Orange #FF9800
    public Color NoiseBandColor { get; set; } = Color.FromRgb(128, 128, 128);  // Gray #808080
    public decimal NoiseMultiplier { get; set; } = 2.0m;

    public SkiaSharp.SKColor SkiaColor => new(Color.R, Color.G, Color.B, Color.A);
    public SkiaSharp.SKColor SkiaFillColor => new(FillColor.R, FillColor.G, FillColor.B, FillColor.A);

    // Layer decomposed path data: Point.X = timestamp (ticks), Point.Y = price
    public List<StockAnalyzer.Core.Models.Point> TrendPath { get; set; } = new();
    public List<StockAnalyzer.Core.Models.Point> PrimaryCyclePath { get; set; } = new();
    public List<StockAnalyzer.Core.Models.Point> CompositePath { get; set; } = new();
    public List<StockAnalyzer.Core.Models.Point> UpperNoiseBandPath { get; set; } = new();
    public List<StockAnalyzer.Core.Models.Point> LowerNoiseBandPath { get; set; } = new();

    // SSA Decomposition Diagnostics
    public double ResidualStdDev { get; set; }
    public double CumulativeVarianceRatio { get; set; }
    public int EffectiveRank { get; set; }
    public double DominantPeriod { get; set; }
    public double SnrDb { get; set; }
    public double SignalPurity { get; set; }
    public double SeparabilityScore { get; set; } = 100.0;
    public int SampleCount { get; set; }

    private readonly StockAnalyzer.Avalonia.Drawing.Renderers.SsaMultiComponentRenderer _renderer = new();

    public SsaMultiComponentObject()
    {
    }

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        _renderer.Render(canvas, this, transform, IsSelected);
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 2) return false;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        double minX = Math.Min(p1.X, p2.X) - tolerance;
        double maxX = Math.Max(p1.X, p2.X) + tolerance;

        return screenPoint.X >= minX && screenPoint.X <= maxX;
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(
                Points[i].Time + timeDelta,
                Points[i].Price + priceDelta
            );
        }

        ShiftPath(TrendPath, timeDelta, priceDelta);
        ShiftPath(PrimaryCyclePath, timeDelta, priceDelta);
        ShiftPath(CompositePath, timeDelta, priceDelta);
        ShiftPath(UpperNoiseBandPath, timeDelta, priceDelta);
        ShiftPath(LowerNoiseBandPath, timeDelta, priceDelta);
    }

    private static void ShiftPath(List<StockAnalyzer.Core.Models.Point>? path, TimeSpan timeDelta, decimal priceDelta)
    {
        if (path == null || path.Count == 0) return;
        for (int i = 0; i < path.Count; i++)
        {
            var p = path[i];
            path[i] = new StockAnalyzer.Core.Models.Point(p.X + timeDelta.Ticks, p.Y + (double)priceDelta);
        }
    }

    public void ClearPaths()
    {
        TrendPath.Clear();
        PrimaryCyclePath.Clear();
        CompositePath.Clear();
        UpperNoiseBandPath.Clear();
        LowerNoiseBandPath.Clear();
        ResidualStdDev = 0.0;
        CumulativeVarianceRatio = 0.0;
        EffectiveRank = 0;
        DominantPeriod = 0.0;
        SnrDb = 0.0;
        SignalPurity = 0.0;
        SeparabilityScore = 100.0;
        SampleCount = 0;
        _renderer.InvalidateCache();
    }

    public void Recalculate(IReadOnlyList<CoreCandleData>? candles, TimeSpan timeframeSpan = default)
    {
        if (candles == null || candles.Count == 0 || Points.Count < 2)
        {
            ClearPaths();
            return;
        }

        var t1 = Points[0].Time;
        var t2 = Points[1].Time;
        var startTime = t1 < t2 ? t1 : t2;
        var endTime = t1 > t2 ? t1 : t2;

        int startIndex = -1;
        int endIndex = -1;

        for (int i = 0; i < candles.Count; i++)
        {
            if (startIndex == -1 && candles[i].Timestamp >= startTime)
            {
                startIndex = i;
            }
            if (candles[i].Timestamp <= endTime)
            {
                endIndex = i;
            }
        }

        if (startIndex < 0 || endIndex < 0 || startIndex > endIndex)
        {
            ClearPaths();
            return;
        }

        int count = endIndex - startIndex + 1;
        if (count < SsaDecompositionEngine.MinSampleCount)
        {
            ClearPaths();
            return;
        }

        SampleCount = count;

        // Extract slice prices
        double[] doublePrices = new double[count];
        long[] timestamps = new long[count];

        for (int i = 0; i < count; i++)
        {
            var c = candles[startIndex + i];
            timestamps[i] = c.Timestamp.Ticks;
            decimal price = PriceSource switch
            {
                PriceType.Open => c.Open,
                PriceType.High => c.High,
                PriceType.Low => c.Low,
                PriceType.Close => c.Close,
                PriceType.Median => (c.High + c.Low) * 0.5m,
                PriceType.Typical => (c.High + c.Low + c.Close) / 3m,
                PriceType.Weighted => (c.High + c.Low + 2m * c.Close) * 0.25m,
                _ => c.Close
            };
            doublePrices[i] = (double)price;
        }

        int l = Math.Clamp(EmbeddingDimension, 2, Math.Max(2, count / 2));
        int k = count - l + 1;
        int r = Math.Clamp(NumComponents, 1, Math.Min(l, k));

        double[]? pooledProcessed = null;
        Span<double> processed = (count <= 512) ? stackalloc double[count] : (pooledProcessed = ArrayPool<double>.Shared.Rent(count)).AsSpan(0, count);

        try
        {
            SsaDecompositionEngine.Detrend(doublePrices.AsSpan(0, count), processed, DetrendMethod, out double slope, out double intercept);

            double[,] sMatrix = new double[l, l];
            SsaDecompositionEngine.BuildLagCovarianceMatrix(processed, l, k, sMatrix);

            double[] eigenvalues = new double[l];
            double[,] eigenvectors = new double[l, l];
            SsaDecompositionEngine.ComputeJacobiEigensystem(sMatrix, l, eigenvalues, eigenvectors);

            int[] sortedIndices = Enumerable.Range(0, l)
                .OrderByDescending(idx => eigenvalues[idx])
                .ToArray();

            double sumEigenvalues = 0.0;
            for (int i = 0; i < l; i++) sumEigenvalues += eigenvalues[i];

            // 1. Reconstruct all individual components m = 0 ... r-1
            double[][] compSeries = new double[r][];
            for (int m = 0; m < r; m++)
            {
                compSeries[m] = new double[count];
                int[] singleIndex = new[] { sortedIndices[m] };
                SsaDecompositionEngine.ReconstructGroup(processed, l, k, singleIndex.AsSpan(), eigenvectors, compSeries[m]);
            }

            // 2. Detect dominant harmonic cycle pair (m*, m*+1) if r >= 2
            int bestM = -1;
            for (int m = 1; m < r - 1; m++)
            {
                int idx1 = sortedIndices[m];
                int idx2 = sortedIndices[m + 1];
                double lambda1 = eigenvalues[idx1];
                double lambda2 = eigenvalues[idx2];

                if (lambda1 > 1e-12 && lambda2 > 1e-12)
                {
                    double ratio = lambda2 / lambda1;
                    if (ratio >= 0.75) // within 25% degeneracy
                    {
                        bestM = m;
                        break;
                    }
                }
            }
            if (bestM < 0 && r >= 2)
            {
                bestM = 1; // Default to component 1
            }

            // 3. Construct Layer Paths
            TrendPath = new List<StockAnalyzer.Core.Models.Point>(count);
            PrimaryCyclePath = new List<StockAnalyzer.Core.Models.Point>(count);
            CompositePath = new List<StockAnalyzer.Core.Models.Point>(count);
            UpperNoiseBandPath = new List<StockAnalyzer.Core.Models.Point>(count);
            LowerNoiseBandPath = new List<StockAnalyzer.Core.Models.Point>(count);

            double sumSqResidual = 0.0;
            double noiseMultiplier = (double)NoiseMultiplier;

            // Compute composite detrended sum
            double[] compositeDetrended = new double[count];
            for (int m = 0; m < r; m++)
            {
                for (int t = 0; t < count; t++)
                {
                    compositeDetrended[t] += compSeries[m][t];
                }
            }

            for (int t = 0; t < count; t++)
            {
                double trendBaseline = intercept + slope * t;
                double layer1 = compSeries[0][t] + trendBaseline;
                double layer2 = layer1;
                if (bestM >= 0 && bestM < r)
                {
                    layer2 += compSeries[bestM][t];
                    if (bestM + 1 < r)
                    {
                        layer2 += compSeries[bestM + 1][t];
                    }
                }

                double layer3 = compositeDetrended[t] + trendBaseline;

                double residual = processed[t] - compositeDetrended[t];
                sumSqResidual += residual * residual;

                TrendPath.Add(new StockAnalyzer.Core.Models.Point(timestamps[t], layer1));
                PrimaryCyclePath.Add(new StockAnalyzer.Core.Models.Point(timestamps[t], layer2));
                CompositePath.Add(new StockAnalyzer.Core.Models.Point(timestamps[t], layer3));
            }

            ResidualStdDev = Math.Sqrt(Math.Max(0.0, sumSqResidual / Math.Max(1, count - r)));

            for (int t = 0; t < count; t++)
            {
                double layer3 = CompositePath[t].Y;
                UpperNoiseBandPath.Add(new StockAnalyzer.Core.Models.Point(timestamps[t], layer3 + noiseMultiplier * ResidualStdDev));
                LowerNoiseBandPath.Add(new StockAnalyzer.Core.Models.Point(timestamps[t], layer3 - noiseMultiplier * ResidualStdDev));
            }

            // Diagnostics
            double signalEnergy = 0.0;
            for (int m = 0; m < r; m++) signalEnergy += eigenvalues[sortedIndices[m]];
            double noiseEnergy = Math.Max(0.0, sumEigenvalues - signalEnergy);

            CumulativeVarianceRatio = (sumEigenvalues > 1e-12) ? Math.Clamp(signalEnergy / sumEigenvalues, 0.0, 1.0) : 0.0;
            EffectiveRank = r;

            if (sumEigenvalues <= 1e-12)
            {
                SnrDb = 0.0;
                SignalPurity = 0.0;
            }
            else
            {
                double relEps = sumEigenvalues * 1e-12;
                double effNoise = Math.Max(noiseEnergy, relEps);
                double effSignal = Math.Max(signalEnergy, relEps);
                double ratio = effSignal / effNoise;
                SnrDb = Math.Clamp(10.0 * Math.Log10(ratio), -20.0, 40.0);
                SignalPurity = Math.Clamp((signalEnergy / sumEigenvalues) * 100.0, 0.0, 100.0);
            }

            // Dominant Period estimation via 1D DFT peak on primary harmonic component
            if (bestM >= 0 && bestM < r)
            {
                int eig1 = sortedIndices[bestM];
                int maxK = l / 2;
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
                    if (dftEnergy > maxDftEnergy)
                    {
                        maxDftEnergy = dftEnergy;
                        bestK = binK;
                    }
                }
                DominantPeriod = (bestK > 0) ? (double)l / bestK : 0.0;
            }
            else
            {
                DominantPeriod = 0.0;
            }

            _renderer.InvalidateCache();
        }
        finally
        {
            if (pooledProcessed != null) ArrayPool<double>.Shared.Return(pooledProcessed);
        }
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp = default, decimal? currentPrice = null)
    {
        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);
        var values = new List<DrawingCalculatedValue>
        {
            new("Samples", "Samples", SampleCount, SampleCount.ToString(), color),
            new("Embedding Dimension", "Embedding Dimension", EmbeddingDimension, EmbeddingDimension.ToString(), color),
            new("Components (r)", "Components (r)", NumComponents, NumComponents.ToString(), color),
            new("Variance Ratio", "Variance Ratio", (decimal)(CumulativeVarianceRatio * 100.0), $"{CumulativeVarianceRatio * 100.0:F1}%", color),
            new("Residual StdDev", "Residual StdDev", (decimal)ResidualStdDev, ResidualStdDev.ToString("F4"), color),
            new("Noise Multiplier", "Noise Multiplier", NoiseMultiplier, $"{NoiseMultiplier:F1}x", color),
            new("Dominant Period", "Dominant Period", DominantPeriod > 0 ? (decimal)DominantPeriod : null, DominantPeriod > 0 ? $"{DominantPeriod:F1} bars" : "N/A", color),
            new("SNR (dB)", "SNR (dB)", (decimal)SnrDb, $"{SnrDb:F1} dB", color),
            new("Signal Purity", "Signal Purity", (decimal)SignalPurity, $"{SignalPurity:F1}%", color)
        };
        return values;
    }
}
