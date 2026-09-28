using System;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Core.Tests.MathUtils;

public class ClothoidMathTests
{
    [Fact]
    public void EstimateInitialState_EmptyOrSinglePrice_ReturnsSafeDefaults()
    {
        var stateEmpty = ClothoidMath.EstimateInitialState(ReadOnlySpan<decimal>.Empty);
        Assert.Equal(0.0, stateEmpty.BasePrice);
        Assert.True(stateEmpty.VolatilityScale > 0.0);

        decimal[] single = [150.0m];
        var stateSingle = ClothoidMath.EstimateInitialState(single);
        Assert.Equal(150.0, stateSingle.BasePrice);
        Assert.True(stateSingle.VolatilityScale > 0.0);
    }

    [Fact]
    public void EstimateInitialState_UpwardTrend_ProducesPositiveVelocityAndAngle()
    {
        decimal[] prices = [100m, 102m, 105m, 109m, 115m];
        var state = ClothoidMath.EstimateInitialState(prices, curvatureRate: 0.05);

        Assert.Equal(115.0, state.BasePrice);
        Assert.True(state.InitialAngle > 0.0, "Upward trend must produce positive initial angle.");
        Assert.True(state.InitialCurvature > 0.0, "Accelerating trend must produce positive initial curvature.");
    }

    [Fact]
    public void GenerateTrajectory_TimeMonotonicity_TrajectoryPointsAreValidAndOrdered()
    {
        var state = new ClothoidMath.ClothoidState(
            BasePrice: 100.0,
            VolatilityScale: 2.5,
            InitialAngle: 0.2,
            InitialCurvature: 0.05,
            CurvatureRate: 0.02);

        int steps = 20;
        double[] proj = new double[steps];
        double[] upper = new double[steps];
        double[] lower = new double[steps];

        ClothoidMath.GenerateTrajectory(state, steps, 2.0, proj, upper, lower);

        for (int i = 0; i < steps; i++)
        {
            Assert.False(double.IsNaN(proj[i]), $"Projected price at step {i} is NaN.");
            Assert.False(double.IsNaN(upper[i]), $"Upper bound at step {i} is NaN.");
            Assert.False(double.IsNaN(lower[i]), $"Lower bound at step {i} is NaN.");

            Assert.True(upper[i] > proj[i], $"Upper bound must exceed projected price at step {i}.");
            Assert.True(lower[i] < proj[i], $"Lower bound must be below projected price at step {i}.");
        }

        // Confidence interval bandwidth must expand over time (sqrt(k) diffusion)
        double marginFirst = upper[0] - proj[0];
        double marginLast = upper[^1] - proj[^1];
        Assert.True(marginLast > marginFirst, "Confidence band must widen with future projection steps.");
    }

    [Fact]
    public void GenerateTrajectory_CurvatureRatePolarity_PositiveTurnsUpwardComparedToNegative()
    {
        var statePos = new ClothoidMath.ClothoidState(100.0, 2.0, 0.0, 0.0, 0.1);
        var stateNeg = new ClothoidMath.ClothoidState(100.0, 2.0, 0.0, 0.0, -0.1);

        double[] projPos = new double[10];
        double[] upper = new double[10];
        double[] lower = new double[10];

        ClothoidMath.GenerateTrajectory(statePos, 10, 2.0, projPos, upper, lower);

        double[] projNeg = new double[10];
        ClothoidMath.GenerateTrajectory(stateNeg, 10, 2.0, projNeg, upper, lower);

        Assert.True(projPos[^1] > projNeg[^1], "Positive curvature rate must curve above negative curvature rate.");
    }

    [Fact]
    public void EvaluateClothoidPoint_ZeroCurvatureRateAndCurvature_ProducesStraightLine()
    {
        double s = 5.0;
        double theta0 = 0.3;
        var (x, y) = ClothoidMath.EvaluateClothoidPoint(s, theta0, kappa0: 0.0, c: 0.0);

        Assert.Equal(s * Math.Cos(theta0), x, 10);
        Assert.Equal(s * Math.Sin(theta0), y, 10);
    }

    [Fact]
    public void EvaluateClothoidPoint_ZeroCurvatureRateWithCurvature_ProducesCircularArc()
    {
        double s = 1.0;
        double theta0 = 0.2;
        double kappa0 = 0.4;
        var (x, y) = ClothoidMath.EvaluateClothoidPoint(s, theta0, kappa0, c: 0.0);

        double expectedTheta = theta0 + kappa0 * s;
        double expectedX = (1.0 / kappa0) * (Math.Sin(expectedTheta) - Math.Sin(theta0));
        double expectedY = -(1.0 / kappa0) * (Math.Cos(expectedTheta) - Math.Cos(theta0));

        Assert.Equal(expectedX, x, 10);
        Assert.Equal(expectedY, y, 10);
    }

    [Fact]
    public void DampAngle_ExtremeAngles_RemainsStrictlyBounded()
    {
        double maxExpected = Math.PI / 2.0 - 0.05;

        double dampedModerate = ClothoidMath.DampAngle(2.5);
        double dampedPos = ClothoidMath.DampAngle(100.0);
        double dampedNeg = ClothoidMath.DampAngle(-100.0);

        Assert.True(dampedModerate < maxExpected);
        Assert.True(dampedModerate > 0.0);
        Assert.True(dampedPos <= maxExpected);
        Assert.True(dampedPos > 0.0);
        Assert.True(dampedNeg >= -maxExpected);
        Assert.True(dampedNeg < 0.0);
    }

    [Fact]
    public void EstimateInitialState_ExtremeOutlierSpike_GuardsInitialCurvatureLimit()
    {
        // Spiked price series
        decimal[] prices = [100m, 101m, 102m, 1000m, 103m, 104m];
        var state = ClothoidMath.EstimateInitialState(prices);

        Assert.True(Math.Abs(state.InitialCurvature) <= 1.5, "Initial curvature must be bounded within [-1.5, 1.5].");
        Assert.True(state.VolatilityScale > 0.0);
    }

    [Fact]
    public void GenerateTrajectory_ExtremeDownwardTrajectory_ClampsLowerPriceToNonNegativeFloor()
    {
        var crashingState = new ClothoidMath.ClothoidState(
            BasePrice: 10.0,
            VolatilityScale: 20.0,
            InitialAngle: -1.2,
            InitialCurvature: -1.0,
            CurvatureRate: -0.1);

        int steps = 20;
        double[] proj = new double[steps];
        double[] upper = new double[steps];
        double[] lower = new double[steps];

        ClothoidMath.GenerateTrajectory(crashingState, steps, confidenceMultiplier: 3.0, proj, upper, lower);

        for (int i = 0; i < steps; i++)
        {
            Assert.True(lower[i] >= 0.0001, $"Lower bound at step {i} must be >= 0.0001 (was {lower[i]}).");
        }
    }

    [Fact]
    public void GenerateTrajectory_LongRangeHorizon_DoesNotFreezeIntoHorizontalCeiling()
    {
        var state = new ClothoidMath.ClothoidState(
            BasePrice: 100.0,
            VolatilityScale: 2.0,
            InitialAngle: 0.1,
            InitialCurvature: 0.05,
            CurvatureRate: 0.05);

        int steps = 40;
        double[] proj = new double[steps];
        double[] upper = new double[steps];
        double[] lower = new double[steps];

        ClothoidMath.GenerateTrajectory(state, steps, confidenceMultiplier: 2.0, proj, upper, lower);

        // Verify that throughout the entire range, especially later steps, points do not flat-line into a horizontal ceiling
        for (int i = 1; i < steps; i++)
        {
            Assert.False(double.IsNaN(proj[i]));
            // Steps must have non-zero variation and not freeze at the exact same float value
            Assert.NotEqual(proj[i], proj[i - 1], 6);
        }
    }

    [Fact]
    public void EstimateInitialState_TerminalBounceInDowntrend_BlendsMacroTrend()
    {
        // 14 bars dropping, last 1 bar bounces slightly
        decimal[] prices = [100m, 98m, 96m, 94m, 92m, 90m, 88m, 86m, 84m, 82m, 80m, 78m, 76m, 74m, 75m];
        var state = ClothoidMath.EstimateInitialState(prices);

        // Macro trend (strongly down) should significantly temper the terminal single-bar bounce (+1m)
        // so that the initial angle is not steep positive
        Assert.True(state.InitialAngle < 0.2, $"Initial angle ({state.InitialAngle}) should be suppressed by macro downtrend.");
    }

    [Fact]
    public void EvaluateClothoidPoint_OriginContinuity_ConvergesWithoutJump()
    {
        // Test near-origin limit with non-zero curvature and curvature rate
        var (x0, y0) = ClothoidMath.EvaluateClothoidPoint(0.0, theta0: 0.5, kappa0: 0.3, c: 0.05);
        Assert.Equal(0.0, x0);
        Assert.Equal(0.0, y0);

        // For micro s -> 0+, coordinates must be continuous and within 1e-4 of origin
        var (xMicro, yMicro) = ClothoidMath.EvaluateClothoidPoint(0.0001, theta0: 0.5, kappa0: 0.3, c: 0.05);
        Assert.InRange(xMicro, 0.0, 0.0002);
        Assert.InRange(Math.Abs(yMicro), 0.0, 0.0002);
    }

    [Fact]
    public void GenerateTrajectory_BandOrderingAndNonNegativeFloor_AlwaysPreserved()
    {
        // Severe negative trajectory crashing towards zero
        var crashingState = new ClothoidMath.ClothoidState(
            BasePrice: 1.0,
            VolatilityScale: 5.0,
            InitialAngle: -1.4,
            InitialCurvature: -1.0,
            CurvatureRate: -0.1);

        int steps = 15;
        double[] proj = new double[steps];
        double[] upper = new double[steps];
        double[] lower = new double[steps];

        ClothoidMath.GenerateTrajectory(crashingState, steps, confidenceMultiplier: 2.5, proj, upper, lower);

        for (int k = 0; k < steps; k++)
        {
            Assert.True(lower[k] >= 0.0001, $"Lower bound at step {k} must be >= 0.0001.");
            Assert.True(proj[k] >= lower[k], $"Projected price must be >= lower bound at step {k}.");
            Assert.True(upper[k] >= proj[k], $"Upper bound must be >= projected price at step {k}.");
        }
    }
}
