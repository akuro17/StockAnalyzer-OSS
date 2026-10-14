using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using StockAnalyzer.Core.Models.Analysis;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// High-performance statistical analysis engine for 2D scatter plots.
/// Evaluates summary statistics, OLS linear regression, Pearson correlation,
/// residual standard error, Student's t confidence/prediction intervals,
/// residual Z-score outlier detection, and R-7 IQR autoscale bounds.
/// </summary>
public static class ScatterAnalysisEngine
{
    public const double DefaultConfidenceLevel = 0.95;
    public const double DefaultZScoreThreshold = 2.5;
    public const double PrecisionEpsilon = 1e-12;

    private static readonly double[] StudentTLut95 =
    [
        12.7062, // df = 1
        4.3027,  // df = 2
        3.1824,  // df = 3
        2.7764,  // df = 4
        2.5706,  // df = 5
        2.4469,  // df = 6
        2.3646,  // df = 7
        2.3060,  // df = 8
        2.2622,  // df = 9
        2.2281,  // df = 10
        2.2010,  // df = 11
        2.1788,  // df = 12
        2.1604,  // df = 13
        2.1448,  // df = 14
        2.1314,  // df = 15
        2.1199,  // df = 16
        2.1098,  // df = 17
        2.1009,  // df = 18
        2.0930,  // df = 19
        2.0860,  // df = 20
        2.0796,  // df = 21
        2.0739,  // df = 22
        2.0687,  // df = 23
        2.0639,  // df = 24
        2.0595,  // df = 25
        2.0555,  // df = 26
        2.0518,  // df = 27
        2.0484,  // df = 28
        2.0452,  // df = 29
        2.0423   // df = 30
    ];

    public const double MachineEpsilon = 2.220446049250313e-16;

    /// <summary>
    /// Evaluates if a 1D range [min, max] is numerically degenerate relative to its scale.
    /// </summary>
    public static bool IsDegenerateRange(double min, double max)
    {
        double scale = Math.Max(Math.Max(Math.Abs(min), Math.Abs(max)), 1.0);
        return (max - min) <= (64.0 * MachineEpsilon * scale);
    }

    /// <summary>
    /// Evaluates if a sum of squared deviations is numerically degenerate relative to its scale.
    /// </summary>
    public static bool IsDegenerateVariance(double ss, int count, double mean)
    {
        double scale = Math.Max(mean * mean, 1.0);
        return ss <= (64.0 * MachineEpsilon * Math.Max(count, 1) * scale);
    }

    /// <summary>
    /// Evaluates if residual sum of squares is effectively zero relative to total variance.
    /// </summary>
    public static bool IsResidualZero(double ssRes, double ssY, int count)
    {
        return ssRes <= (64.0 * MachineEpsilon * Math.Max(count, 1) * Math.Max(ssY, 1.0));
    }

    /// <summary>
    /// Computes the standard normal inverse cumulative distribution function (probit).
    /// Uses Peter J. Acklam's rational approximation with absolute error &lt; 1.15e-9.
    /// </summary>
    public static double NormalInverseCdf(double p)
    {
        if (p <= 0.0) return double.NegativeInfinity;
        if (p >= 1.0) return double.PositiveInfinity;
        if (double.IsNaN(p)) return double.NaN;

        const double a1 = -3.969683028665376e+01;
        const double a2 =  2.209460984245205e+02;
        const double a3 = -2.759285104469687e+02;
        const double a4 =  1.383577518672690e+02;
        const double a5 = -3.066479806614716e+01;
        const double a6 =  2.506628277459239e+00;

        const double b1 = -5.447609879822406e+01;
        const double b2 =  1.615858368580409e+02;
        const double b3 = -1.556989798598866e+02;
        const double b4 =  6.680131188771972e+01;
        const double b5 = -1.328068155288572e+01;

        const double c1 = -7.784894002430293e-03;
        const double c2 = -3.223964580411365e-01;
        const double c3 = -2.400758277161838e+00;
        const double c4 = -2.549732539343734e+00;
        const double c5 =  4.374664141464968e+00;
        const double c6 =  2.938163982698783e+00;

        const double d1 =  7.784695709041462e-03;
        const double d2 =  3.224671290700398e-01;
        const double d3 =  2.445134137142996e+00;
        const double d4 =  3.754408661907416e+00;

        const double pLow = 0.02425;
        const double pHigh = 1.0 - pLow;

        if (p < pLow)
        {
            double q = Math.Sqrt(-2.0 * Math.Log(p));
            return (((((c1 * q + c2) * q + c3) * q + c4) * q + c5) * q + c6) /
                   ((((d1 * q + d2) * q + d3) * q + d4) * q + 1.0);
        }

        if (p <= pHigh)
        {
            double q = p - 0.5;
            double r = q * q;
            return (((((a1 * r + a2) * r + a3) * r + a4) * r + a5) * r + a6) * q /
                   (((((b1 * r + b2) * r + b3) * r + b4) * r + b5) * r + 1.0);
        }

        double qu = Math.Sqrt(-2.0 * Math.Log(1.0 - p));
        return -(((((c1 * qu + c2) * qu + c3) * qu + c4) * qu + c5) * qu + c6) /
                ((((d1 * qu + d2) * qu + d3) * qu + d4) * qu + 1.0);
    }

    /// <summary>
    /// Computes two-tailed Student's t critical value for a given degrees of freedom and confidence level.
    /// Uses exact 4-decimal LUT for df in [1, 30] at 95% level, and generalized Cornish-Fisher expansion otherwise.
    /// </summary>
    public static double GetTCritical(int degreesOfFreedom, double confidenceLevel = DefaultConfidenceLevel)
    {
        if (degreesOfFreedom < 1)
        {
            return 0.0;
        }

        if (confidenceLevel <= 0.0 || confidenceLevel >= 1.0 || double.IsNaN(confidenceLevel))
        {
            confidenceLevel = DefaultConfidenceLevel;
        }

        if (Math.Abs(confidenceLevel - 0.95) < 1e-6 && degreesOfFreedom <= 30)
        {
            return StudentTLut95[degreesOfFreedom - 1];
        }

        double alpha = 1.0 - confidenceLevel;
        double p = 1.0 - (alpha * 0.5);
        double z = NormalInverseCdf(p);
        double df = degreesOfFreedom;
        double df2 = df * df;
        double df3 = df2 * df;

        double z2 = z * z;
        double z3 = z2 * z;
        double z5 = z3 * z2;
        double z7 = z5 * z2;

        double g1 = (z3 + z) / 4.0;
        double g2 = ((5.0 * z5) + (16.0 * z3) + (3.0 * z)) / 96.0;
        double g3 = ((3.0 * z7) + (19.0 * z5) + (17.0 * z3) - (15.0 * z)) / 384.0;

        return z + (g1 / df) + (g2 / df2) + (g3 / df3);
    }

    /// <summary>
    /// Computes two-tailed 95% Student's t critical value for a given degrees of freedom (df = N - 2).
    /// </summary>
    public static double GetTCritical95(int degreesOfFreedom) => GetTCritical(degreesOfFreedom, DefaultConfidenceLevel);

    /// <summary>
    /// Analyzes a dataset container of scatter points.
    /// </summary>
    public static ScatterAnalysisResult Analyze(
        ScatterDataset dataset,
        double confidenceLevel = DefaultConfidenceLevel,
        double zScoreThreshold = DefaultZScoreThreshold)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        if (dataset.Points.Count == 0)
        {
            return ScatterAnalysisResult.Empty;
        }

        if (dataset.Points is ScatterPoint[] array)
        {
            return Analyze(new ReadOnlySpan<ScatterPoint>(array), confidenceLevel, zScoreThreshold);
        }

        if (dataset.Points is List<ScatterPoint> list)
        {
            return Analyze(CollectionsMarshal.AsSpan(list), confidenceLevel, zScoreThreshold);
        }

        ScatterPoint[] buffer = new ScatterPoint[dataset.Points.Count];
        for (int i = 0; i < dataset.Points.Count; i++)
        {
            buffer[i] = dataset.Points[i];
        }

        return Analyze(new ReadOnlySpan<ScatterPoint>(buffer), confidenceLevel, zScoreThreshold);
    }

    /// <summary>
    /// Performs statistical evaluation and OLS regression analysis on a span of 2D scatter points.
    /// Automatically omits non-finite values (NaN, +/-Infinity) and safely handles degenerate datasets.
    /// </summary>
    public static ScatterAnalysisResult Analyze(
        ReadOnlySpan<ScatterPoint> points,
        double confidenceLevel = DefaultConfidenceLevel,
        double zScoreThreshold = DefaultZScoreThreshold)
    {
        int totalCount = points.Length;
        if (totalCount == 0)
        {
            return ScatterAnalysisResult.Empty;
        }

        // Count valid finite points
        int validCount = 0;
        for (int i = 0; i < totalCount; i++)
        {
            if (points[i].IsFinite)
            {
                validCount++;
            }
        }

        if (validCount == 0)
        {
            return ScatterAnalysisResult.Empty with
            {
                TotalPointCount = totalCount,
                OutlierMask = new bool[totalCount]
            };
        }

        // Extract valid points and map back to original indices
        ScatterPoint[] validPoints = new ScatterPoint[validCount];
        int[] origIndices = new int[validCount];
        int vIdx = 0;
        for (int i = 0; i < totalCount; i++)
        {
            if (points[i].IsFinite)
            {
                origIndices[vIdx] = i;
                validPoints[vIdx++] = points[i];
            }
        }

        bool[] fullOutlierMask = new bool[totalCount];

        // Degenerate case: N = 1
        if (validCount == 1)
        {
            double singleX = validPoints[0].X;
            double singleY = validPoints[0].Y;
            return new ScatterAnalysisResult(
                TotalPointCount: totalCount,
                ValidPointCount: 1,
                OutlierCount: 0,
                MeanX: singleX,
                MeanY: singleY,
                StdDevX: 0.0,
                StdDevY: 0.0,
                HasRegression: false,
                Slope: 0.0,
                Intercept: 0.0,
                CorrelationR: double.NaN,
                RSquared: double.NaN,
                ResidualStdError: 0.0,
                TCritical95: 0.0,
                MinX: singleX,
                MaxX: singleX,
                MinY: singleY,
                MaxY: singleY,
                SuppressedMinX: singleX,
                SuppressedMaxX: singleX,
                SuppressedMinY: singleY,
                SuppressedMaxY: singleY,
                OutlierMask: fullOutlierMask,
                HasIntervals: false,
                HasCorrelation: false,
                OutlierDetectionAvailable: false,
                ConfidenceLevel: confidenceLevel
            );
        }

        // N >= 2: Compute summary statistics
        double sumX = 0.0;
        double sumY = 0.0;
        double minX = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity;
        double maxY = double.NegativeInfinity;

        for (int i = 0; i < validCount; i++)
        {
            double x = validPoints[i].X;
            double y = validPoints[i].Y;

            sumX += x;
            sumY += y;

            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        double meanX = sumX / validCount;
        double meanY = sumY / validCount;

        // Sum of squares: S_xx, S_yy, S_xy
        double sxx = 0.0;
        double syy = 0.0;
        double sxy = 0.0;

        for (int i = 0; i < validCount; i++)
        {
            double dx = validPoints[i].X - meanX;
            double dy = validPoints[i].Y - meanY;

            sxx += dx * dx;
            syy += dy * dy;
            sxy += dx * dy;
        }

        double stdDevX = validCount > 1 ? Math.Sqrt(sxx / (validCount - 1)) : 0.0;
        double stdDevY = validCount > 1 ? Math.Sqrt(syy / (validCount - 1)) : 0.0;

        // Relative degeneracy tests
        bool isDegenerateX = IsDegenerateVariance(sxx, validCount, meanX);
        bool isDegenerateY = IsDegenerateVariance(syy, validCount, meanY);

        bool hasRegression = false;
        bool hasCorrelation = false;
        bool hasIntervals = false;
        double slope = 0.0;
        double intercept = 0.0;
        double correlationR = double.NaN;
        double rSquared = double.NaN;
        double residualStdError = 0.0;
        double tCrit = 0.0;

        if (!isDegenerateX)
        {
            hasRegression = true;
            slope = sxy / sxx;
            intercept = meanY - (slope * meanX);

            if (!isDegenerateY)
            {
                hasCorrelation = true;
                double denom = Math.Sqrt(sxx * syy);
                correlationR = Math.Clamp(sxy / denom, -1.0, 1.0);
                rSquared = correlationR * correlationR;
            }
            else
            {
                // Horizontal alignment (Syy degenerate): correlation undefined
                hasCorrelation = false;
                correlationR = double.NaN;
                rSquared = double.NaN;
            }

            double ssRes = Math.Max(0.0, syy - (slope * sxy));
            if (IsResidualZero(ssRes, syy, validCount))
            {
                ssRes = 0.0;
            }

            int df = validCount - 2;
            if (df > 0) // N >= 3
            {
                residualStdError = Math.Sqrt(ssRes / df);
                tCrit = GetTCritical(df, confidenceLevel);
                hasIntervals = true;
            }
            else // N == 2: df == 0, intervals mathematically undefined
            {
                residualStdError = 0.0;
                tCrit = 0.0;
                hasIntervals = false;
            }
        }
        else
        {
            // Vertical alignment (Sxx degenerate): slope undefined
            hasRegression = false;
            hasCorrelation = false;
            hasIntervals = false;
            slope = 0.0;
            intercept = 0.0;
            correlationR = double.NaN;
            rSquared = double.NaN;
            residualStdError = 0.0;
            tCrit = 0.0;
        }

        // Outlier detection via Externally Studentized Residuals (N >= 4 and hasRegression)
        int outlierCount = 0;
        bool outlierDetectionAvailable = false;

        if (hasRegression && validCount >= 4)
        {
            outlierDetectionAvailable = true;
            double sSquared = residualStdError * residualStdError;

            for (int i = 0; i < validCount; i++)
            {
                double xi = validPoints[i].X;
                double yi = validPoints[i].Y;
                double dx = xi - meanX;
                double hii = (1.0 / validCount) + ((dx * dx) / sxx);

                double ei = yi - (intercept + (slope * xi));
                double oneMinusHii = 1.0 - hii;

                double ti = 0.0;
                if (oneMinusHii > MachineEpsilon)
                {
                    double ssResI = ((validCount - 2) * sSquared) - ((ei * ei) / oneMinusHii);
                    double siSquared = ssResI / (validCount - 3);

                    if (siSquared > (64.0 * MachineEpsilon * Math.Max(sSquared, 1.0)))
                    {
                        double denom = Math.Sqrt(siSquared * oneMinusHii);
                        if (denom > 0.0)
                        {
                            ti = ei / denom;
                        }
                    }
                    else
                    {
                        // Leave-one-out residual variance is effectively zero (remaining N-1 points are collinear).
                        // If this point has a non-zero residual, it is an extreme outlier with infinite studentized residual.
                        if (Math.Abs(ei) > (64.0 * MachineEpsilon * Math.Max(Math.Abs(yi), 1.0)))
                        {
                            ti = Math.Sign(ei) * double.PositiveInfinity;
                        }
                        else
                        {
                            ti = 0.0;
                        }
                    }
                }

                if (Math.Abs(ti) > zScoreThreshold)
                {
                    fullOutlierMask[origIndices[i]] = true;
                    outlierCount++;
                }
            }
        }

        // Interquartile Range (IQR) Autoscale Suppression via R-7 method
        double[] xVals = new double[validCount];
        double[] yVals = new double[validCount];
        for (int i = 0; i < validCount; i++)
        {
            xVals[i] = validPoints[i].X;
            yVals[i] = validPoints[i].Y;
        }

        Array.Sort(xVals);
        Array.Sort(yVals);

        double q1X = ComputeQuantileR7(xVals, 0.25);
        double q3X = ComputeQuantileR7(xVals, 0.75);
        double iqrX = q3X - q1X;

        double suppMinX;
        double suppMaxX;
        if (IsDegenerateRange(minX, maxX) || IsDegenerateRange(q1X, q3X))
        {
            suppMinX = minX;
            suppMaxX = maxX;
        }
        else
        {
            suppMinX = q1X - (1.5 * iqrX);
            suppMaxX = q3X + (1.5 * iqrX);
        }

        double q1Y = ComputeQuantileR7(yVals, 0.25);
        double q3Y = ComputeQuantileR7(yVals, 0.75);
        double iqrY = q3Y - q1Y;

        double suppMinY;
        double suppMaxY;
        if (IsDegenerateRange(minY, maxY) || IsDegenerateRange(q1Y, q3Y))
        {
            suppMinY = minY;
            suppMaxY = maxY;
        }
        else
        {
            suppMinY = q1Y - (1.5 * iqrY);
            suppMaxY = q3Y + (1.5 * iqrY);
        }

        return new ScatterAnalysisResult(
            TotalPointCount: totalCount,
            ValidPointCount: validCount,
            OutlierCount: outlierCount,
            MeanX: meanX,
            MeanY: meanY,
            StdDevX: stdDevX,
            StdDevY: stdDevY,
            HasRegression: hasRegression,
            Slope: slope,
            Intercept: intercept,
            CorrelationR: correlationR,
            RSquared: rSquared,
            ResidualStdError: residualStdError,
            TCritical95: tCrit,
            MinX: minX,
            MaxX: maxX,
            MinY: minY,
            MaxY: maxY,
            SuppressedMinX: suppMinX,
            SuppressedMaxX: suppMaxX,
            SuppressedMinY: suppMinY,
            SuppressedMaxY: suppMaxY,
            OutlierMask: fullOutlierMask,
            HasIntervals: hasIntervals,
            HasCorrelation: hasCorrelation,
            OutlierDetectionAvailable: outlierDetectionAvailable,
            ConfidenceLevel: confidenceLevel
        );
    }

    /// <summary>
    /// Evaluates quantile using Hyndman-Fan type 7 (R-7 default linear interpolation).
    /// </summary>
    private static double ComputeQuantileR7(double[] sorted, double p)
    {
        int n = sorted.Length;
        if (n == 0) return 0.0;
        if (n == 1) return sorted[0];
        if (p <= 0.0) return sorted[0];
        if (p >= 1.0) return sorted[n - 1];

        double index = (n - 1) * p;
        int k = (int)Math.Floor(index);
        double d = index - k;

        if (k >= n - 1) return sorted[n - 1];
        return sorted[k] + (d * (sorted[k + 1] - sorted[k]));
    }
}
