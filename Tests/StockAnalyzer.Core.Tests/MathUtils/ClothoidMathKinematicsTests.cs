using System;
using System.Linq;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Core.Tests.MathUtils;

public class ClothoidMathKinematicsTests
{
    [Fact]
    public void SolveCubicWlsKinematics_LengthLessThanFour_ReturnsFalse()
    {
        decimal[] prices = [100m, 101m, 102m];
        bool success = ClothoidMath.SolveCubicWlsKinematics(prices, 1.5, out double v, out double a, out double j);

        Assert.False(success);
        Assert.Equal(0.0, v);
        Assert.Equal(0.0, a);
        Assert.Equal(0.0, j);
    }

    [Fact]
    public void SolveCubicWlsKinematics_FlatPrices_ReturnsTrueWithZeroKinematics()
    {
        decimal[] prices = [100m, 100m, 100m, 100m, 100m, 100m];
        bool success = ClothoidMath.SolveCubicWlsKinematics(prices, 1.5, out double v, out double a, out double j);

        Assert.True(success);
        Assert.False(double.IsNaN(v));
        Assert.False(double.IsNaN(a));
        Assert.False(double.IsNaN(j));
        Assert.InRange(Math.Abs(v), 0.0, 1e-6);
        Assert.InRange(Math.Abs(a), 0.0, 1e-6);
        Assert.InRange(Math.Abs(j), 0.0, 1e-6);
    }

    [Fact]
    public void SolveCubicWlsKinematics_LinearUpwardTrend_ProducesPositiveVelocity()
    {
        // Linearly increasing prices
        decimal[] prices = [100m, 102m, 104m, 106m, 108m, 110m, 112m, 114m];
        bool success = ClothoidMath.SolveCubicWlsKinematics(prices, 0.0, out double v, out double a, out double j);

        Assert.True(success);
        Assert.True(v > 0.0, "Velocity must be positive for upward linear trend.");
        Assert.InRange(Math.Abs(a), 0.0, 1e-4);
        Assert.InRange(Math.Abs(j), 0.0, 1e-4);
    }

    [Fact]
    public void SolveCubicWlsKinematics_ParabolicPrices_ProducesPositiveAcceleration()
    {
        // Convex acceleration: y(t) = 0.5 * t^2 where t = i - (N - 1)
        int n = 10;
        decimal[] prices = new decimal[n];
        for (int i = 0; i < n; i++)
        {
            double t = i - (n - 1);
            prices[i] = (decimal)(100.0 + 0.5 * t * t);
        }

        bool success = ClothoidMath.SolveCubicWlsKinematics(prices, 0.0, out double v, out double a, out double j);

        Assert.True(success);
        // At t = 0, y'(0) = 0 (velocity 0), y''(0) = 1.0 (acceleration > 0)
        Assert.InRange(Math.Abs(v), 0.0, 1e-4);
        Assert.True(a > 0.0, "Acceleration must be positive for convex quadratic function.");
        Assert.InRange(Math.Abs(j), 0.0, 1e-4);
    }

    [Fact]
    public void CalculateCurvatureRateFromKinematics_StandardCases_MatchesAnalyticalEquations()
    {
        // Case 1: Pure jerk with zero velocity and acceleration
        // c = (1 * (1 + 0) - 0) / 1^3 = 1.0
        double c1 = ClothoidMath.CalculateCurvatureRateFromKinematics(velocity: 0.0, acceleration: 0.0, jerk: 1.0);
        Assert.Equal(1.0, c1, 6);

        // Case 2: Negative jerk with zero velocity and acceleration
        double c2 = ClothoidMath.CalculateCurvatureRateFromKinematics(velocity: 0.0, acceleration: 0.0, jerk: -2.5);
        Assert.Equal(-2.5, c2, 6);

        // Case 3: Velocity = 1, Acceleration = 0, Jerk = 1
        // c = (1 * (1 + 1) - 0) / (1 + 1)^3 = 2 / 8 = 0.25
        double c3 = ClothoidMath.CalculateCurvatureRateFromKinematics(velocity: 1.0, acceleration: 0.0, jerk: 1.0);
        Assert.Equal(0.25, c3, 6);

        // Case 4: Velocity = 1, Acceleration = 1, Jerk = 0
        // c = (0 - 3 * 1 * 1) / (1 + 1)^3 = -3 / 8 = -0.375
        double c4 = ClothoidMath.CalculateCurvatureRateFromKinematics(velocity: 1.0, acceleration: 1.0, jerk: 0.0);
        Assert.Equal(-0.375, c4, 6);
    }

    [Fact]
    public void GenerateFresnelWeights_PeriodOne_SetsSingleWeightToOne()
    {
        Span<double> weights = stackalloc double[1];
        ClothoidMath.GenerateFresnelWeights(weights, 1, 0.5, 6.0);

        Assert.Equal(1.0, weights[0]);
    }

    [Fact]
    public void GenerateFresnelWeights_CenteredOffset_IsSymmetric()
    {
        int period = 21;
        Span<double> weights = stackalloc double[period];
        ClothoidMath.GenerateFresnelWeights(weights, period, offset: 0.5, sigma: 6.0);

        int mid = (period - 1) / 2;
        Assert.True(weights[mid] > 0.0, "Center weight must be positive.");

        // Peak should be at center
        for (int i = 0; i < period; i++)
        {
            Assert.True(weights[i] <= weights[mid] + 1e-9, "Center must be max weight for offset 0.5.");
            Assert.True(weights[i] >= 0.0, "Fresnel weights must be non-negative.");
        }

        // Symmetry test
        for (int i = 0; i < mid; i++)
        {
            Assert.Equal(weights[i], weights[period - 1 - i], 9);
        }
    }

    [Fact]
    public void GenerateFresnelWeights_TerminalOffset_PeaksNearTerminalBar()
    {
        int period = 20;
        Span<double> weights = stackalloc double[period];
        ClothoidMath.GenerateFresnelWeights(weights, period, offset: 1.0, sigma: 6.0);

        // Peak should be at the terminal bar (period - 1)
        Assert.Equal(weights[^1], weights.ToArray().Max());
        Assert.True(weights[^1] > weights[0]);
    }

    [Fact]
    public void SolveCubicWlsKinematics_HighBasePriceWithMicroDifferences_PreservesPrecision()
    {
        int n = 10;
        decimal[] prices = new decimal[n];
        for (int i = 0; i < n; i++)
        {
            prices[i] = 50000.0m + 0.01m * i;
        }

        bool success = ClothoidMath.SolveCubicWlsKinematics(prices, 0.0, out double v, out double a, out double j);
        Assert.True(success);
        Assert.True(v > 0.0, "Velocity should be positive for upward micro trend.");
        Assert.InRange(Math.Abs(a), 0.0, 1e-4);
        Assert.InRange(Math.Abs(j), 0.0, 1e-4);
    }
}
