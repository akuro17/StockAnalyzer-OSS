using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models.Analysis;
using Xunit;

namespace StockAnalyzer.Tests.Analysis;

public sealed class ScatterAnalysisEngineTests
{
    private const double Tolerance = 1e-6;

    [Fact]
    public void ScatterPoint_MemoryLayout_IsAlignedTo32Bytes()
    {
        int size = Unsafe.SizeOf<ScatterPoint>();
        Assert.Equal(32, size);

        var point = new ScatterPoint(10.5, 20.25);
        Assert.True(point.IsFinite);
        Assert.False(point.HasTimestamp);
        Assert.Null(point.TimestampUtc);

        var dt = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        var temporalPoint = new ScatterPoint(1.0, 2.0, dt, 0xFF00FF00, 4.0f);
        Assert.True(temporalPoint.HasTimestamp);
        Assert.Equal(dt, temporalPoint.TimestampUtc);
        Assert.Equal(0xFF00FF00u, temporalPoint.ColorArgb);
        Assert.Equal(4.0f, temporalPoint.Radius);

        var nanPoint = new ScatterPoint(double.NaN, 1.0);
        Assert.False(nanPoint.IsFinite);

        var infPoint = new ScatterPoint(1.0, double.PositiveInfinity);
        Assert.False(infPoint.IsFinite);
    }

    [Fact]
    public void ScatterDataset_Initialization_HoldsExpectedProperties()
    {
        var points = new[]
        {
            new ScatterPoint(1.0, 2.0),
            new ScatterPoint(3.0, 4.0)
        };
        var labels = new[] { "AAPL", "MSFT" };
        var dataset = new ScatterDataset("Test Dataset", "X Metric", "Y Metric", points, labels);

        Assert.Equal("Test Dataset", dataset.Title);
        Assert.Equal("X Metric", dataset.XAxisLabel);
        Assert.Equal("Y Metric", dataset.YAxisLabel);
        Assert.Equal(2, dataset.Points.Count);
        Assert.NotNull(dataset.Labels);
        Assert.Equal("AAPL", dataset.Labels![0]);
    }

    [Fact]
    public void Analyze_KnownLinearData_MatchesPerfectRegression()
    {
        // y = 2x + 1
        var points = new[]
        {
            new ScatterPoint(1.0, 3.0),
            new ScatterPoint(2.0, 5.0),
            new ScatterPoint(3.0, 7.0),
            new ScatterPoint(4.0, 9.0),
            new ScatterPoint(5.0, 11.0)
        };

        var result = ScatterAnalysisEngine.Analyze(points);

        Assert.Equal(5, result.TotalPointCount);
        Assert.Equal(5, result.ValidPointCount);
        Assert.Equal(0, result.OutlierCount);
        Assert.True(result.HasRegression);

        Assert.Equal(3.0, result.MeanX, Tolerance);
        Assert.Equal(7.0, result.MeanY, Tolerance);
        Assert.Equal(2.0, result.Slope, Tolerance);
        Assert.Equal(1.0, result.Intercept, Tolerance);
        Assert.Equal(1.0, result.CorrelationR, Tolerance);
        Assert.Equal(1.0, result.RSquared, Tolerance);
        Assert.Equal(0.0, result.ResidualStdError, Tolerance);

        // Bounds
        Assert.Equal(1.0, result.MinX, Tolerance);
        Assert.Equal(5.0, result.MaxX, Tolerance);
        Assert.Equal(3.0, result.MinY, Tolerance);
        Assert.Equal(11.0, result.MaxY, Tolerance);
    }

    [Fact]
    public void Analyze_OrthogonalUncorrelatedData_ProducesZeroCorrelation()
    {
        // 4 points symmetric around origin: (-1, 0), (1, 0), (0, -1), (0, 1)
        var points = new[]
        {
            new ScatterPoint(-1.0, 0.0),
            new ScatterPoint(1.0, 0.0),
            new ScatterPoint(0.0, -1.0),
            new ScatterPoint(0.0, 1.0)
        };

        var result = ScatterAnalysisEngine.Analyze(points);

        Assert.Equal(4, result.ValidPointCount);
        Assert.True(result.HasRegression);
        Assert.Equal(0.0, result.Slope, Tolerance);
        Assert.Equal(0.0, result.Intercept, Tolerance);
        Assert.Equal(0.0, result.CorrelationR, Tolerance);
        Assert.Equal(0.0, result.RSquared, Tolerance);
    }

    [Fact]
    public void Analyze_EmptyAndSinglePoint_SafeFallbackWithoutExceptions()
    {
        // N = 0
        var emptyResult = ScatterAnalysisEngine.Analyze(ReadOnlySpan<ScatterPoint>.Empty);
        Assert.Equal(0, emptyResult.TotalPointCount);
        Assert.Equal(0, emptyResult.ValidPointCount);
        Assert.False(emptyResult.HasRegression);
        Assert.False(emptyResult.HasCorrelation);
        Assert.False(emptyResult.HasIntervals);
        Assert.Equal(0.0, emptyResult.Slope);
        Assert.Equal(0.0, emptyResult.Intercept);
        Assert.True(double.IsNaN(emptyResult.CorrelationR));
        Assert.True(double.IsNaN(emptyResult.RSquared));

        // N = 1
        var singlePoint = new[] { new ScatterPoint(42.0, 84.0) };
        var singleResult = ScatterAnalysisEngine.Analyze(singlePoint);
        Assert.Equal(1, singleResult.TotalPointCount);
        Assert.Equal(1, singleResult.ValidPointCount);
        Assert.False(singleResult.HasRegression);
        Assert.False(singleResult.HasCorrelation);
        Assert.False(singleResult.HasIntervals);
        Assert.True(double.IsNaN(singleResult.CorrelationR));
        Assert.True(double.IsNaN(singleResult.RSquared));
        Assert.Equal(42.0, singleResult.MeanX, Tolerance);
        Assert.Equal(84.0, singleResult.MeanY, Tolerance);
        Assert.Equal(42.0, singleResult.MinX, Tolerance);
        Assert.Equal(42.0, singleResult.MaxX, Tolerance);
        Assert.Equal(84.0, singleResult.MinY, Tolerance);
        Assert.Equal(84.0, singleResult.MaxY, Tolerance);
        Assert.Equal(0.0, singleResult.Slope);
        Assert.Equal(0.0, singleResult.ResidualStdError);
        Assert.Single(singleResult.OutlierMask);
        Assert.False(singleResult.OutlierMask[0]);
    }

    [Fact]
    public void Analyze_VerticalAndHorizontalDegeneracies_HandledCorrectly()
    {
        // All X values identical: vertical alignment Sxx <= relative tol
        var verticalPoints = new[]
        {
            new ScatterPoint(5.0, 10.0),
            new ScatterPoint(5.0, 20.0),
            new ScatterPoint(5.0, 30.0)
        };
        var vertResult = ScatterAnalysisEngine.Analyze(verticalPoints);
        Assert.False(vertResult.HasRegression);
        Assert.False(vertResult.HasCorrelation);
        Assert.False(vertResult.HasIntervals);
        Assert.Equal(0.0, vertResult.Slope);
        Assert.Equal(0.0, vertResult.Intercept);
        Assert.True(double.IsNaN(vertResult.CorrelationR));
        Assert.True(double.IsNaN(vertResult.RSquared));

        // All Y values identical: horizontal alignment Syy <= relative tol
        var horizontalPoints = new[]
        {
            new ScatterPoint(1.0, 7.0),
            new ScatterPoint(2.0, 7.0),
            new ScatterPoint(3.0, 7.0)
        };
        var horizResult = ScatterAnalysisEngine.Analyze(horizontalPoints);
        Assert.True(horizResult.HasRegression);
        Assert.False(horizResult.HasCorrelation); // Correlation is undefined
        Assert.True(horizResult.HasIntervals);
        Assert.Equal(0.0, horizResult.Slope, Tolerance);
        Assert.Equal(7.0, horizResult.Intercept, Tolerance);
        Assert.True(double.IsNaN(horizResult.CorrelationR));
        Assert.True(double.IsNaN(horizResult.RSquared));
        Assert.Equal(0.0, horizResult.ResidualStdError, Tolerance);
    }

    [Fact]
    public void Analyze_NonFinitePointsOmitted_FiltersSafelyAndPreservesMaskAlignment()
    {
        var pointsWithInvalids = new[]
        {
            new ScatterPoint(1.0, 2.0),                          // idx 0: valid
            new ScatterPoint(double.NaN, 4.0),                  // idx 1: invalid
            new ScatterPoint(2.0, double.PositiveInfinity),      // idx 2: invalid
            new ScatterPoint(3.0, 6.0),                          // idx 3: valid
            new ScatterPoint(double.NegativeInfinity, double.NaN),// idx 4: invalid
            new ScatterPoint(5.0, 10.0)                          // idx 5: valid
        };

        var result = ScatterAnalysisEngine.Analyze(pointsWithInvalids);

        Assert.Equal(6, result.TotalPointCount);
        Assert.Equal(3, result.ValidPointCount); // (1,2), (3,6), (5,10)
        Assert.True(result.HasRegression);
        Assert.True(result.HasCorrelation);
        Assert.Equal(2.0, result.Slope, Tolerance);
        Assert.Equal(0.0, result.Intercept, Tolerance);
        Assert.Equal(1.0, result.CorrelationR, Tolerance);

        // OutlierMask must be 1:1 aligned with total original count
        Assert.Equal(6, result.OutlierMask.Count);
        Assert.False(result.OutlierMask[1]); // NaN point
        Assert.False(result.OutlierMask[2]); // Inf point
        Assert.False(result.OutlierMask[4]); // -Inf point
    }

    [Fact]
    public void Analyze_OutlierDetection_FlagsResidualOutliersWithOrigIndices()
    {
        // 10 collinear points (y = x) + 1 extreme outlier at index 5 (x = 6, y = 100)
        var points = new List<ScatterPoint>();
        for (int i = 1; i <= 10; i++)
        {
            if (i == 6)
            {
                points.Add(new ScatterPoint(i, 100.0)); // Outlier
            }
            else
            {
                points.Add(new ScatterPoint(i, i));
            }
        }

        var result = ScatterAnalysisEngine.Analyze(points.ToArray(), zScoreThreshold: 2.5);

        Assert.Equal(10, result.ValidPointCount);
        Assert.True(result.HasRegression);
        Assert.True(result.ResidualStdError > 0.0);
        Assert.True(result.OutlierDetectionAvailable);
        Assert.Equal(1, result.OutlierCount);
        Assert.True(result.OutlierMask[5]); // Index 5 is the outlier

        for (int i = 0; i < 10; i++)
        {
            if (i != 5)
            {
                Assert.False(result.OutlierMask[i]);
            }
        }
    }

    [Fact]
    public void Analyze_OutlierMask_ExactIndexMappingWithInterspersedNaNs()
    {
        // Interspersed NaNs:
        // [0: valid, 1: NaN, 2: valid, 3: NaN, 4: Outlier, 5: valid, 6: valid, 7: valid]
        var points = new[]
        {
            new ScatterPoint(1.0, 1.0),
            new ScatterPoint(double.NaN, 5.0),
            new ScatterPoint(2.0, 2.0),
            new ScatterPoint(3.0, double.PositiveInfinity),
            new ScatterPoint(4.0, 100.0), // Extreme Outlier at original index 4
            new ScatterPoint(5.0, 5.0),
            new ScatterPoint(6.0, 6.0),
            new ScatterPoint(7.0, 7.0)
        };

        var result = ScatterAnalysisEngine.Analyze(points, zScoreThreshold: 2.0);

        Assert.Equal(8, result.TotalPointCount);
        Assert.Equal(6, result.ValidPointCount);
        Assert.Equal(8, result.OutlierMask.Count);
        Assert.True(result.OutlierMask[4]); // Outlier mapped to index 4!
        Assert.False(result.OutlierMask[1]); // NaN
        Assert.False(result.OutlierMask[3]); // Inf
        Assert.False(result.OutlierMask[0]);
    }

    [Fact]
    public void Analyze_N2_RegressionHolds_IntervalsUndefined()
    {
        var points = new[]
        {
            new ScatterPoint(1.0, 3.0),
            new ScatterPoint(3.0, 7.0)
        };

        var result = ScatterAnalysisEngine.Analyze(points);

        Assert.Equal(2, result.ValidPointCount);
        Assert.True(result.HasRegression);
        Assert.True(result.HasCorrelation);
        Assert.False(result.HasIntervals); // df = 0, intervals undefined
        Assert.False(result.OutlierDetectionAvailable);
        Assert.Equal(2.0, result.Slope, Tolerance);
        Assert.Equal(1.0, result.Intercept, Tolerance);
        Assert.Equal(1.0, result.CorrelationR, Tolerance);
        Assert.Equal(0.0, result.ResidualStdError, Tolerance);
        Assert.Equal(0.0, result.TCritical95);
    }

    [Fact]
    public void Analyze_ScaleInvariance_MicroAndMacroData()
    {
        // Micro scale: 1e-7
        var microPoints = new[]
        {
            new ScatterPoint(1e-7, 3e-7),
            new ScatterPoint(2e-7, 5e-7),
            new ScatterPoint(3e-7, 7e-7),
            new ScatterPoint(4e-7, 9e-7),
            new ScatterPoint(5e-7, 11e-7)
        };
        var microRes = ScatterAnalysisEngine.Analyze(microPoints);
        Assert.True(microRes.HasRegression);
        Assert.True(microRes.HasCorrelation);
        Assert.Equal(2.0, microRes.Slope, 1e-5);
        Assert.Equal(1e-7, microRes.Intercept, 1e-12);
        Assert.Equal(1.0, microRes.CorrelationR, Tolerance);

        // Macro scale: 1e12
        var macroPoints = new[]
        {
            new ScatterPoint(1e12, 3e12),
            new ScatterPoint(2e12, 5e12),
            new ScatterPoint(3e12, 7e12),
            new ScatterPoint(4e12, 9e12),
            new ScatterPoint(5e12, 11e12)
        };
        var macroRes = ScatterAnalysisEngine.Analyze(macroPoints);
        Assert.True(macroRes.HasRegression);
        Assert.True(macroRes.HasCorrelation);
        Assert.Equal(2.0, macroRes.Slope, 1e-5);
        Assert.Equal(1e12, macroRes.Intercept, 1e7);
        Assert.Equal(1.0, macroRes.CorrelationR, Tolerance);
    }

    [Fact]
    public void Analyze_IqrR7QuantilesAndSuppression_CalculatesExactTukeyFences()
    {
        var points = new[]
        {
            new ScatterPoint(1.0, 10.0),
            new ScatterPoint(2.0, 20.0),
            new ScatterPoint(3.0, 30.0),
            new ScatterPoint(4.0, 40.0),
            new ScatterPoint(5.0, 50.0)
        };

        var result = ScatterAnalysisEngine.Analyze(points);

        Assert.Equal(-1.0, result.SuppressedMinX, Tolerance);
        Assert.Equal(7.0, result.SuppressedMaxX, Tolerance);
        Assert.Equal(-10.0, result.SuppressedMinY, Tolerance);
        Assert.Equal(70.0, result.SuppressedMaxY, Tolerance);

        // Fallback when IQR is degenerate: all points identical X
        var identicalPoints = new[]
        {
            new ScatterPoint(5.0, 10.0),
            new ScatterPoint(5.0, 20.0),
            new ScatterPoint(5.0, 30.0)
        };
        var idResult = ScatterAnalysisEngine.Analyze(identicalPoints);
        Assert.Equal(5.0, idResult.SuppressedMinX, Tolerance);
        Assert.Equal(5.0, idResult.SuppressedMaxX, Tolerance);
    }

    [Fact]
    public void NormalInverseCdf_AcklamAlgorithm_MatchesStandardNormalQuantiles()
    {
        Assert.Equal(0.0, ScatterAnalysisEngine.NormalInverseCdf(0.5), 1e-8);
        Assert.Equal(1.9599639845, ScatterAnalysisEngine.NormalInverseCdf(0.975), 1e-8);
        Assert.Equal(1.6448536269, ScatterAnalysisEngine.NormalInverseCdf(0.95), 1e-8);
        Assert.Equal(2.5758293035, ScatterAnalysisEngine.NormalInverseCdf(0.995), 1e-8);
        Assert.Equal(-1.9599639845, ScatterAnalysisEngine.NormalInverseCdf(0.025), 1e-8);

        Assert.True(double.IsNegativeInfinity(ScatterAnalysisEngine.NormalInverseCdf(0.0)));
        Assert.True(double.IsPositiveInfinity(ScatterAnalysisEngine.NormalInverseCdf(1.0)));
    }

    [Fact]
    public void GetTCritical95_LutAndCornishFisher_MatchSpecification()
    {
        // df in [1, 30]: exact LUT values
        Assert.Equal(12.7062, ScatterAnalysisEngine.GetTCritical95(1), Tolerance);
        Assert.Equal(4.3027, ScatterAnalysisEngine.GetTCritical95(2), Tolerance);
        Assert.Equal(3.1824, ScatterAnalysisEngine.GetTCritical95(3), Tolerance);
        Assert.Equal(2.7764, ScatterAnalysisEngine.GetTCritical95(4), Tolerance);
        Assert.Equal(2.5706, ScatterAnalysisEngine.GetTCritical95(5), Tolerance);
        Assert.Equal(2.2281, ScatterAnalysisEngine.GetTCritical95(10), Tolerance);
        Assert.Equal(2.0860, ScatterAnalysisEngine.GetTCritical95(20), Tolerance);
        Assert.Equal(2.0423, ScatterAnalysisEngine.GetTCritical95(30), Tolerance);

        // df > 30: 3-term Cornish-Fisher expansion
        int df = 60;
        double z = ScatterAnalysisEngine.NormalInverseCdf(0.975);
        double g1 = (Math.Pow(z, 3) + z) / 4.0;
        double g2 = ((5.0 * Math.Pow(z, 5)) + (16.0 * Math.Pow(z, 3)) + (3.0 * z)) / 96.0;
        double g3 = ((3.0 * Math.Pow(z, 7)) + (19.0 * Math.Pow(z, 5)) + (17.0 * Math.Pow(z, 3)) - (15.0 * z)) / 384.0;
        double expectedCf = z + (g1 / df) + (g2 / (df * df)) + (g3 / (df * df * df));
        Assert.Equal(expectedCf, ScatterAnalysisEngine.GetTCritical95(df), Tolerance);

        // Close to SciPy scipy.stats.t.ppf(0.975, 60) = 2.0002978
        Assert.Equal(2.0003, ScatterAnalysisEngine.GetTCritical95(60), 1e-4);

        // df <= 0
        Assert.Equal(0.0, ScatterAnalysisEngine.GetTCritical95(0));
        Assert.Equal(0.0, ScatterAnalysisEngine.GetTCritical95(-1));
    }

    [Fact]
    public void GetTCritical_VariableConfidenceLevels_CalculatesAccurately()
    {
        // 90% confidence level (alpha = 0.10) for df = 50: SciPy reference ~ 1.6759
        double t90 = ScatterAnalysisEngine.GetTCritical(50, 0.90);
        Assert.Equal(1.6759, t90, 1e-3);

        // 99% confidence level (alpha = 0.01) for df = 50: SciPy reference ~ 2.6778
        double t99 = ScatterAnalysisEngine.GetTCritical(50, 0.99);
        Assert.Equal(2.6778, t99, 1e-3);
    }

    [Fact]
    public void ConfidenceAndPredictionIntervals_CalculateConsistentBands()
    {
        // Points with non-zero residual variance
        var points = new[]
        {
            new ScatterPoint(1.0, 2.1),
            new ScatterPoint(2.0, 3.9),
            new ScatterPoint(3.0, 6.2),
            new ScatterPoint(4.0, 7.8),
            new ScatterPoint(5.0, 10.3)
        };

        var result = ScatterAnalysisEngine.Analyze(points);
        Assert.True(result.HasRegression);
        Assert.True(result.HasIntervals);
        Assert.True(result.ResidualStdError > 0.0);
        Assert.True(result.TCritical95 > 0.0);

        double testX = 3.0; // At MeanX
        var (ciLow, ciHigh) = result.GetConfidenceInterval(testX);
        var (piLow, piHigh) = result.GetPredictionInterval(testX);
        double yHat = result.PredictY(testX);

        // Symmetry around yHat at meanX
        Assert.True(ciLow < yHat);
        Assert.True(ciHigh > yHat);
        Assert.Equal(yHat - ciLow, ciHigh - yHat, Tolerance);

        // Prediction interval must be strictly wider than Confidence interval
        double ciWidth = ciHigh - ciLow;
        double piWidth = piHigh - piLow;
        Assert.True(piWidth > ciWidth);

        // Degenerate checks
        var emptyRes = ScatterAnalysisResult.Empty;
        var (eCiLow, eCiHigh) = emptyRes.GetConfidenceInterval(10.0);
        Assert.Equal(0.0, eCiLow);
        Assert.Equal(0.0, eCiHigh);
    }
}
