using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.GeometricPattern;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// Parabolic Hough Transform Engine for detecting quadratic / parabolic structures (y = ax^2 + bx + c)
/// from financial time-series pivot points using Randomized Hough Transform (RHT) and inlier OLS polynomial regression.
/// </summary>
public static class ParabolicHoughTransformEngine
{
    private const double Epsilon = 1e-9;

    /// <summary>
    /// Detects structural parabolas from candlestick data within the lookback window.
    /// </summary>
    public static ParabolicHoughResult DetectParabolasFromCandles(
        IReadOnlyList<CandleData> candles,
        int lookback = 100,
        int pivotWindow = 3,
        int voteThreshold = 3,
        int maxCurves = 1,
        ParabolicHoughCurvatureSign curvatureSign = ParabolicHoughCurvatureSign.Both,
        double inlierToleranceAtrMultiplier = 0.25,
        double minRSquared = 0.60)
    {
        if (candles == null || candles.Count < Math.Max(lookback, pivotWindow * 2 + 1))
        {
            return ParabolicHoughResult.Empty;
        }

        int startIndex = Math.Max(0, candles.Count - lookback);
        int endIndex = candles.Count - 1;
        int count = endIndex - startIndex + 1;

        if (count < 5)
        {
            return ParabolicHoughResult.Empty;
        }

        // Sub-slice candles for analysis
        var windowCandles = new CandleData[count];
        for (int i = 0; i < count; i++)
        {
            windowCandles[i] = candles[startIndex + i];
        }

        // 1. Calculate ATR for noise-tolerant inlier banding
        double atr = GeometricPatternDetector.ComputeATR(candles, startIndex, count);
        if (atr < 1e-6)
        {
            atr = (double)(candles[endIndex].Close * 0.01m);
            if (atr < 1e-6) atr = 1.0;
        }

        // 2. Extract causal fractal pivots
        var pivotBuffer = new List<FractalPivot>(count / 2);
        PivotDetectionEngine.ExtractPivots(windowCandles, pivotWindow, pivotWindow, pivotBuffer);

        // Confirmation lag: only pivots causal to lookback window
        int maxConfirmedBar = count - 1 - pivotWindow;
        var pivotList = new List<(int Index, decimal Price)>();

        for (int i = 0; i < pivotBuffer.Count; i++)
        {
            if (pivotBuffer[i].Index <= maxConfirmedBar)
            {
                pivotList.Add((pivotBuffer[i].Index, pivotBuffer[i].Price));
            }
        }

        // Sort by bar index and remove duplicates at identical indices
        pivotList.Sort((a, b) => a.Index.CompareTo(b.Index));
        var uniquePivots = new List<(int Index, decimal Price)>(pivotList.Count);
        for (int i = 0; i < pivotList.Count; i++)
        {
            if (uniquePivots.Count == 0 || uniquePivots[^1].Index != pivotList[i].Index)
            {
                uniquePivots.Add(pivotList[i]);
            }
        }

        if (uniquePivots.Count < 3)
        {
            return ParabolicHoughResult.Empty;
        }

        return DetectParabolasFromPoints(
            uniquePivots,
            totalBars: count,
            startIndex: startIndex,
            atr: atr,
            voteThreshold: voteThreshold,
            maxCurves: maxCurves,
            curvatureSign: curvatureSign,
            inlierToleranceAtrMultiplier: inlierToleranceAtrMultiplier,
            minRSquared: minRSquared);
    }

    /// <summary>
    /// Detects parabolas from candidate pivot points.
    /// </summary>
    public static ParabolicHoughResult DetectParabolasFromPoints(
        IReadOnlyList<(int Index, decimal Price)> points,
        int totalBars,
        int startIndex,
        double atr,
        int voteThreshold = 3,
        int maxCurves = 1,
        ParabolicHoughCurvatureSign curvatureSign = ParabolicHoughCurvatureSign.Both,
        double inlierToleranceAtrMultiplier = 0.25,
        double minRSquared = 0.60)
    {
        if (points == null || points.Count < 3 || totalBars < 5)
        {
            return ParabolicHoughResult.Empty;
        }

        decimal minP = points[0].Price;
        decimal maxP = points[0].Price;
        for (int i = 1; i < points.Count; i++)
        {
            if (points[i].Price < minP) minP = points[i].Price;
            if (points[i].Price > maxP) maxP = points[i].Price;
        }

        double priceRangeD = (double)(maxP - minP);
        if (priceRangeD < Epsilon)
        {
            return ParabolicHoughResult.Empty;
        }

        double minPriceD = (double)minP;
        double inlierTol = inlierToleranceAtrMultiplier * atr;
        int m = points.Count;

        // Generate triplets
        var candidates = new List<HoughDetectedParabola>();
        int maxTriplets = 1200;
        int tripletCount = 0;

        // Deterministic pseudo-random generator if combinations exceed threshold
        bool exhaustive = (m * (m - 1) * (m - 2) / 6) <= maxTriplets;
        Random? rng = exhaustive ? null : new Random(42);

        for (int i = 0; i < m - 2; i++)
        {
            for (int j = i + 1; j < m - 1; j++)
            {
                for (int k = j + 1; k < m; k++)
                {
                    if (!exhaustive)
                    {
                        if (rng!.NextDouble() > (double)maxTriplets / (m * (m - 1) * (m - 2) / 6))
                            continue;
                    }

                    tripletCount++;
                    if (tripletCount > maxTriplets * 2) break;

                    int idx1 = points[i].Index;
                    int idx2 = points[j].Index;
                    int idx3 = points[k].Index;

                    int minBarSpan = Math.Max(1, Math.Min(2, (totalBars - 1) / 10));
                    if (Math.Abs(idx1 - idx2) < minBarSpan || Math.Abs(idx2 - idx3) < minBarSpan || Math.Abs(idx1 - idx3) < minBarSpan * 2)
                        continue;

                    double u1 = (double)idx1 / (totalBars - 1);
                    double u2 = (double)idx2 / (totalBars - 1);
                    double u3 = (double)idx3 / (totalBars - 1);

                    double v1 = ((double)points[i].Price - minPriceD) / priceRangeD;
                    double v2 = ((double)points[j].Price - minPriceD) / priceRangeD;
                    double v3 = ((double)points[k].Price - minPriceD) / priceRangeD;

                    double d1 = (u1 - u2) * (u1 - u3);
                    double d2 = (u2 - u1) * (u2 - u3);
                    double d3 = (u3 - u1) * (u3 - u2);

                    double detD = Math.Abs(d1 * d2 * d3);
                    if (detD < 1e-8 || Math.Abs(d1) < 1e-4 || Math.Abs(d2) < 1e-4 || Math.Abs(d3) < 1e-4)
                        continue;

                    double normA = v1 / d1 + v2 / d2 + v3 / d3;
                    double normB = -(v1 * (u2 + u3) / d1 + v2 * (u1 + u3) / d2 + v3 * (u1 + u2) / d3);
                    double normC = v1 * u2 * u3 / d1 + v2 * u1 * u3 / d2 + v3 * u1 * u2 / d3;

                    // Curvature sign check
                    if (curvatureSign == ParabolicHoughCurvatureSign.Convex && normA <= 0) continue;
                    if (curvatureSign == ParabolicHoughCurvatureSign.Concave && normA >= 0) continue;
                    if (Math.Abs(normA) < 0.05 || Math.Abs(normA) > 25.0) continue;

                    // Find inliers
                    var inliers = new List<(int Index, double Price)>();
                    for (int pIdx = 0; pIdx < m; pIdx++)
                    {
                        double u = (double)points[pIdx].Index / (totalBars - 1);
                        double modelV = normA * u * u + normB * u + normC;
                        double modelP = minPriceD + modelV * priceRangeD;
                        double actualP = (double)points[pIdx].Price;

                        if (Math.Abs(actualP - modelP) <= inlierTol)
                        {
                            inliers.Add((points[pIdx].Index, actualP));
                        }
                    }

                    if (inliers.Count < voteThreshold) continue;

                    // Refine via OLS Polynomial Regression
                    var (regA, regB, regC, r2) = FitQuadraticOLS(inliers);

                    if (r2 < minRSquared) continue;

                    if (curvatureSign == ParabolicHoughCurvatureSign.Convex && regA <= 0) continue;
                    if (curvatureSign == ParabolicHoughCurvatureSign.Concave && regA >= 0) continue;

                    // Calculate vertex
                    double xv = (Math.Abs(regA) > Epsilon) ? -regB / (2.0 * regA) : 0.0;
                    double yv = regA * xv * xv + regB * xv + regC;
                    int vertexBar = (int)Math.Round(xv);

                    decimal startPrice = (decimal)regC;
                    decimal endPrice = (decimal)(regA * (totalBars - 1) * (totalBars - 1) + regB * (totalBars - 1) + regC);
                    decimal vertexPrice = (decimal)yv;

                    int minBar = inliers[0].Index;
                    int maxBar = inliers[^1].Index;
                    int span = maxBar - minBar;

                    double touchScore = Math.Clamp((inliers.Count - 2) / 4.0, 0.0, 1.0) * 100.0;
                    double spanCoverage = Math.Clamp(span / Math.Max(1.0, totalBars * 0.5), 0.1, 1.0);
                    double strength = touchScore * spanCoverage * r2;

                    var detectedSign = regA > 0 ? ParabolicHoughCurvatureSign.Convex : ParabolicHoughCurvatureSign.Concave;

                    candidates.Add(new HoughDetectedParabola(
                        NormA: normA,
                        NormB: normB,
                        NormC: normC,
                        CurvaturePrice: regA,
                        SlopePrice: regB,
                        InterceptPrice: (decimal)regC,
                        StartBar: startIndex,
                        EndBar: startIndex + totalBars - 1,
                        StartPrice: startPrice,
                        EndPrice: endPrice,
                        VertexPrice: vertexPrice,
                        VertexBar: startIndex + vertexBar,
                        Votes: inliers.Count,
                        RSquared: r2,
                        Strength: strength,
                        CurvatureSign: detectedSign));
                }
            }
        }

        if (candidates.Count == 0)
        {
            return ParabolicHoughResult.Empty;
        }

        // Sort by strength descending
        candidates.Sort((x, y) => y.Strength.CompareTo(x.Strength));

        // Cluster / suppress close duplicates
        var selected = new List<HoughDetectedParabola>();
        foreach (var cand in candidates)
        {
            bool duplicate = false;
            foreach (var sel in selected)
            {
                // If vertex bar and prices are within 5% / 3 bars, treat as duplicate
                if (Math.Abs(cand.VertexBar - sel.VertexBar) <= 3 &&
                    Math.Abs((double)(cand.VertexPrice - sel.VertexPrice)) <= atr * 0.5)
                {
                    duplicate = true;
                    break;
                }
            }

            if (!duplicate)
            {
                selected.Add(cand);
                if (selected.Count >= maxCurves) break;
            }
        }

        return new ParabolicHoughResult(selected, m);
    }

    /// <summary>
    /// Fits a quadratic polynomial y = Ax^2 + Bx + C using Ordinary Least Squares (OLS) via normal equations.
    /// </summary>
    public static (double A, double B, double C, double RSquared) FitQuadraticOLS(IReadOnlyList<(int Index, double Price)> points)
    {
        int n = points.Count;
        if (n < 3) return (0, 0, points.Count > 0 ? points[0].Price : 0, 0);

        double s0 = n;
        double s1 = 0, s2 = 0, s3 = 0, s4 = 0;
        double t0 = 0, t1 = 0, t2 = 0;

        for (int i = 0; i < n; i++)
        {
            double x = points[i].Index;
            double y = points[i].Price;
            double x2 = x * x;

            s1 += x;
            s2 += x2;
            s3 += x2 * x;
            s4 += x2 * x2;

            t0 += y;
            t1 += x * y;
            t2 += x2 * y;
        }

        // Solve 3x3 linear system:
        // [ s4  s3  s2 ] [ A ]   [ t2 ]
        // [ s3  s2  s1 ] [ B ] = [ t1 ]
        // [ s2  s1  s0 ] [ C ]   [ t0 ]

        double det = s4 * (s2 * s0 - s1 * s1)
                   - s3 * (s3 * s0 - s1 * s2)
                   + s2 * (s3 * s1 - s2 * s2);

        if (Math.Abs(det) < Epsilon)
        {
            return (0, 0, t0 / n, 0);
        }

        double detA = t2 * (s2 * s0 - s1 * s1)
                    - s3 * (t1 * s0 - s1 * t0)
                    + s2 * (t1 * s1 - s2 * t0);

        double detB = s4 * (t1 * s0 - s1 * t0)
                    - t2 * (s3 * s0 - s1 * s2)
                    + s2 * (s3 * t0 - t1 * s2);

        double detC = s4 * (s2 * t0 - t1 * s1)
                    - s3 * (s3 * t0 - t1 * s2)
                    + t2 * (s3 * s1 - s2 * s2);

        double a = detA / det;
        double b = detB / det;
        double c = detC / det;

        // Compute R^2
        double yMean = t0 / n;
        double ssTot = 0;
        double ssRes = 0;

        for (int i = 0; i < n; i++)
        {
            double x = points[i].Index;
            double y = points[i].Price;
            double pred = a * x * x + b * x + c;

            ssTot += (y - yMean) * (y - yMean);
            ssRes += (y - pred) * (y - pred);
        }

        double r2 = ssTot > Epsilon ? Math.Max(0.0, 1.0 - (ssRes / ssTot)) : 1.0;
        return (a, b, c, r2);
    }
}
