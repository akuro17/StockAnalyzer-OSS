#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Calculator;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

/// <summary>
/// DSL v5 Task 1: general linear algebra (A), weighted Gaussian quadrature + ODE (B), cubic/quartic + complex projections (C),
/// numerical optimization (D) and piecewise/array operations (F).
/// </summary>
public sealed class CalculatorDslV5Task1Tests
{
    private static readonly IReadOnlyDictionary<string, decimal> Empty =
        new Dictionary<string, decimal>(StringComparer.Ordinal);

    private static readonly CalculatorLimits Heavy = new()
    {
        MaxExecutionOps = 800_000,
        MaxExpressionChars = 2048,
        MaxTokens = 1024,
    };

    private const double TightTolerance = 1e-12;
    private const double IntegrationTolerance = 1e-9;
    private const double SolverTolerance = 1e-6;

    private static ExpressionCalculator.CalculatorBatchResult Run(string expression, CalculatorLimits? limits = null)
    {
        var items = new[] { new ExpressionCalculator.CalculatorBatchItem("r", expression) };
        return ExpressionCalculator.EvaluateBatch(Empty, null, items, limits ?? CalculatorLimits.Default)[0];
    }

    private static decimal Scalar(string expression, CalculatorLimits? limits = null)
    {
        ExpressionCalculator.CalculatorBatchResult result = Run(expression, limits);
        Assert.True(result.IsOk, $"{expression} => {result.Error?.Code}: {result.Error?.Message}");
        Assert.False(result.IsArray, $"{expression} returned an array.");
        return result.ScalarValue;
    }

    private static decimal[] Arr(string expression, CalculatorLimits? limits = null)
    {
        ExpressionCalculator.CalculatorBatchResult result = Run(expression, limits);
        Assert.True(result.IsOk, $"{expression} => {result.Error?.Code}: {result.Error?.Message}");
        Assert.True(result.IsArray, $"{expression} returned a scalar.");
        return result.ArrayValues!.ToArray();
    }

    private static void Fails(string expression, CalculatorErrorCode expected, CalculatorLimits? limits = null)
    {
        ExpressionCalculator.CalculatorBatchResult result = Run(expression, limits);
        Assert.False(result.IsOk, $"{expression} unexpectedly succeeded.");
        Assert.Equal(expected, result.Error!.Code);
    }

    private static void Close(double expected, decimal actual, double tolerance)
        => Assert.True(Math.Abs(expected - (double)actual) <= tolerance, $"expected {expected} but was {actual} (tolerance {tolerance}).");

    private static void Close(double[] expected, decimal[] actual, double tolerance)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Close(expected[i], actual[i], tolerance);
        }
    }

    private static string Vector(IEnumerable<decimal> values)
        => "[" + string.Join(", ", values.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";

    // ---------------------------------------------------------------- Domain A

    [Fact]
    public void VectorFunctionsAcceptLengthsAboveSixteen()
    {
        string ones20 = Vector(Enumerable.Repeat(1m, 20));
        Assert.Equal(20m, Scalar($"vec_dot({ones20}, {ones20})"));
        Close(Math.Sqrt(20.0), Scalar($"vec_norm({ones20})"), TightTolerance);

        decimal[] unit = Arr($"vec_normalize({ones20})");
        Assert.Equal(20, unit.Length);
        Close(1.0 / Math.Sqrt(20.0), unit[0], TightTolerance);

        Assert.Equal(32m, Scalar("vec_dot([1, 2, 3], [4, 5, 6])"));
        Assert.Equal(5m, Scalar("vec_norm([3, 4])"));
        Close([0.6, 0.8], Arr("vec_normalize([3, 4])"), TightTolerance);
    }

    [Fact]
    public void VectorFunctionsReportContractErrors()
    {
        Fails("vec_dot([1, 2], [1, 2, 3])", CalculatorErrorCode.DimensionMismatch);
        Fails("vec_dot(1, [1])", CalculatorErrorCode.DomainError);
        Fails("vec_normalize([0, 0])", CalculatorErrorCode.DivisionByZero);
        Fails("vec_normalize([0.0000000000001, 0])", CalculatorErrorCode.DivisionByZero);
        Fails("vec_norm(3)", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void Cross3DMatchesRightHandRuleAndRequiresLengthThree()
    {
        Assert.Equal(new[] { 0m, 0m, 1m }, Arr("cross3d([1, 0, 0], [0, 1, 0])"));
        Assert.Equal(new[] { -3m, 6m, -3m }, Arr("cross3d([1, 2, 3], [4, 5, 6])"));
        Fails("cross3d([1, 2], [3, 4])", CalculatorErrorCode.DimensionMismatch);
    }

    [Fact]
    public void MatrixProductTransposeDeterminantAndInverse()
    {
        Assert.Equal(new[] { 1m, 4m, 2m, 5m, 3m, 6m }, Arr("mat_transpose([1, 2, 3, 4, 5, 6], 2, 3)"));
        Assert.Equal(new[] { 19m, 22m, 43m, 50m }, Arr("mat_mul([1, 2, 3, 4], 2, 2, [5, 6, 7, 8], 2, 2)"));
        Assert.Equal(new[] { 2m, 4m }, Arr("mat_mul([1, 2, 3, 4], 2, 2, [0, 1], 2, 1)"));
        Close(-2.0, Scalar("mat_det([1, 2, 3, 4], 2)"), TightTolerance);
        Assert.Equal(0m, Scalar("mat_det([1, 2, 2, 4], 2)"));

        Close([0.5, 0.0, 0.0, 0.25], Arr("mat_inv([2, 0, 0, 4], 2)"), TightTolerance);
        Close([1.0, 2.0], Arr("mat_solve([2, 0, 0, 4], 2, [2, 8])"), TightTolerance);

        Fails("mat_mul([1, 2, 3, 4], 2, 2, [1, 2, 3], 3, 1)", CalculatorErrorCode.DimensionMismatch);
        Fails("mat_mul([1, 2, 3, 4], 2, 2, [1, 2, 3], 2, 2)", CalculatorErrorCode.DimensionMismatch);
        Fails("mat_transpose([1, 2, 3], 2, 2)", CalculatorErrorCode.DimensionMismatch);
        Fails("mat_transpose([1, 2, 3, 4], 17, 1)", CalculatorErrorCode.DomainError);
        Fails("mat_transpose([1, 2, 3, 4], 1.5, 2)", CalculatorErrorCode.DomainError);
        Fails("mat_inv([1, 2, 2, 4], 2)", CalculatorErrorCode.SingularMatrix);
        Fails("mat_solve([1, 2, 2, 4], 2, [1, 2])", CalculatorErrorCode.SingularMatrix);
        Fails("mat_solve([2, 0, 0, 4], 2, [1, 2, 3])", CalculatorErrorCode.DimensionMismatch);
        Fails("mat_det([1, 2, 3], 2)", CalculatorErrorCode.DimensionMismatch);
    }

    [Fact]
    public void LeastSquaresSolvesOverdeterminedSystemAndRejectsUnderdetermined()
    {
        Close([1.0, 2.0], Arr("mat_lstsq([1, 0, 0, 1, 1, 1], 3, 2, [1, 2, 3])"), 1e-10);
        Fails("mat_lstsq([1, 2, 3, 4], 1, 4, [1])", CalculatorErrorCode.DomainError);
        Fails("mat_lstsq([1, 0, 0, 1, 1, 1], 3, 2, [1, 2])", CalculatorErrorCode.DimensionMismatch);
    }

    [Fact]
    public void CholeskyMapsNonPositiveDefiniteAndAsymmetricInputToDomainError()
    {
        Close([2.0, 0.0, 1.0, Math.Sqrt(2.0)], Arr("mat_chol([4, 2, 2, 3], 2)"), TightTolerance);
        Fails("mat_chol([1, 2, 2, 1], 2)", CalculatorErrorCode.DomainError);
        Fails("mat_chol([0, 0, 0, 0], 2)", CalculatorErrorCode.DomainError);
        Fails("mat_chol([1, 2, 3, 4], 2)", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void ThinQrReconstructsInputAndRejectsWideMatrices()
    {
        const string A = "[1, 2, 3, 4, 5, 7]";
        decimal[] q = Arr($"mat_qr_q({A}, 3, 2)");
        decimal[] r = Arr($"mat_qr_r({A}, 3, 2)");
        Assert.Equal(6, q.Length);
        Assert.Equal(4, r.Length);
        Assert.Equal(0m, r[2]);

        decimal[] product = Arr($"mat_mul(mat_qr_q({A}, 3, 2), 3, 2, mat_qr_r({A}, 3, 2), 2, 2)");
        Close([1, 2, 3, 4, 5, 7], product, 1e-10);
        Fails("mat_qr_q([1, 2, 3, 4], 1, 4)", CalculatorErrorCode.DomainError);
        Fails("mat_qr_r([1, 2, 3, 4], 1, 4)", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void SymmetricEigenValuesDescendAndVectorSignsAreDeterministic()
    {
        Close([3.0, 1.0], Arr("mat_eig_sym_vals([2, 1, 1, 2], 2)"), 1e-10);

        decimal[] vecs = Arr("mat_eig_sym_vecs([2, 1, 1, 2], 2)");
        double s = 1.0 / Math.Sqrt(2.0);
        Close([s, s, s, -s], vecs, 1e-10);

        // 3x3: V diag(lambda) V^T reproduces A and every column starts with a positive entry.
        double[] a = [4, 1, 0, 1, 3, 1, 0, 1, 2];
        decimal[] values = Arr("mat_eig_sym_vals([4, 1, 0, 1, 3, 1, 0, 1, 2], 3)");
        decimal[] v = Arr("mat_eig_sym_vecs([4, 1, 0, 1, 3, 1, 0, 1, 2], 3)");
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                double sum = 0.0;
                for (int k = 0; k < 3; k++)
                {
                    sum += (double)v[i * 3 + k] * (double)values[k] * (double)v[j * 3 + k];
                }

                Assert.Equal(a[i * 3 + j], sum, 9);
            }
        }

        AssertFirstSignificantEntryPositive(v, 3, 3);
        Fails("mat_eig_sym_vals([1, 2, 3, 4], 2)", CalculatorErrorCode.DomainError);
    }

    [Theory]
    [InlineData("[1, 2, 3, 4, 5, 6]", 3, 2)]
    [InlineData("[1, 2, 3, 4, 5, 6]", 2, 3)]
    [InlineData("[3, 0, 0, 4]", 2, 2)]
    [InlineData("[-1, 2, 2, -3, 0, 5]", 3, 2)]
    public void SvdReconstructsInputWithDescendingValuesAndCanonicalSigns(string matrix, int rows, int cols)
    {
        decimal[] s = Arr($"mat_svd_s({matrix}, {rows}, {cols})");
        decimal[] u = Arr($"mat_svd_u({matrix}, {rows}, {cols})");
        decimal[] vt = Arr($"mat_svd_vt({matrix}, {rows}, {cols})");
        int k = Math.Min(rows, cols);
        Assert.Equal(k, s.Length);
        Assert.Equal(rows * k, u.Length);
        Assert.Equal(k * cols, vt.Length);
        for (int i = 1; i < k; i++)
        {
            Assert.True(s[i - 1] >= s[i]);
        }

        decimal[] source = Arr(matrix);
        for (int i = 0; i < rows; i++)
        {
            for (int j = 0; j < cols; j++)
            {
                double sum = 0.0;
                for (int t = 0; t < k; t++)
                {
                    sum += (double)u[i * k + t] * (double)s[t] * (double)vt[t * cols + j];
                }

                Assert.Equal((double)source[i * cols + j], sum, 9);
            }
        }

        AssertFirstSignificantEntryPositive(u, rows, k);
        Fails($"mat_svd_s([1, 2, 3], {rows}, {cols})", CalculatorErrorCode.DimensionMismatch);
    }

    [Fact]
    public void Rotation2DReturnsRowMajorRotationMatrixAndBoundsAngle()
    {
        Assert.Equal(new[] { 1m, 0m, 0m, 1m }, Arr("mat_rot2d(0)"));
        Close([0.0, -1.0, 1.0, 0.0], Arr("mat_rot2d(pi / 2)"), 1e-12);
        Fails("mat_rot2d(1000000001)", CalculatorErrorCode.DomainError);
        Fails("mat_rot2d([1])", CalculatorErrorCode.DomainError);
    }

    // ---------------------------------------------------------------- Domain B

    [Theory]
    [InlineData("quad_gauss_chebyshev(1 + x, x, 1, 1)", Math.PI)]
    [InlineData("quad_gauss_chebyshev(1 + x, x, 2, 1)", Math.PI / 2.0)]
    [InlineData("quad_gauss_chebyshev(x^2, x, 1, 3)", Math.PI / 2.0)]
    [InlineData("quad_gauss_chebyshev(x^2, x, 2, 4)", Math.PI / 8.0)]
    public void ChebyshevQuadratureIsExactForLowDegreePolynomials(string expression, double expected)
        => Close(expected, Scalar(expression), IntegrationTolerance);

    [Theory]
    [InlineData("quad_gauss_laguerre(x, x, 0, 1)", 1.0)]
    [InlineData("quad_gauss_laguerre(x^2, x, 0, 2)", 2.0)]
    [InlineData("quad_gauss_laguerre(x^3, x, 0, 3)", 6.0)]
    [InlineData("quad_gauss_laguerre(x^3, x, 0, 4)", 6.0)]
    [InlineData("quad_gauss_laguerre(x, x, 1, 2)", 2.0)]
    [InlineData("quad_gauss_laguerre(x^2, x, 0.5, 5)", 3.3233509704478426)]
    public void LaguerreQuadratureCoversTableAndNonTableOrders(string expression, double expected)
        => Close(expected, Scalar(expression), 1e-8);

    [Theory]
    [InlineData("quad_gauss_hermite(1, x, 1)", 1.7724538509055159)]
    [InlineData("quad_gauss_hermite(x^2, x, 2)", 0.88622692545275801)]
    [InlineData("quad_gauss_hermite(x^4, x, 3)", 1.3293403881791370)]
    [InlineData("quad_gauss_hermite(x^4, x, 4)", 1.3293403881791370)]
    [InlineData("quad_gauss_hermite(x^6, x, 5)", 3.3233509704478426)]
    public void HermiteQuadratureCoversTableAndNonTableOrders(string expression, double expected)
        => Close(expected, Scalar(expression), 1e-8);

    [Theory]
    [InlineData("quad_gauss_jacobi(1 + x, x, 0, 0, 1)", 2.0)]
    [InlineData("quad_gauss_jacobi(x^2, x, 1, 0, 2)", 2.0 / 3.0)]
    [InlineData("quad_gauss_jacobi(1, x, 1, 1, 1)", 4.0 / 3.0)]
    [InlineData("quad_gauss_jacobi(x^3, x, 0, 0, 2)", 0.0)]
    public void JacobiQuadratureAcceptsOrderOne(string expression, double expected)
        => Close(expected, Scalar(expression), 1e-9);

    [Theory]
    [InlineData("quad_gauss_lobatto(x, x, 0, 1, 2)", 0.5)]
    [InlineData("quad_gauss_lobatto(x^3, x, 0, 1, 3)", 0.25)]
    [InlineData("quad_gauss_lobatto(x^5, x, 0, 1, 4)", 1.0 / 6.0)]
    [InlineData("quad_gauss_lobatto(x^7, x, 0, 1, 5)", 0.125)]
    [InlineData("quad_gauss_lobatto(x^9, x, 0, 1, 6)", 0.1)]
    [InlineData("quad_gauss_lobatto(x^13, x, 2, 3, 8)", 4766585.0 / 14.0)]
    public void LobattoQuadratureCoversTableAndNonTableOrders(string expression, double expected)
    {
        decimal actual = Scalar(expression);
        Assert.True(Math.Abs(expected - (double)actual) <= 1e-9 * Math.Max(1.0, Math.Abs(expected)), $"{expression}: {actual}");
    }

    [Fact]
    public void GaussianQuadratureRejectsOutOfContractArguments()
    {
        Fails("quad_gauss_chebyshev(x, x, 3, 4)", CalculatorErrorCode.DomainError);
        Fails("quad_gauss_chebyshev(x, x, 1, 0)", CalculatorErrorCode.DomainError);
        Fails("quad_gauss_chebyshev(x, x, 1, 129)", CalculatorErrorCode.DomainError);
        Fails("quad_gauss_laguerre(x, x, -1, 4)", CalculatorErrorCode.DomainError);
        Fails("quad_gauss_laguerre(x, x, 0, 65)", CalculatorErrorCode.DomainError);
        Fails("quad_gauss_hermite(x, x, 0)", CalculatorErrorCode.DomainError);
        Fails("quad_gauss_hermite(x, x, 1.5)", CalculatorErrorCode.DomainError);
        Fails("quad_gauss_jacobi(x, x, -1, 0, 3)", CalculatorErrorCode.DomainError);
        Fails("quad_gauss_jacobi(x, x, 0, 0, 0)", CalculatorErrorCode.DomainError);
        Fails("quad_gauss_lobatto(x, x, 1, 1, 5)", CalculatorErrorCode.DomainError);
        Fails("quad_gauss_lobatto(x, x, 2, 1, 5)", CalculatorErrorCode.DomainError);
        Fails("quad_gauss_lobatto(x, x, 0, 1, 1)", CalculatorErrorCode.DomainError);
        Fails("quad_gauss_lobatto(x, x, 0, 1, 65)", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void OdeSolversIntegrateScalarAndVectorSystems()
    {
        decimal[] rk4 = Arr("ode_rk4([y], t, [y], 0, 1, [1], 100)", Heavy);
        Assert.Single(rk4);
        Close(Math.E, rk4[0], 1e-8);

        decimal[] rk45 = Arr("ode_rk45([y], t, [y], 0, 1, [1])", Heavy);
        Close(Math.E, rk45[0], 1e-6);
        decimal[] rk45Tight = Arr("ode_rk45([y], t, [y], 0, 1, [1], 0.0000000001)", Heavy);
        Close(Math.E, rk45Tight[0], 1e-9);

        // Harmonic oscillator u' = v, v' = -u: (u, v)(pi) = (-1, 0). The right-hand sides use the declared t and state names.
        decimal[] oscillator = Arr("ode_rk4([v, -u], t, [u, v], 0, pi, [1, 0], 200)", Heavy);
        Close([-1.0, 0.0], oscillator, 1e-7);

        // y' = 2 t integrates to t^2 and the independent variable is bound inside every right-hand side.
        Close([1.0], Arr("ode_rk4([2 * t], t, [y], 0, 1, [0], 4)", Heavy), 1e-12);
        Close([0.0], Arr("ode_rk4([2 * t], t, [y], 1, 0, [1], 4)", Heavy), 1e-12);
    }

    [Fact]
    public void OdeSolversReturnInitialStateWhenIntervalIsEmptyAfterValidation()
    {
        Assert.Equal(new[] { 5m, 6m }, Arr("ode_rk4([a, b], t, [a, b], 2, 2, [5, 6], 10)", Heavy));
        Assert.Equal(new[] { 5m }, Arr("ode_rk45([y], t, [y], 3, 3, [5])", Heavy));

        // Validation precedes the shortcut.
        Fails("ode_rk4([y], t, [y], 2, 2, [5], 0)", CalculatorErrorCode.DomainError, Heavy);
        Fails("ode_rk4([y], t, [y], 2, 2, [5], 2001)", CalculatorErrorCode.DomainError, Heavy);
        Fails("ode_rk45([y], t, [y], 2, 2, [5], 0.1)", CalculatorErrorCode.DomainError, Heavy);
        Fails("ode_rk45([y], t, [y], 2, 2, [5], 0.0000000000001)", CalculatorErrorCode.DomainError, Heavy);
        Fails("ode_rk4([y, y], t, [y], 2, 2, [5], 10)", CalculatorErrorCode.DimensionMismatch, Heavy);
        Fails("ode_rk4([y], t, [y], 2, 2, [5, 6], 10)", CalculatorErrorCode.DimensionMismatch, Heavy);
    }

    [Fact]
    public void OdeSolversValidateStructureAndPreserveBodyErrors()
    {
        Fails("ode_rk4([y], y, [y], 0, 1, [1], 10)", CalculatorErrorCode.DomainError, Heavy);
        Fails("ode_rk4([a, b], t, [a, a], 0, 1, [1, 2], 10)", CalculatorErrorCode.DomainError, Heavy);
        Fails("ode_rk4(y, t, [y], 0, 1, [1], 10)", CalculatorErrorCode.DomainError, Heavy);
        Fails("ode_rk4([y], t, y, 0, 1, [1], 10)", CalculatorErrorCode.DomainError, Heavy);
        Fails("ode_rk4([y], t, [y], 0, 1, 1, 10)", CalculatorErrorCode.DomainError, Heavy);
        Fails("ode_rk4([y], 3, [y], 0, 1, [1], 10)", CalculatorErrorCode.DomainError, Heavy);

        // A right-hand side that names an unknown variable keeps its own error code.
        Fails("ode_rk4([z], t, [y], 0, 1, [1], 10)", CalculatorErrorCode.UndefinedName, Heavy);
        Fails("ode_rk4([1 / (y - 1)], t, [y], 0, 1, [1], 10)", CalculatorErrorCode.DivisionByZero, Heavy);

        // When the enclosing call has a wrong argument count, the nested ODE's declared t / state names must not be reported as undefined.
        Fails("abs(1, ode_rk4([y], t, [y], 0, 1, [1], 10))", CalculatorErrorCode.WrongArgumentCount, Heavy);
        Fails("abs(1, ode_rk45([y], t, [y], 0, 1, [1]))", CalculatorErrorCode.WrongArgumentCount, Heavy);
        Fails("abs(1, ode_rk4([y], t, [y], 0, 1, [w], 10))", CalculatorErrorCode.UndefinedName, Heavy);
    }

    [Fact]
    public void OdeSolverSupportsEightStatesAndTheWiderBoundVariableBuffer()
    {
        decimal[] state = Arr(
            "ode_rk4([0, 0, 0, 0, 0, 0, 0, 0], t, [y1, y2, y3, y4, y5, y6, y7, y8], 0, 1, [1, 2, 3, 4, 5, 6, 7, 8], 2)",
            Heavy);
        Assert.Equal(new[] { 1m, 2m, 3m, 4m, 5m, 6m, 7m, 8m }, state);

        Fails(
            "ode_rk4([0, 0, 0, 0, 0, 0, 0, 0, 0], t, [y1, y2, y3, y4, y5, y6, y7, y8, y9], 0, 1, [1, 2, 3, 4, 5, 6, 7, 8, 9], 2)",
            CalculatorErrorCode.DomainError,
            Heavy);

        var scope = default(BoundVariableScope);
        for (int i = 0; i < BoundVariableScope.MaxBoundVariables; i++)
        {
            Assert.True(scope.TryPush($"v{i}", i, out BoundVariableScope next));
            scope = next;
        }

        Assert.Equal(32, scope.Count);
        Assert.False(scope.TryPush("overflow", 0m, out _));
        Assert.True(scope.TryGetValue("v31", out decimal last));
        Assert.Equal(31m, last);
    }

    // ---------------------------------------------------------------- Domain C

    [Fact]
    public void CubicRootsAreRealOnlyAscendingWithMultiplicity()
    {
        Close([1.0, 2.0, 3.0], Arr("solve_cubic(1, -6, 11, -6)"), 1e-9);
        Close([1.0, 1.0, 1.0], Arr("solve_cubic(1, -3, 3, -1)"), SolverTolerance);
        Close([1.0, 1.0, 2.0], Arr("solve_cubic(1, -4, 5, -2)"), SolverTolerance);
        Close([1.0, 2.0, 2.0], Arr("solve_cubic(1, -5, 8, -4)"), SolverTolerance);
        Close([1.0], Arr("solve_cubic(1, 0, 0, -1)"), 1e-9);
        Close([1.0, 2.0, 3.0], Arr("solve_cubic(2, -12, 22, -12)"), 1e-9);
    }

    [Fact]
    public void CubicAndQuarticRejectDegenerateLeadingCoefficient()
    {
        Fails("solve_cubic(0, 1, 2, 3)", CalculatorErrorCode.DomainError);
        Fails("solve_cubic(0.000000000001, 1, 2, 3)", CalculatorErrorCode.DomainError);
        Fails("solve_cubic(0.0000000001, 1, 1000, 3)", CalculatorErrorCode.DomainError);
        Fails("solve_quartic(0, 1, 2, 3, 4)", CalculatorErrorCode.DomainError);
        Fails("solve_quartic(0.000000000001, 1, 2, 3, 4)", CalculatorErrorCode.DomainError);
        Fails("solve_cubic([1], 1, 2, 3)", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void QuarticRootsAreRealOnlyAscendingWithMultiplicity()
    {
        Close([1.0, 2.0, 3.0, 4.0], Arr("solve_quartic(1, -10, 35, -50, 24)"), 1e-8);
        Close([-2.0, -1.0, 1.0, 2.0], Arr("solve_quartic(1, 0, -5, 0, 4)"), 1e-9);
        Close([-1.0, -1.0, 1.0, 1.0], Arr("solve_quartic(1, 0, -2, 0, 1)"), SolverTolerance);
        Close([0.0, 0.0, 0.0, 0.0], Arr("solve_quartic(1, 0, 0, 0, 0)"), SolverTolerance);
        Assert.Empty(Arr("solve_quartic(1, 0, 0, 0, 1)"));
        Close([-1.0, 1.0], Arr("solve_quartic(1, 0, 0, 0, -1)"), 1e-9);
    }

    [Theory]
    [InlineData("c_mul_re(1, 2, 3, 4)", -5.0)]
    [InlineData("c_mul_im(1, 2, 3, 4)", 10.0)]
    [InlineData("c_div_re(1, 2, 3, 4)", 0.44)]
    [InlineData("c_div_im(1, 2, 3, 4)", 0.08)]
    [InlineData("c_ln_re(3, 4)", 1.6094379124341003)]
    [InlineData("c_ln_im(3, 4)", 0.9272952180016122)]
    [InlineData("c_sqrt_re(3, 4)", 2.0)]
    [InlineData("c_sqrt_im(3, 4)", 1.0)]
    [InlineData("c_sqrt_re(-4, 0)", 0.0)]
    [InlineData("c_sqrt_im(-4, 0)", 2.0)]
    [InlineData("c_pow_re(0, 0, 2, 5)", 0.0)]
    [InlineData("c_pow_im(0, 0, 2, 5)", 0.0)]
    [InlineData("c_pow_re(0, 0, 0, 0)", 1.0)]
    [InlineData("c_pow_im(0, 0, 0, 0)", 0.0)]
    [InlineData("c_pow_re(0, 1, 2, 0)", -1.0)]
    [InlineData("c_pow_re(2, 0, 3, 0)", 8.0)]
    [InlineData("c_pow_im(2, 0, 3, 0)", 0.0)]
    public void ComplexProjectionsMatchClosedForms(string expression, double expected)
        => Close(expected, Scalar(expression), 1e-12);

    [Fact]
    public void ComplexProjectionsReportContractErrors()
    {
        Fails("c_div_re(1, 1, 0, 0)", CalculatorErrorCode.DivisionByZero);
        Fails("c_div_im(1, 1, 0, 0)", CalculatorErrorCode.DivisionByZero);
        Fails("c_ln_re(0, 0)", CalculatorErrorCode.DomainError);
        Fails("c_pow_re(0, 0, -1, 0)", CalculatorErrorCode.DomainError);
        Fails("c_pow_im(0, 0, 0, 1)", CalculatorErrorCode.DomainError);
        Fails("c_mul_re([1], 2, 3, 4)", CalculatorErrorCode.DomainError);
        Fails("c_mul_re(79228162514264337593543950335, 0, 2, 0)", CalculatorErrorCode.ArithmeticOverflow);
    }

    // ---------------------------------------------------------------- Domain D

    [Fact]
    public void OneDimensionalMinimizersFindTheMinimum()
    {
        Close(2.0, Scalar("opt_brent((x - 2)^2 + 1, x, 0, 5)", Heavy), SolverTolerance);
        Close(2.0, Scalar("opt_golden((x - 2)^2 + 1, x, 0, 5)", Heavy), SolverTolerance);
        Close(1.0, Scalar("opt_brent(x^4 - 4 * x^3 + 6 * x^2 - 4 * x + 1 + (x - 1)^2, x, -2, 4, 0.0000001)", Heavy), 1e-3);

        // Boundary minimum of a monotone function.
        Close(0.0, Scalar("opt_brent(x, x, 0, 1)", Heavy), 1e-6);
        Close(0.0, Scalar("opt_golden(x, x, 0, 1)", Heavy), 1e-6);
    }

    [Fact]
    public void OneDimensionalMinimizersValidateArguments()
    {
        Fails("opt_brent(x^2, x, 1, 1)", CalculatorErrorCode.DomainError);
        Fails("opt_brent(x^2, x, 2, 1)", CalculatorErrorCode.DomainError);
        Fails("opt_golden(x^2, x, 2, 1)", CalculatorErrorCode.DomainError);
        Fails("opt_brent(x^2, x, 0, 1, 0.1)", CalculatorErrorCode.DomainError);
        Fails("opt_golden(x^2, x, 0, 1, 0.0000000000001)", CalculatorErrorCode.DomainError);
        Fails("opt_brent(z, x, 0, 1)", CalculatorErrorCode.UndefinedName, Heavy);
        Fails("opt_golden(z, x, 0, 1)", CalculatorErrorCode.UndefinedName, Heavy);
        Fails("opt_brent(x^2, 3, 0, 1)", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void NelderMeadAndBfgsFindMultivariateMinima()
    {
        Close([1.0, 2.0], Arr("opt_nelder_mead((x - 1)^2 + (y - 2)^2, [x, y], [0, 0])", Heavy), 1e-5);
        Close([3.0, -1.0], Arr("opt_bfgs((x - 3)^2 + 2 * (y + 1)^2, [x, y], [0, 0])", Heavy), 1e-6);
        Close([2.0], Arr("opt_nelder_mead((x - 2)^2, [x], [10], 0.000001)", Heavy), 1e-3);
        Close([2.0], Arr("opt_bfgs((x - 2)^4 + (x - 2)^2, [x], [10])", Heavy), 1e-4);

        // Coupled quadratic: minimum of x^2 + x*y + y^2 - 3x is (2, -1).
        Close([2.0, -1.0], Arr("opt_bfgs(x^2 + x * y + y^2 - 3 * x, [x, y], [5, 5])", Heavy), 1e-5);
        Close([2.0, -1.0], Arr("opt_nelder_mead(x^2 + x * y + y^2 - 3 * x, [x, y], [5, 5])", Heavy), 1e-4);
    }

    [Fact]
    public void MultivariateMinimizersValidateArguments()
    {
        Fails("opt_nelder_mead(x^2, [x], [1, 2])", CalculatorErrorCode.DimensionMismatch, Heavy);
        Fails("opt_bfgs(x^2 + y^2, [x, y], [1])", CalculatorErrorCode.DimensionMismatch, Heavy);
        Fails("opt_bfgs(x^2, [x], 1)", CalculatorErrorCode.DomainError, Heavy);
        Fails("opt_bfgs(x^2, x, [1])", CalculatorErrorCode.DomainError, Heavy);
        Fails("opt_nelder_mead(x^2 + y^2, [x, x], [1, 2])", CalculatorErrorCode.DomainError, Heavy);
        Fails("opt_nelder_mead(x^2, [x], [1], 0.1)", CalculatorErrorCode.DomainError, Heavy);
        Fails("opt_bfgs(x^2, [x], [1], 0.0000000000001)", CalculatorErrorCode.DomainError, Heavy);
        Fails(
            "opt_nelder_mead(a + b + c + d + e1 + f + g + h + i, [a, b, c, d, e1, f, g, h, i], [1, 1, 1, 1, 1, 1, 1, 1, 1])",
            CalculatorErrorCode.DomainError,
            Heavy);
        Fails("opt_nelder_mead(z, [x], [1])", CalculatorErrorCode.UndefinedName, Heavy);
    }

    // ---------------------------------------------------------------- Domain F

    [Fact]
    public void ConditionalFunctionsSelectBranchesInDecimal()
    {
        Assert.Equal(10m, Scalar("if_gt(2, 1, 10, 20)"));
        Assert.Equal(20m, Scalar("if_gt(1, 1, 10, 20)"));
        Assert.Equal(10m, Scalar("if_ge(1, 1, 10, 20)"));
        Assert.Equal(20m, Scalar("if_ge(0.9999999999999999999999999999, 1, 10, 20)"));
        Assert.Equal(5m, Scalar("if_eq(1, 1, 5, 6)"));
        Assert.Equal(6m, Scalar("if_eq(1, 1.0000000000000000000000000001, 5, 6)"));
        Assert.Equal(5m, Scalar("if_eq(1, 1.05, 5, 6, 0.1)"));
        Assert.Equal(6m, Scalar("if_eq(1, 1.05, 5, 6, 0.01)"));
        Assert.Equal(5m, Scalar("if_eq(1, 1.05, 5, 6, 0.05)"));
        Assert.Equal(0m, Scalar("step(-0.0001)"));
        Assert.Equal(1m, Scalar("step(0)"));
        Assert.Equal(1m, Scalar("step(7)"));
        Fails("if_eq(1, 1, 5, 6, -0.1)", CalculatorErrorCode.DomainError);
        Fails("if_gt([1], 1, 5, 6)", CalculatorErrorCode.DomainError);
        Fails("if_eq(-79228162514264337593543950335, 79228162514264337593543950335, 5, 6, 1)", CalculatorErrorCode.ArithmeticOverflow);
    }

    [Fact]
    public void ZipScaleAndCumulativeArrayOperationsAreExactDecimal()
    {
        Assert.Equal(new[] { 4m, 5m }, Arr("arr_zip_sub([5, 7], [1, 2])"));
        Assert.Equal(new[] { 2m, 2m }, Arr("arr_zip_div([6, 8], [3, 4])"));
        Assert.Equal(new[] { 0.5m, 0.25m }, Arr("arr_zip_div([1, 1], [2, 4])"));
        Assert.Equal(new[] { 2.5m, 5m, 7.5m }, Arr("arr_scale([1, 2, 3], 2.5)"));
        Assert.Equal(new[] { 1m, 3m, 6m, 10m }, Arr("arr_cumsum([1, 2, 3, 4])"));
        Assert.Equal(new[] { 1m, 2m, 6m, 24m }, Arr("arr_cumprod([1, 2, 3, 4])"));
        Assert.Equal(new[] { 0.3m }, Arr("arr_cumsum([0.1, 0.2])").Skip(1).ToArray());

        Fails("arr_zip_sub([1, 2], [1])", CalculatorErrorCode.DimensionMismatch);
        Fails("arr_zip_div([1, 2], [1])", CalculatorErrorCode.DimensionMismatch);
        Fails("arr_zip_div([1, 2], [1, 0])", CalculatorErrorCode.DivisionByZero);
        Fails("arr_zip_sub(1, [1])", CalculatorErrorCode.DomainError);
        Fails("arr_scale(2, [1])", CalculatorErrorCode.DomainError);
        Fails("arr_scale([1], [2])", CalculatorErrorCode.DomainError);
        Fails("arr_cumprod([79228162514264337593543950335, 2])", CalculatorErrorCode.ArithmeticOverflow);
        Fails("arr_cumsum([79228162514264337593543950335, 1])", CalculatorErrorCode.ArithmeticOverflow);
        Fails("arr_scale([79228162514264337593543950335], 2)", CalculatorErrorCode.ArithmeticOverflow);
        Fails("arr_cumsum(3)", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void SortRankAndArgExtremaFollowTheContract()
    {
        Assert.Equal(new[] { 1m, 2m, 3m }, Arr("arr_sort([3, 1, 2])"));
        Assert.Equal(new[] { 3m, 2m, 1m }, Arr("arr_sort([3, 1, 2], 1)"));
        Assert.Equal(new[] { 1m, 4m, 2.5m, 2.5m }, Arr("arr_rank([10, 30, 20, 20])"));
        Assert.Equal(new[] { 4m, 1m, 2.5m, 2.5m }, Arr("arr_rank([10, 30, 20, 20], 1)"));
        Assert.Equal(new[] { 2m, 2m, 2m }, Arr("arr_rank([7, 7, 7])"));
        Assert.Equal(1m, Scalar("arr_argmax([1, 5, 5, 2])"));
        Assert.Equal(1m, Scalar("arr_argmin([3, 1, 1])"));
        Assert.Equal(0m, Scalar("arr_argmax([9])"));

        Fails("arr_sort([3, 1], 2)", CalculatorErrorCode.DomainError);
        Fails("arr_sort([3, 1], 0.5)", CalculatorErrorCode.DomainError);
        Fails("arr_rank([3, 1], -1)", CalculatorErrorCode.DomainError);
        Fails("arr_sort(3)", CalculatorErrorCode.DomainError);
        Fails("arr_argmax(3)", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void ArrayReturningFunctionsComposeAsArrayArguments()
    {
        // A function result must be recognised as an array when it feeds an array-lowered variadic function.
        Assert.Equal(10m, Scalar("sum(arr_cumsum([1, 2, 3]))"));
        Assert.Equal(14m, Scalar("max(arr_scale(arr_sort([4, 1, 7]), 2))"));
        Assert.Equal(28m, Scalar("vec_dot(arr_zip_sub([5, 7, 9], [1, 2, 3]), [1, 1, 1]) + 13"));
    }

    // ---------------------------------------------------------------- Kernels & non-destructive contract

    [Fact]
    public void GeneralVectorKernelsHonourTheDslArrayBound()
    {
        var budget = DslExecutionBudget.CreateDefault();
        double[] ones = Enumerable.Repeat(1.0, DslArrayArena.MaxArrayLength).ToArray();
        MathResult<double> dot = DslLinearAlgebraMath.VecDotGeneral(ones, ones, ref budget);
        Assert.True(dot.IsOk);
        Assert.Equal(512.0, dot.Value);
        Assert.Equal(DslExecutionBudget.DefaultMaxOps - DslArrayArena.MaxArrayLength / 16, budget.RemainingOps);

        double[] tooLong = new double[DslArrayArena.MaxArrayLength + 1];
        Assert.Equal(MathErrorCode.DimensionMismatch, DslLinearAlgebraMath.VecDotGeneral(tooLong, tooLong, ref budget).Error);
        Assert.Equal(MathErrorCode.DomainError, DslLinearAlgebraMath.VecDotGeneral([], [], ref budget).Error);
        Assert.Equal(MathErrorCode.DimensionMismatch, DslLinearAlgebraMath.VecDotGeneral([1.0], [1.0, 2.0], ref budget).Error);
        Assert.Equal(MathErrorCode.DomainError, DslLinearAlgebraMath.VecNormGeneral([]).Error);
        Assert.Equal(MathErrorCode.NonFiniteResult, DslLinearAlgebraMath.VecNormGeneral([double.NaN]).Error);
        Assert.Equal(MathErrorCode.DivisionByZero, DslLinearAlgebraMath.VecNormalizeGeneral([1e-12, 0.0], new double[2]));
    }

    [Fact]
    public void ExistingKernelsKeepTheirOriginalContracts()
    {
        var budget = DslExecutionBudget.CreateDefault();

        // Dimension-limited vector kernels still stop at 16.
        double[] seventeen = Enumerable.Repeat(1.0, 17).ToArray();
        Assert.Equal(MathErrorCode.DimensionMismatch, DslLinearAlgebraMath.VecDot(seventeen, seventeen).Error);
        Assert.Equal(MathErrorCode.DimensionMismatch, DslLinearAlgebraMath.VecNorm(seventeen).Error);

        // Original quadrature order sets are unchanged.
        Assert.Equal(MathErrorCode.DomainError, DslQuadratureMath.QuadGaussChebyshev(new ConstantFunc(), 1, 1, ref budget).Error);
        Assert.Equal(MathErrorCode.DomainError, DslQuadratureMath.QuadGaussLaguerre(new ConstantFunc(), 0.0, 3, ref budget).Error);
        Assert.Equal(MathErrorCode.DomainError, DslQuadratureMath.QuadGaussHermite(new ConstantFunc(), 3, ref budget).Error);
        Assert.Equal(MathErrorCode.DomainError, DslQuadratureMath.QuadGaussJacobi(new ConstantFunc(), 0.0, 0.0, 1, ref budget).Error);
        Assert.Equal(MathErrorCode.DomainError, DslQuadratureMath.QuadGaussLobatto(new ConstantFunc(), 0.0, 1.0, 6, ref budget).Error);

        // Original ODE kernels still reject an empty interval; the DSL layer answers that case.
        double[] y0 = [1.0];
        double[] y1 = new double[1];
        Assert.Equal(MathErrorCode.DomainError, DslNumericalAnalysisMath.OdeRk4(new DecayOde(), 1.0, 1.0, y0, 10, y1, ref budget));
        Assert.Equal(MathErrorCode.DomainError, DslNumericalAnalysisMath.OdeRk45(new DecayOde(), 1.0, 1.0, y0, 1e-8, y1, ref budget));

        // Original polynomial solvers keep their lower-degree fallback and distinct-root reporting.
        Span<decimal> roots = stackalloc decimal[4];
        Assert.Equal(MathErrorCode.None, DslElementaryMath.SolveCubic(0m, 1m, -3m, 2m, roots, out int fallbackCount));
        Assert.Equal(2, fallbackCount);
        Assert.Equal(MathErrorCode.None, DslElementaryMath.SolveCubic(1m, -3m, 3m, -1m, roots, out int tripleCount));
        Assert.Equal(1, tripleCount);
        Assert.Equal(MathErrorCode.None, DslElementaryMath.SolveQuartic(1m, 0m, -2m, 0m, 1m, roots, out int distinctCount));
        Assert.Equal(2, distinctCount);

        // Original complex power keeps its z = 0 restriction.
        Assert.Equal(
            MathErrorCode.DomainError,
            DslElementaryMath.ComplexPow(new DslComplex(0m, 0m), new DslComplex(2m, 5m)).Error);

        // Original array zips keep rejecting empty input shape rules (non-empty path unchanged).
        Assert.Equal(new[] { 4m, 6m }, Arr("arr_zip_add([1, 2], [3, 4])"));
        Assert.Equal(new[] { 3m, 8m }, Arr("arr_zip_mul([1, 2], [3, 4])"));
    }

    [Fact]
    public void DecimalSolversExposeMultiplicityDirectly()
    {
        Span<decimal> roots = stackalloc decimal[4];
        Assert.Equal(MathErrorCode.DomainError, DslElementaryMath.SolveCubicDsl(0m, 1m, 2m, 3m, roots, out _));
        Assert.Equal(MathErrorCode.DomainError, DslElementaryMath.SolveQuarticDsl(0.000000000001m, 1m, 2m, 3m, 4m, roots, out _));
        Assert.Equal(MathErrorCode.DimensionMismatch, DslElementaryMath.SolveCubicDsl(1m, 2m, 3m, 4m, roots.Slice(0, 2), out _));

        Assert.Equal(MathErrorCode.None, DslElementaryMath.SolveCubicDsl(1m, -3m, 3m, -1m, roots, out int count));
        Assert.Equal(3, count);
        for (int i = 0; i < count; i++)
        {
            Assert.True(Math.Abs((double)roots[i] - 1.0) < 1e-6);
        }
    }

    private static void AssertFirstSignificantEntryPositive(decimal[] matrix, int rows, int columns)
    {
        for (int j = 0; j < columns; j++)
        {
            for (int r = 0; r < rows; r++)
            {
                decimal entry = matrix[r * columns + j];
                if (Math.Abs(entry) <= 1e-12m)
                {
                    continue;
                }

                Assert.True(entry > 0m, $"column {j} starts with {entry}");
                break;
            }
        }
    }

    private readonly struct ConstantFunc : IScalarFunction
    {
        public bool TryEvaluate(double x, ref DslExecutionBudget budget, out double y)
        {
            y = 1.0;
            return true;
        }
    }

    private readonly struct DecayOde : IOdeSystem
    {
        public int Dimension => 1;

        public bool TryEvaluate(double t, scoped ReadOnlySpan<double> y, scoped Span<double> dydt, ref DslExecutionBudget budget)
        {
            dydt[0] = -y[0];
            return true;
        }
    }
}
