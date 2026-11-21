using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

/// <summary>
/// DSL v5 review-adoption T6b (owner decisions Q-6 / R1, 2026-11-21): <c>opt_bfgs</c> accepts the noise floor of the central-difference gradient as convergence
/// when the line search fails at the identity Hessian or when an accepted step no longer lowers <c>f</c> resolvably; <c>opt_nelder_mead</c> stops on a
/// tolerance relative to the size of the point and of <c>f</c>. Probe data: <c>docs/sa_probe_dsl_v5_bfgs_nm</c>.
/// </summary>
public sealed class CalculatorDslV5OptimizerNoiseTests
{
    /// <summary>f = (x - cx)^2 + (y - cy)^2 + offset (the second term only when two-dimensional) plus an optional linear tilt.</summary>
    private readonly struct Bowl(double cx, double cy, double offset, int dim, int[] counter, double tilt = 0.0, double curvature = 1.0) : INdScalarFunction
    {
        public int Dimension => dim;

        public bool TryEvaluate(scoped ReadOnlySpan<double> x, ref DslExecutionBudget budget, out double y)
        {
            counter[0]++;
            double dx = x[0] - cx;
            y = (curvature * dx * dx) + offset + (tilt * x[0]);
            if (dim > 1)
            {
                double dy = x[1] - cy;
                y += dy * dy;
            }

            return true;
        }
    }

    private static (MathErrorCode Error, double[] X, int Evaluations) Minimize(
        bool bfgs, double cx, double cy, double offset, double[] start, double tol, double tilt = 0.0, double curvature = 1.0)
    {
        var counter = new int[1];
        var f = new Bowl(cx, cy, offset, start.Length, counter, tilt, curvature);
        var budget = DslExecutionBudget.CreateDefault();
        double[] xOut = new double[start.Length];
        MathErrorCode err = bfgs
            ? DslOptimizationMath.OptBfgs(f, start, tol, xOut, ref budget)
            : DslOptimizationMath.OptNelderMead(f, start, tol, xOut, ref budget);
        return (err, xOut, counter[0]);
    }

    private static double PositionError(double[] x, double cx, double cy)
        => x.Length > 1 ? Math.Max(Math.Abs(x[0] - cx), Math.Abs(x[1] - cy)) : Math.Abs(x[0] - cx);

    // ---------------------------------------------------------------- BFGS: noise-floor rescue (positive cases)

    public static IEnumerable<object[]> NoisyBfgsCases() =>
    [
        [0.0, 100.0, 1e-10],
        [0.0, 100.0, 1e-12],
        [0.0, 1e4, 1e-8],
        [0.0, 1e4, 1e-10],
        [0.0, 1e4, 1e-12],
        [10.0, 1.0, 1e-12],
    ];

    /// <summary>Probe cases that used to end in CONVERGENCE_FAILURE after 500 accepted iterations because the gradient noise (about 1e-8 at f = 100) stayed above tol.</summary>
    [Theory]
    [MemberData(nameof(NoisyBfgsCases))]
    public void BfgsAcceptsTheGradientNoiseFloorWhenTheFunctionValueStopsImproving(double start, double offset, double tol)
    {
        (MathErrorCode error, double[] x, int evaluations) = Minimize(true, 1.0, -2.0, offset, [start, start], tol);

        Assert.Equal(MathErrorCode.None, error);
        Assert.True(PositionError(x, 1.0, -2.0) <= 1e-5, $"position error {PositionError(x, 1.0, -2.0):E2}, x = [{string.Join(", ", x)}]");
        Assert.True(evaluations < 300, $"evaluations {evaluations}");
    }

    [Fact]
    public void BfgsKeepsItsAccuracyWhereTheGradientIsResolvable()
    {
        // The normal stop ||g||inf <= tol is unchanged: a noise-free problem still converges to the requested tolerance.
        (MathErrorCode error, double[] x, _) = Minimize(true, 1.0, -2.0, 0.0, [0.0, 0.0], 1e-12);
        Assert.Equal(MathErrorCode.None, error);
        Assert.True(PositionError(x, 1.0, -2.0) <= 1e-9, $"position error {PositionError(x, 1.0, -2.0):E2}");

        // Large offsets that used to succeed keep succeeding with an error that follows the resolution of f (sqrt(eps * K)).
        (error, x, _) = Minimize(true, 1.0, -2.0, 1e6, [0.0, 0.0], 1e-8);
        Assert.Equal(MathErrorCode.None, error);
        Assert.True(PositionError(x, 1.0, -2.0) <= 1e-4);
    }

    // ---------------------------------------------------------------- BFGS: no false convergence (negative cases)

    [Fact]
    public void BfgsDoesNotDeclareConvergenceOnASlopeWhoseGradientIsBelowTheNoiseFloor()
    {
        // f = 1e6 + 0.001 x: the gradient (1e-3) is far below the noise floor of the gradient at |f| = 1e6 (about 0.9), but every step lowers f by about 1e-6,
        // which is resolvable (the function-value noise is about 2e-9). There is no minimum: the optimiser must not claim one.
        (MathErrorCode error, _, int evaluations) = Minimize(true, 0.0, 0.0, 1e6, [0.0], 1e-8, tilt: 0.001, curvature: 0.0);

        Assert.Equal(MathErrorCode.ConvergenceFailure, error);
        Assert.True(evaluations > 100, $"evaluations {evaluations}");
    }

    [Fact]
    public void BfgsDoesNotStopHalfwayDownAGentleBowl()
    {
        // f = 100 + 1e-4 (x - 1000)^2 starting at 0: the gradient at the start is -0.2, far above its noise floor (about 2e-7).
        (MathErrorCode error, double[] x, _) = Minimize(true, 1000.0, 0.0, 100.0, [0.0], 1e-10, curvature: 1e-4);

        Assert.Equal(MathErrorCode.None, error);
        Assert.True(Math.Abs(x[0] - 1000.0) <= 1e-3, $"x = {x[0]}");
    }

    // ---------------------------------------------------------------- Nelder-Mead: tolerance relative to the size of the point and of f

    [Theory]
    [InlineData(1e7, 1e-8)]
    [InlineData(1e7, 1e-10)]
    [InlineData(1e7, 1e-12)]
    [InlineData(1e9, 1e-8)]
    [InlineData(1e9, 1e-10)]
    [InlineData(1e9, 1e-12)]
    public void NelderMeadConvergesAtCoordinatesWhereTheAbsoluteVertexToleranceIsBelowTheSpacingOfDoubles(double magnitude, double tol)
    {
        double centre = magnitude + 0.37;
        (MathErrorCode error, double[] x, int evaluations) = Minimize(false, centre, 0.0, 0.0, [centre + 10.0], tol);

        Assert.Equal(MathErrorCode.None, error);
        Assert.True(Math.Abs(x[0] - centre) <= 1e-3, $"x - c = {x[0] - centre:E2}");
        Assert.True(evaluations < 400, $"evaluations {evaluations}");
    }

    [Fact]
    public void NelderMeadKeepsItsAccuracyAtUnitScale()
    {
        (MathErrorCode error, double[] x, _) = Minimize(false, 1.0, -2.0, 0.0, [0.0, 0.0], 1e-12);
        Assert.Equal(MathErrorCode.None, error);
        Assert.True(PositionError(x, 1.0, -2.0) <= 1e-9, $"position error {PositionError(x, 1.0, -2.0):E2}");
    }

    [Fact]
    public void NelderMeadDoesNotStopBeforeTheSimplexHasShrunk()
    {
        // A relative tolerance must not end the search early: tol = 1e-8 at |x| ~ 1e3 still has to locate the minimum to well below 1e-4.
        (MathErrorCode error, double[] x, _) = Minimize(false, 1000.37, 0.0, 0.0, [900.0], 1e-8);
        Assert.Equal(MathErrorCode.None, error);
        Assert.True(Math.Abs(x[0] - 1000.37) <= 1e-4, $"x = {x[0]}");

        // A large offset of f makes the f-spread test relative to |f|: the position is still resolved to the resolution of f (sqrt(eps * 1e6) = 1.5e-5).
        (error, x, _) = Minimize(false, 1e5, 0.0, 1e6, [1e5 + 10.0], 1e-10);
        Assert.Equal(MathErrorCode.None, error);
        Assert.True(Math.Abs(x[0] - 1e5) <= 1e-3, $"x = {x[0]}");
    }
}
