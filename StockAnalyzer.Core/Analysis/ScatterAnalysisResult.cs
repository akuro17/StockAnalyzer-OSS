using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// Immutable evaluation result containing summary statistics, OLS regression parameters,
/// correlation metrics, outlier identification, and autoscale bounds for scatter plot analysis.
/// </summary>
public sealed record ScatterAnalysisResult(
    int TotalPointCount,
    int ValidPointCount,
    int OutlierCount,
    double MeanX,
    double MeanY,
    double StdDevX,
    double StdDevY,
    bool HasRegression,
    double Slope,
    double Intercept,
    double CorrelationR,
    double RSquared,
    double ResidualStdError,
    double TCritical95,
    double MinX,
    double MaxX,
    double MinY,
    double MaxY,
    double SuppressedMinX,
    double SuppressedMaxX,
    double SuppressedMinY,
    double SuppressedMaxY,
    IReadOnlyList<bool> OutlierMask, // Index aligned with total original points
    bool HasIntervals = false,
    bool HasCorrelation = false,
    bool OutlierDetectionAvailable = false,
    double ConfidenceLevel = 0.95
)
{
    private const double Epsilon = 1e-12;

    public static ScatterAnalysisResult Empty { get; } = new(
        TotalPointCount: 0,
        ValidPointCount: 0,
        OutlierCount: 0,
        MeanX: 0.0,
        MeanY: 0.0,
        StdDevX: 0.0,
        StdDevY: 0.0,
        HasRegression: false,
        Slope: 0.0,
        Intercept: 0.0,
        CorrelationR: double.NaN,
        RSquared: double.NaN,
        ResidualStdError: 0.0,
        TCritical95: 0.0,
        MinX: 0.0,
        MaxX: 0.0,
        MinY: 0.0,
        MaxY: 0.0,
        SuppressedMinX: 0.0,
        SuppressedMaxX: 0.0,
        SuppressedMinY: 0.0,
        SuppressedMaxY: 0.0,
        OutlierMask: Array.Empty<bool>(),
        HasIntervals: false,
        HasCorrelation: false,
        OutlierDetectionAvailable: false,
        ConfidenceLevel: 0.95
    );

    /// <summary>
    /// Sum of squared deviations for X (S_xx).
    /// </summary>
    public double Sxx => ValidPointCount > 1 ? (ValidPointCount - 1) * StdDevX * StdDevX : 0.0;

    /// <summary>
    /// Sum of squared deviations for Y (S_yy).
    /// </summary>
    public double Syy => ValidPointCount > 1 ? (ValidPointCount - 1) * StdDevY * StdDevY : 0.0;

    /// <summary>
    /// Evaluates predicted Y for a given X on the regression line. Returns 0 if HasRegression is false.
    /// </summary>
    public double PredictY(double x) => HasRegression ? Intercept + Slope * x : 0.0;

    /// <summary>
    /// Computes the 95% Confidence Interval for the conditional mean E[Y|X=x].
    /// CI(x) = yHat +/- t_crit * s * sqrt(1/N + (x - meanX)^2 / S_xx).
    /// </summary>
    public (double Lower, double Upper) GetConfidenceInterval(double x)
    {
        double yHat = PredictY(x);
        if (!HasRegression || !HasIntervals || ValidPointCount < 3 || ResidualStdError <= 0.0 || TCritical95 <= 0.0)
        {
            return (yHat, yHat);
        }

        double sxx = Sxx;
        if (sxx <= Epsilon)
        {
            return (yHat, yHat);
        }

        double diff = x - MeanX;
        double se = ResidualStdError * Math.Sqrt((1.0 / ValidPointCount) + ((diff * diff) / sxx));
        double margin = TCritical95 * se;
        return (yHat - margin, yHat + margin);
    }

    /// <summary>
    /// Computes the 95% Prediction Interval for a single observation Y given X=x.
    /// PI(x) = yHat +/- t_crit * s * sqrt(1 + 1/N + (x - meanX)^2 / S_xx).
    /// </summary>
    public (double Lower, double Upper) GetPredictionInterval(double x)
    {
        double yHat = PredictY(x);
        if (!HasRegression || !HasIntervals || ValidPointCount < 3 || ResidualStdError <= 0.0 || TCritical95 <= 0.0)
        {
            return (yHat, yHat);
        }

        double sxx = Sxx;
        if (sxx <= Epsilon)
        {
            return (yHat, yHat);
        }

        double diff = x - MeanX;
        double se = ResidualStdError * Math.Sqrt(1.0 + (1.0 / ValidPointCount) + ((diff * diff) / sxx));
        double margin = TCritical95 * se;
        return (yHat - margin, yHat + margin);
    }
}
