#nullable enable
using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Calculator;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

public sealed class CalculatorDslV6Domain4And5Tests
{
    private static readonly IReadOnlyDictionary<string, decimal> EmptyScalars = new Dictionary<string, decimal>(StringComparer.Ordinal);

    private static ExpressionCalculator.CalculatorBatchResult EvalBatchOne(string expr)
    {
        IReadOnlyList<ExpressionCalculator.CalculatorBatchResult> results = ExpressionCalculator.EvaluateBatch(
            EmptyScalars,
            null,
            new[] { new ExpressionCalculator.CalculatorBatchItem("r", expr) },
            CalculatorLimits.Default);
        return results[0];
    }

    // ---- Domain 4: Extended Linear Algebra ----

    [Fact]
    public void LuDecomposition_SatisfiesPAEqualsLU()
    {
        IReadOnlyList<ExpressionCalculator.CalculatorBatchResult> res = ExpressionCalculator.EvaluateBatch(
            EmptyScalars,
            new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal)
            {
                ["A"] = new[] { 2m, 3m, 4m, 1m },
            },
            new[]
            {
                new ExpressionCalculator.CalculatorBatchItem("L", "mat_lu_l(A, 2)"),
                new ExpressionCalculator.CalculatorBatchItem("U", "mat_lu_u(A, 2)"),
                new ExpressionCalculator.CalculatorBatchItem("P", "mat_lu_p(A, 2)"),
                new ExpressionCalculator.CalculatorBatchItem("LU", "mat_mul(L, 2, 2, U, 2, 2)"),
            },
            CalculatorLimits.Default);

        Assert.All(res, r => Assert.True(r.IsOk, r.Error?.Message));
        Assert.Equal(new[] { 1m, 0m }, res[2].ArrayValues);
        // P*A for perm=[1,0] is row 1 then row 0 = [4, 1, 2, 3]
        Assert.Equal(4m, res[3].ArrayValues![0]);
        Assert.Equal(1m, res[3].ArrayValues![1]);
        Assert.Equal(2m, res[3].ArrayValues![2]);
        Assert.Equal(3m, res[3].ArrayValues![3]);
    }

    [Fact]
    public void GeneralEigenvalues_SortCanonicallyWithConjugatePairs()
    {
        // Rotation by 90 degrees: [[0, -1], [1, 0]] -> eigenvalues 0 - 1i, 0 + 1i
        ExpressionCalculator.CalculatorBatchResult re = EvalBatchOne("mat_eig_re([0, -1, 1, 0], 2)");
        ExpressionCalculator.CalculatorBatchResult im = EvalBatchOne("mat_eig_im([0, -1, 1, 0], 2)");

        Assert.True(re.IsOk, re.Error?.Message);
        Assert.True(im.IsOk, im.Error?.Message);
        Assert.Equal(new[] { 0m, 0m }, re.ArrayValues);
        Assert.Equal(new[] { -1m, 1m }, im.ArrayValues);
    }

    [Fact]
    public void PinvRankTraceNormExpmAndKron_MatchExactMath()
    {
        // Rank-deficient 2x2 matrix [[1, 2], [2, 4]] -> rank 1, spectral norm = 5
        Assert.Equal(1m, EvalBatchOne("mat_rank([1, 2, 2, 4], 2, 2)").ScalarValue);
        Assert.Equal(5m, EvalBatchOne("mat_trace([2, 9, -1, 3], 2)").ScalarValue);
        Assert.Equal(5m, EvalBatchOne("mat_norm([3, 0, 0, 4], 2, 2, 0)").ScalarValue);
        Assert.Equal(4m, EvalBatchOne("mat_norm([3, 0, 0, 4], 2, 2, 1)").ScalarValue);
        Assert.Equal(4m, EvalBatchOne("mat_norm([3, 0, 0, 4], 2, 2, 2)").ScalarValue);
        Assert.InRange(EvalBatchOne("mat_norm([1, 2, 2, 4], 2, 2, 3)").ScalarValue, 4.999999m, 5.000001m);

        // Pseudoinverse of [1, 0, 0, 0] is [1, 0, 0, 0]
        ExpressionCalculator.CalculatorBatchResult pinv = EvalBatchOne("mat_pinv([1, 0, 0, 0], 2, 2)");
        Assert.True(pinv.IsOk);
        Assert.Equal(new[] { 1m, 0m, 0m, 0m }, pinv.ArrayValues);

        // Zero matrix pinv & rank
        Assert.Equal(0m, EvalBatchOne("mat_rank([0, 0, 0, 0], 2, 2)").ScalarValue);
        Assert.Equal(new[] { 0m, 0m, 0m, 0m }, EvalBatchOne("mat_pinv([0, 0, 0, 0], 2, 2)").ArrayValues);

        // expm([[0, 0], [0, 0]]) = I_2, expm([[1, 0], [0, 2]]) = [[e, 0], [0, e^2]]
        ExpressionCalculator.CalculatorBatchResult expm = EvalBatchOne("mat_expm([1, 0, 0, 2], 2)");
        Assert.True(expm.IsOk, expm.Error?.Message);
        Assert.InRange(expm.ArrayValues![0], 2.71828m, 2.71829m);
        Assert.Equal(0m, expm.ArrayValues![1]);
        Assert.Equal(0m, expm.ArrayValues![2]);
        Assert.InRange(expm.ArrayValues![3], 7.38905m, 7.38906m);

        // Large negative diagonal -> exact underflow to 0 without NaN or overflow; excessive norm (s > 30) -> DOMAIN_ERROR
        ExpressionCalculator.CalculatorBatchResult expmUnder = EvalBatchOne("mat_expm([-10000000000, 0, 0, -10000000000], 2)");
        Assert.True(expmUnder.IsOk, expmUnder.Error?.Message);
        Assert.Equal(new[] { 0m, 0m, 0m, 0m }, expmUnder.ArrayValues);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("mat_expm([0, 1000000000000, 0, 0], 2)").Error!.Code);

        // Dimension enforcement (>16 rejected for DslLinearAlgebraMath wrappers)
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("mat_norm(arr_ones(289), 17, 17, 0)").Error!.Code);

        // Kronecker product
        ExpressionCalculator.CalculatorBatchResult kron = EvalBatchOne("mat_kron([1, 2], 1, 2, [3, 4], 2, 1)");
        Assert.True(kron.IsOk, kron.Error?.Message);
        Assert.Equal(new[] { 3m, 6m, 4m, 8m }, kron.ArrayValues);
    }

    // ---- Domain 5: Optimization, fsolve, Polynomials, BDF-2 & 2D Interpolation ----

    [Fact]
    public void OptQpAndLbfgsb_FindExactBoxConstrainedMinima()
    {
        // Minimize 0.5*(x0^2 + x1^2) - 3*x0 - 3*x1 s.t. 0 <= x0 <= 2, 0 <= x1 <= 4 -> (2, 3)
        ExpressionCalculator.CalculatorBatchResult qp = EvalBatchOne("opt_qp([1, 0, 0, 1], [-3, -3], 2, [0, 0], [2, 4], 1e-10)");
        Assert.True(qp.IsOk, qp.Error?.Message);
        Assert.InRange(qp.ArrayValues![0], 1.999999m, 2.000001m);
        Assert.InRange(qp.ArrayValues![1], 2.999999m, 3.000001m);

        // Scale invariance across large (1e12) and tiny (1e-12) quadratic objectives
        ExpressionCalculator.CalculatorBatchResult qpLarge = EvalBatchOne("opt_qp([1000000000000, 0, 0, 1000000000000], [-3000000000000, -3000000000000], 2, [0, 0], [2, 4], 1e-10)");
        ExpressionCalculator.CalculatorBatchResult qpSmall = EvalBatchOne("opt_qp([0.000000000001, 0, 0, 0.000000000001], [-0.000000000003, -0.000000000003], 2, [0, 0], [2, 4], 1e-10)");
        Assert.True(qpLarge.IsOk, qpLarge.Error?.Message);
        Assert.True(qpSmall.IsOk, qpSmall.Error?.Message);
        Assert.InRange(qpLarge.ArrayValues![0], 1.999999m, 2.000001m);
        Assert.InRange(qpLarge.ArrayValues![1], 2.999999m, 3.000001m);
        Assert.InRange(qpSmall.ArrayValues![0], 1.999999m, 2.000001m);
        Assert.InRange(qpSmall.ArrayValues![1], 2.999999m, 3.000001m);

        // Reject indefinite H
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("opt_qp([1, 0, 0, -1], [0, 0], 2, [0, 0], [1, 1])").Error!.Code);

        // Minimize (x - 5)^2 + (y + 2)^2 s.t. x in [0, 3], y in [-1, 1] -> (3, -1)
        ExpressionCalculator.CalculatorBatchResult lbfgsb = EvalBatchOne("opt_lbfgsb((x - 5)^2 + (y + 2)^2, [x, y], [1, 0], [0, -1], [3, 1], 1e-8)");
        Assert.True(lbfgsb.IsOk, lbfgsb.Error?.Message);
        Assert.InRange(lbfgsb.ArrayValues![0], 2.9999m, 3.0001m);
        Assert.InRange(lbfgsb.ArrayValues![1], -1.0001m, -0.9999m);

        // Domain-guarded objective (-ln(x)) on [1, 10] never steps across x = 0 during finite difference -> minimum at x = 10
        ExpressionCalculator.CalculatorBatchResult lbfgsbLog = EvalBatchOne("opt_lbfgsb(-ln(x), [x], [1], [1], [10], 1e-8)");
        Assert.True(lbfgsbLog.IsOk, lbfgsbLog.Error?.Message);
        Assert.InRange(lbfgsbLog.ArrayValues![0], 9.9999m, 10.0001m);

        // Narrow box width [1, 1 + 1e-9] < 2*h_j uses safe first-order one-sided FD without out-of-bounds probe
        ExpressionCalculator.CalculatorBatchResult lbfgsbNarrow = EvalBatchOne("opt_lbfgsb(x^2, [x], [1.0000000005], [1], [1.000000001], 1e-8)");
        Assert.True(lbfgsbNarrow.IsOk, lbfgsbNarrow.Error?.Message);
        Assert.InRange(lbfgsbNarrow.ArrayValues![0], 0.999999999m, 1.000000001m);

        // Near-boundary 2nd-order one-sided FD from boundary x0 = 1 on [1, 3] for (x - 2)^2 -> x = 2
        ExpressionCalculator.CalculatorBatchResult lbfgsbBoundaryStart = EvalBatchOne("opt_lbfgsb((x - 2)^2, [x], [1], [1], [3], 1e-8)");
        Assert.True(lbfgsbBoundaryStart.IsOk, lbfgsbBoundaryStart.Error?.Message);
        Assert.InRange(lbfgsbBoundaryStart.ArrayValues![0], 1.99999m, 2.00001m);
    }

    [Fact]
    public void FsolveAndPolynomials_SolveNonlinearAndPolynomialRoots()
    {
        // Solve x + y - 3 = 0, x^2 + y^2 - 5 = 0 starting at [0.5, 2.5] -> (1, 2)
        ExpressionCalculator.CalculatorBatchResult sol = EvalBatchOne("fsolve([x + y - 3, x^2 + y^2 - 5], [x, y], [0.5, 2.5], 1e-10)");
        Assert.True(sol.IsOk, sol.Error?.Message);
        Assert.InRange(sol.ArrayValues![0], 0.999999m, 1.000001m);
        Assert.InRange(sol.ArrayValues![1], 1.999999m, 2.000001m);

        // polyval(2x^2 - 3x + 5 at x = 4) = 25
        Assert.Equal(25m, EvalBatchOne("polyval([2, -3, 5], 4)").ScalarValue);

        // x^2 + 1 = 0 -> roots 0 - 1i, 0 + 1i
        Assert.Equal(new[] { 0m, 0m }, EvalBatchOne("poly_roots_re([1, 0, 1])").ArrayValues);
        Assert.Equal(new[] { -1m, 1m }, EvalBatchOne("poly_roots_im([1, 0, 1])").ArrayValues);

        // Scaled large root (x - 10^6)(x - 1) via radix-2 balancing & tiny-leading-coefficient scaling 1e-15*(x-1)(x-2)
        ExpressionCalculator.CalculatorBatchResult largeRoot = EvalBatchOne("poly_roots_re([1, -1000001, 1000000])");
        Assert.True(largeRoot.IsOk, largeRoot.Error?.Message);
        Assert.InRange(largeRoot.ArrayValues![0], 0.9999m, 1.0001m);
        Assert.InRange(largeRoot.ArrayValues![1], 999999.9m, 1000000.1m);

        ExpressionCalculator.CalculatorBatchResult tinyLead = EvalBatchOne("poly_roots_re([0.000000000000001, -0.000000000000003, 0.000000000000002])");
        Assert.True(tinyLead.IsOk, tinyLead.Error?.Message);
        Assert.InRange(tinyLead.ArrayValues![0], 0.999999m, 1.000001m);
        Assert.InRange(tinyLead.ArrayValues![1], 1.999999m, 2.000001m);

        // Degree-16 repeated root (x - 10)^16 stays finite without companion-matrix blowup
        decimal[] deg16Coeffs = new decimal[17];
        for (int k = 0; k <= 16; k++)
        {
            long binom = 1;
            for (int j = 0; j < k; j++)
            {
                binom = binom * (16 - j) / (j + 1);
            }

            decimal sign = (k % 2 == 0) ? 1m : -1m;
            decimal pow10 = 1m;
            for (int j = 0; j < k; j++)
            {
                pow10 *= 10m;
            }

            deg16Coeffs[k] = sign * binom * pow10;
        }

        ExpressionCalculator.CalculatorBatchResult deg16 = ExpressionCalculator.EvaluateBatch(
            EmptyScalars,
            new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal) { ["c16"] = deg16Coeffs },
            new[] { new ExpressionCalculator.CalculatorBatchItem("r", "poly_roots_re(c16)") },
            CalculatorLimits.Default)[0];
        Assert.True(deg16.IsOk, deg16.Error?.Message);
        Assert.Equal(16, deg16.ArrayValues!.Count);
        foreach (decimal rootVal in deg16.ArrayValues!)
        {
            Assert.InRange(rootVal, 7m, 13m);
        }

        // Degenerate / excess scale ratio -> DOMAIN_ERROR
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("poly_roots_re([0.000000000000001, 10000000000, 10000000000])").Error!.Code);

        // Canonical complex sort permutation invariance across all 6 permutations of a 1e-12 head-anchored cluster
        double[] baseRe = { 1.0, 1.0 + 4e-13, 1.0 - 4e-13 };
        double[] baseIm = { 2.0, -2.0, 0.5 };
        int[][] perms =
        {
            new[] { 0, 1, 2 }, new[] { 0, 2, 1 },
            new[] { 1, 0, 2 }, new[] { 1, 2, 0 },
            new[] { 2, 0, 1 }, new[] { 2, 1, 0 }
        };
        double[] expectedRe = new double[3];
        double[] expectedIm = new double[3];
        for (int p = 0; p < perms.Length; p++)
        {
            double[] rBuf = { baseRe[perms[p][0]], baseRe[perms[p][1]], baseRe[perms[p][2]] };
            double[] iBuf = { baseIm[perms[p][0]], baseIm[perms[p][1]], baseIm[perms[p][2]] };
            DslLinearAlgebraMath.SortComplexPairsCanonical(rBuf, iBuf, 3);
            if (p == 0)
            {
                Array.Copy(rBuf, expectedRe, 3);
                Array.Copy(iBuf, expectedIm, 3);
            }
            else
            {
                Assert.Equal(expectedRe, rBuf);
                Assert.Equal(expectedIm, iBuf);
            }
        }
        Assert.Equal(new[] { -2.0, 0.5, 2.0 }, expectedIm);
    }

    [Fact]
    public void OdeBdf2AndInterp2DBilinear_MatchAnalyticAndGridContracts()
    {
        // Stiff decay y' = -50 * y, y(0) = 1 -> y(0.1) = exp(-5) ~= 0.0067379
        ExpressionCalculator.CalculatorBatchResult bdf2 = EvalBatchOne("ode_bdf2([-50 * y], t, [y], 0, 0.1, [1], 100, 1e-10)");
        Assert.True(bdf2.IsOk, bdf2.Error?.Message);
        Assert.InRange(bdf2.ArrayValues![0], 0.0066m, 0.0069m);

        // Severe stiffness y' = -10000 * y, y(0) = 1 to t = 0.01 (h*lambda = -5) -> L-stable BDF-1+BDF-2 damps without sign flip or blowup
        ExpressionCalculator.CalculatorBatchResult veryStiff = EvalBatchOne("ode_bdf2([-10000 * y], t, [y], 0, 0.01, [1], 20, 1e-10)");
        Assert.True(veryStiff.IsOk, veryStiff.Error?.Message);
        Assert.InRange(veryStiff.ArrayValues![0], 0m, 1e-5m);

        // nSteps = 1 uses L-stable BDF-1 (Implicit Euler): y' = -2 * y, y(0) = 1 to t = 0.5 -> 1 / (1 + 1) = 0.5
        ExpressionCalculator.CalculatorBatchResult oneStep = EvalBatchOne("ode_bdf2([-2 * y], t, [y], 0, 0.5, [1], 1, 1e-10)");
        Assert.True(oneStep.IsOk, oneStep.Error?.Message);
        Assert.InRange(oneStep.ArrayValues![0], 0.499999m, 0.500001m);

        // Global second-order O(h^2) convergence on y' = -y, y(0) = 1 to t = 1 (exact exp(-1))
        ExpressionCalculator.CalculatorBatchResult errN40Res = EvalBatchOne("ode_bdf2([-y], t, [y], 0, 1, [1], 40, 1e-12)");
        ExpressionCalculator.CalculatorBatchResult errN80Res = EvalBatchOne("ode_bdf2([-y], t, [y], 0, 1, [1], 80, 1e-12)");
        Assert.True(errN40Res.IsOk);
        Assert.True(errN80Res.IsOk);
        double exact = Math.Exp(-1.0);
        double err40 = Math.Abs((double)errN40Res.ArrayValues![0] - exact);
        double err80 = Math.Abs((double)errN80Res.ArrayValues![0] - exact);
        Assert.InRange(err40 / err80, 3.5, 4.5);

        // Backward integration t1 < t0: y' = 2, y(1) = 5 -> y(0) = 3
        ExpressionCalculator.CalculatorBatchResult back = EvalBatchOne("ode_bdf2([2], t, [y], 1, 0, [5], 10)");
        Assert.True(back.IsOk, back.Error?.Message);
        Assert.InRange(back.ArrayValues![0], 2.999999m, 3.000001m);

        // t0 == t1 returns y0 copy unchanged
        ExpressionCalculator.CalculatorBatchResult sameT = EvalBatchOne("ode_bdf2([2 * y], t, [y], 3, 3, [42], 10)");
        Assert.True(sameT.IsOk, sameT.Error?.Message);
        Assert.Equal(new[] { 42m }, sameT.ArrayValues);

        // Bilinear grid: Z(x, y) = x + 10*y on xs=[0, 2], ys=[0, 4] -> Z = [0, 2, 40, 42]
        // Query at (1, 2) = 21
        ExpressionCalculator.CalculatorBatchResult interp = EvalBatchOne("interp2d_bilinear([0, 2], [0, 4], [0, 2, 40, 42], 1, 2)");
        Assert.True(interp.IsOk, interp.Error?.Message);
        Assert.Equal(21m, interp.ScalarValue);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("interp2d_bilinear([0, 2], [0, 4], [0, 2, 40, 42], 3, 2)").Error!.Code);
    }
}
