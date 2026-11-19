#nullable enable
using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Calculator;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

public sealed class CalculatorDslLinearAlgebraTests
{
    private static readonly IReadOnlyDictionary<string, decimal> Empty = new Dictionary<string, decimal>(StringComparer.Ordinal);

    // ---- Calculator Scalar Matrix Projections (det2, det3, solve2_x, solve2_y) ----

    [Fact]
    public void CalculatorDet2AndDet3EvaluateExactDecimalAndNormalizeZero()
    {
        CalculatorOutcome d2 = ExpressionCalculator.Evaluate("det2(3, 5, 1, 2)", Empty, CalculatorLimits.Default);
        Assert.Null(d2.Error);
        Assert.Equal(1m, d2.Value);

        CalculatorOutcome d2NegZero = ExpressionCalculator.Evaluate("det2(-0, 1, 0, 2)", Empty, CalculatorLimits.Default);
        Assert.Null(d2NegZero.Error);
        Assert.Equal(0m, d2NegZero.Value);
        Assert.Equal(0, decimal.GetBits(d2NegZero.Value!.Value)[3]);

        // det3 uses 9 scalar arguments under CalculatorLimits.Default (MaxFunctionArguments = 8).
        CalculatorOutcome d3 = ExpressionCalculator.Evaluate("det3(6, 1, 1, 4, -2, 5, 2, 8, 7)", Empty, CalculatorLimits.Default);
        Assert.Null(d3.Error);
        Assert.Equal(-306m, d3.Value);
    }

    [Fact]
    public void CalculatorSolve2UsesCanonicalMatrixFirstOrderAndDetectsSingularMatrix()
    {
        // x + 2y = 5, 3x + 4y = 6 -> A = [1, 2; 3, 4], b = [5, 6] => x = -4, y = 4.5
        // (Matrix-first: args are a11, a12, a21, a22, b1, b2)
        CalculatorOutcome xOut = ExpressionCalculator.Evaluate("solve2_x(1, 2, 3, 4, 5, 6)", Empty, CalculatorLimits.Default);
        CalculatorOutcome yOut = ExpressionCalculator.Evaluate("solve2_y(1, 2, 3, 4, 5, 6)", Empty, CalculatorLimits.Default);
        Assert.Null(xOut.Error);
        Assert.Null(yOut.Error);
        Assert.Equal(-4m, xOut.Value);
        Assert.Equal(4.5m, yOut.Value);

        // Partial pivoting needed when a11 == 0
        CalculatorOutcome pivotX = ExpressionCalculator.Evaluate("solve2_x(0, 2, 1, 3, 4, 7)", Empty, CalculatorLimits.Default);
        CalculatorOutcome pivotY = ExpressionCalculator.Evaluate("solve2_y(0, 2, 1, 3, 4, 7)", Empty, CalculatorLimits.Default);
        Assert.Null(pivotX.Error);
        Assert.Null(pivotY.Error);
        Assert.Equal(1m, pivotX.Value);
        Assert.Equal(2m, pivotY.Value);

        // Singular and near-singular systems
        CalculatorOutcome sing = ExpressionCalculator.Evaluate("solve2_x(1, 2, 2, 4, 3, 6)", Empty, CalculatorLimits.Default);
        Assert.Equal(CalculatorErrorCode.SingularMatrix, sing.Error!.Code);

        CalculatorOutcome zeroMat = ExpressionCalculator.Evaluate("solve2_y(0, 0, 0, 0, 1, 2)", Empty, CalculatorLimits.Default);
        Assert.Equal(CalculatorErrorCode.SingularMatrix, zeroMat.Error!.Code);
    }

    [Fact]
    public void CalculatorMatrixOverflowAndCustomCapReturnExpectedErrors()
    {
        CalculatorOutcome ov = ExpressionCalculator.Evaluate("det2(1000000000000000, 0, 0, 1000000000000000)", Empty, CalculatorLimits.Default);
        Assert.Equal(CalculatorErrorCode.ArithmeticOverflow, ov.Error!.Code);

        var cap2 = new CalculatorLimits { MaxFunctionArguments = 2 };
        CalculatorOutcome capped = ExpressionCalculator.Evaluate("det3(1,0,0,0,1,0,0,0,1)", Empty, cap2);
        Assert.Equal(CalculatorErrorCode.WrongArgumentCount, capped.Error!.Code);
    }

    // ---- Vectors, Rotations, Affine, Matrix Multiply & Budget ----

    [Fact]
    public void VectorOperationsAndCross3DHandleNormalBoundaryAndErrorCases()
    {
        ReadOnlySpan<double> u = [1.0, 2.0, 3.0];
        ReadOnlySpan<double> v = [4.0, 5.0, 6.0];
        MathResult<double> dot = DslLinearAlgebraMath.VecDot(u, v);
        Assert.True(dot.IsOk);
        Assert.Equal(32.0, dot.Value, 12);

        // Scaled norm avoids overflow on large finite doubles
        ReadOnlySpan<double> huge = [3e200, 4e200];
        MathResult<double> hugeNorm = DslLinearAlgebraMath.VecNorm(huge);
        Assert.True(hugeNorm.IsOk);
        Assert.Equal(5e200, hugeNorm.Value, 1e188);

        Span<double> normBuf = stackalloc double[3];
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.VecNormalize(u, normBuf));
        Assert.Equal(1.0, DslLinearAlgebraMath.VecNorm(normBuf).Value, 12);

        ReadOnlySpan<double> tiny = [1e-15, 0.0, 0.0];
        Assert.Equal(MathErrorCode.DivisionByZero, DslLinearAlgebraMath.VecNormalize(tiny, normBuf));

        Span<double> cross = stackalloc double[3];
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.Cross3D(u, v, cross));
        Assert.Equal([-3.0, 6.0, -3.0], cross.ToArray());

        ReadOnlySpan<double> badLen = [1.0, 2.0];
        Assert.Equal(MathErrorCode.DimensionMismatch, DslLinearAlgebraMath.Cross3D(badLen, v, cross));
        Assert.Equal(MathErrorCode.DimensionMismatch, DslLinearAlgebraMath.VecDot(badLen, v).Error);

        ReadOnlySpan<double> nonFinite = [1.0, double.NaN, 0.0];
        Assert.Equal(MathErrorCode.NonFiniteResult, DslLinearAlgebraMath.VecNorm(nonFinite).Error);
    }

    [Fact]
    public void MatrixTransformsAndMatMulEnforceBudgetAndDimensions()
    {
        Span<double> rot2 = stackalloc double[4];
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatRot2D(Math.PI * 0.5, rot2));
        Assert.Equal(0.0, rot2[0], 1e-12);
        Assert.Equal(-1.0, rot2[1], 1e-12);
        Assert.Equal(1.0, rot2[2], 1e-12);
        Assert.Equal(0.0, rot2[3], 1e-12);

        ReadOnlySpan<double> zAxis = [0.0, 0.0, 5.0];
        Span<double> rot3 = stackalloc double[9];
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatRot3D(zAxis, Math.PI * 0.5, rot3));
        Assert.Equal(0.0, rot3[0], 1e-12);
        Assert.Equal(-1.0, rot3[1], 1e-12);
        Assert.Equal(1.0, rot3[3], 1e-12);
        Assert.Equal(1.0, rot3[8], 1e-12);

        ReadOnlySpan<double> zeroAxis = [0.0, 0.0, 0.0];
        Assert.Equal(MathErrorCode.DivisionByZero, DslLinearAlgebraMath.MatRot3D(zeroAxis, 1.0, rot3));

        Span<double> affine = stackalloc double[9];
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatAffine(rot2, 10.0, -5.0, affine));
        Assert.Equal(10.0, affine[2], 12);
        Assert.Equal(-5.0, affine[5], 12);
        Assert.Equal(1.0, affine[8], 12);

        DslExecutionBudget budget = DslExecutionBudget.Create(10);
        ReadOnlySpan<double> a = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0]; // 2x3
        ReadOnlySpan<double> b = [7.0, 8.0, 9.0, 10.0, 11.0, 12.0]; // 3x2
        Span<double> c = stackalloc double[4];
        // Cost = 2 * 3 * 2 = 12 > 10 -> BudgetExceeded
        Assert.Equal(MathErrorCode.BudgetExceeded, DslLinearAlgebraMath.MatMul(a, 2, 3, b, 3, 2, c, ref budget));

        budget = DslExecutionBudget.CreateDefault();
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatMul(a, 2, 3, b, 3, 2, c, ref budget));
        Assert.Equal(DslExecutionBudget.DefaultMaxOps - 12, budget.RemainingOps);
        Assert.Equal([58.0, 64.0, 139.0, 154.0], c.ToArray());

        // In-place transpose support
        Span<double> tr = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0];
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatTranspose(tr, 2, 3, tr));
        Assert.Equal([1.0, 4.0, 2.0, 5.0, 3.0, 6.0], tr.ToArray());
    }

    // ---- LU, Det, Inv, Solve, Least Squares ----

    [Fact]
    public void LuDetInvSolveAndLeastSquaresWorkAndRejectDim17()
    {
        DslExecutionBudget budget = DslExecutionBudget.CreateDefault();
        ReadOnlySpan<double> a =
        [
            0.0, 2.0, 1.0,
            1.0, -2.0, -3.0,
            -1.0, 1.0, 2.0,
        ];
        Span<double> l = stackalloc double[9];
        Span<double> u = stackalloc double[9];
        Span<int> perm = stackalloc int[3];
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatLu(a, 3, l, u, perm, out int parity, ref budget));
        Assert.Equal(-1, parity);

        Span<double> luProd = stackalloc double[9];
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatMul(l, 3, 3, u, 3, 3, luProd, ref budget));
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                Assert.Equal(a[perm[i] * 3 + j], luProd[i * 3 + j], 1e-12);
            }
        }

        MathResult<double> det = DslLinearAlgebraMath.MatDet(a, 3, ref budget);
        Assert.True(det.IsOk);
        Assert.Equal(1.0, det.Value, 1e-12);

        ReadOnlySpan<double> singular = [1.0, 2.0, 2.0, 4.0];
        MathResult<double> singDet = DslLinearAlgebraMath.MatDet(singular, 2, ref budget);
        Assert.True(singDet.IsOk);
        Assert.Equal(0.0, singDet.Value);

        Span<double> inv = stackalloc double[9];
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatInv(a, 3, inv, ref budget));
        Span<double> ident = stackalloc double[9];
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatMul(a, 3, 3, inv, 3, 3, ident, ref budget));
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                Assert.Equal(i == j ? 1.0 : 0.0, ident[i * 3 + j], 1e-12);
            }
        }

        ReadOnlySpan<double> rhs = [5.0, -11.0, 7.0];
        Span<double> sol = stackalloc double[3];
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatSolve(a, 3, rhs, sol, ref budget));
        Span<double> checkRhs = stackalloc double[3];
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatMul(a, 3, 3, sol, 3, 1, checkRhs, ref budget));
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(rhs[i], checkRhs[i], 1e-12);
        }

        // Overdetermined least squares (4x2)
        ReadOnlySpan<double> lsA = [1.0, 1.0, 1.0, 2.0, 1.0, 3.0, 1.0, 4.0];
        ReadOnlySpan<double> lsB = [6.0, 5.0, 7.0, 10.0];
        Span<double> lsX = stackalloc double[2];
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatLstSq(lsA, 4, 2, lsB, lsX, out double rss, ref budget));
        Assert.Equal(3.5, lsX[0], 1e-12);
        Assert.Equal(1.4, lsX[1], 1e-12);
        Assert.Equal(4.2, rss, 1e-12);

        // N = 16 boundary succeeds; N = 17 rejects with DimensionMismatch
        Span<double> eye16 = stackalloc double[256];
        Span<double> rhs16 = stackalloc double[16];
        Span<double> sol16 = stackalloc double[16];
        for (int i = 0; i < 16; i++)
        {
            eye16[i * 16 + i] = 2.0;
            rhs16[i] = i + 1.0;
        }

        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatSolve(eye16, 16, rhs16, sol16, ref budget));
        Assert.Equal(8.0, sol16[15], 1e-12);

        Span<double> buf17 = stackalloc double[17 * 17];
        Span<double> v17 = stackalloc double[17];
        Assert.Equal(MathErrorCode.DimensionMismatch, DslLinearAlgebraMath.MatSolve(buf17, 17, v17, v17, ref budget));
    }

    // ---- Cholesky, QR, Symmetric Eigen (Descending), SVD, General Eigen ----

    [Fact]
    public void CholeskyAndQrEnforceSymmetryPositiveDefinitenessAndOrthogonality()
    {
        DslExecutionBudget budget = DslExecutionBudget.CreateDefault();
        ReadOnlySpan<double> spd = [4.0, 12.0, -16.0, 12.0, 37.0, -43.0, -16.0, -43.0, 98.0];
        Span<double> chol = stackalloc double[9];
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatChol(spd, 3, chol, ref budget));
        Assert.Equal(2.0, chol[0], 1e-12);
        Assert.Equal(6.0, chol[3], 1e-12);
        Assert.Equal(1.0, chol[4], 1e-12);
        Assert.Equal(-8.0, chol[6], 1e-12);
        Assert.Equal(5.0, chol[7], 1e-12);
        Assert.Equal(3.0, chol[8], 1e-12);

        ReadOnlySpan<double> asym = [4.0, 1.0, 0.0, 4.0];
        Assert.Equal(MathErrorCode.DomainError, DslLinearAlgebraMath.MatChol(asym, 2, chol, ref budget));

        ReadOnlySpan<double> indef = [1.0, 2.0, 2.0, 1.0];
        Assert.Equal(MathErrorCode.SingularMatrix, DslLinearAlgebraMath.MatChol(indef, 2, chol, ref budget));

        // Thin QR on 4x3
        ReadOnlySpan<double> a = [1.0, -1.0, 4.0, 1.0, 4.0, -2.0, 1.0, 4.0, 2.0, 1.0, -1.0, 0.0];
        Span<double> q = stackalloc double[12];
        Span<double> r = stackalloc double[9];
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatQr(a, 4, 3, q, r, ref budget));

        Span<double> qrProd = stackalloc double[12];
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatMul(q, 4, 3, r, 3, 3, qrProd, ref budget));
        for (int i = 0; i < 12; i++)
        {
            Assert.Equal(a[i], qrProd[i], 1e-11);
        }

        // Full QR on 4x3
        Span<double> qFull = stackalloc double[16];
        Span<double> rFull = stackalloc double[12];
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatQr(a, 4, 3, qFull, rFull, ref budget, fullQ: true));
        Span<double> qtFull = stackalloc double[16];
        Span<double> qqtFull = stackalloc double[16];
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatTranspose(qFull, 4, 4, qtFull));
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatMul(qtFull, 4, 4, qFull, 4, 4, qqtFull, ref budget));
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                Assert.Equal(i == j ? 1.0 : 0.0, qqtFull[i * 4 + j], 1e-12);
            }
        }
    }

    [Fact]
    public void MatEigenSymReturnsDescendingPcaOrderedEigenvaluesAndOrthonormalEigenvectors()
    {
        DslExecutionBudget budget = DslExecutionBudget.CreateDefault();
        // Symmetric covariance-like matrix with known eigenvalues [6, 3, 1] -> must return descending!
        ReadOnlySpan<double> sym =
        [
            3.0, 1.0, 0.0,
            1.0, 3.0, 0.0,
            0.0, 0.0, 6.0,
        ];
        Span<double> vals = stackalloc double[3];
        Span<double> vecs = stackalloc double[9];
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatEigenSym(sym, 3, vals, vecs, ref budget));

        Assert.Equal(6.0, vals[0], 1e-11);
        Assert.Equal(4.0, vals[1], 1e-11);
        Assert.Equal(2.0, vals[2], 1e-11);
        Assert.True(vals[0] >= vals[1] && vals[1] >= vals[2]);

        // Verify A * V == V * diag(lambda) and V^T * V == I
        Span<double> av = stackalloc double[9];
        Span<double> vt = stackalloc double[9];
        Span<double> vtv = stackalloc double[9];
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatMul(sym, 3, 3, vecs, 3, 3, av, ref budget));
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatTranspose(vecs, 3, 3, vt));
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatMul(vt, 3, 3, vecs, 3, 3, vtv, ref budget));
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                Assert.Equal(i == j ? 1.0 : 0.0, vtv[i * 3 + j], 1e-12);
                Assert.Equal(vecs[i * 3 + j] * vals[j], av[i * 3 + j], 1e-11);
            }
        }
    }

    [Fact]
    public void MatSvdHandlesSquareTallWideAndRankDeficientMatrices()
    {
        DslExecutionBudget budget = DslExecutionBudget.CreateDefault();

        // Wide 2x3 matrix
        ReadOnlySpan<double> wide = [3.0, 2.0, 2.0, 2.0, 3.0, -2.0];
        Span<double> u = stackalloc double[4];
        Span<double> s = stackalloc double[2];
        Span<double> vt = stackalloc double[6];
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatSvd(wide, 2, 3, u, s, vt, ref budget));
        Assert.Equal(5.0, s[0], 1e-11);
        Assert.Equal(3.0, s[1], 1e-11);

        // Reconstruct U * diag(S) * Vt
        Span<double> us = stackalloc double[4];
        for (int i = 0; i < 2; i++)
        {
            for (int j = 0; j < 2; j++)
            {
                us[i * 2 + j] = u[i * 2 + j] * s[j];
            }
        }

        Span<double> recon = stackalloc double[6];
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatMul(us, 2, 2, vt, 2, 3, recon, ref budget));
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(wide[i], recon[i], 1e-11);
        }

        // Rank-1 3x3 matrix must still produce orthonormal U
        ReadOnlySpan<double> rank1 = [1.0, 2.0, 3.0, 2.0, 4.0, 6.0, 3.0, 6.0, 9.0];
        Span<double> u3 = stackalloc double[9];
        Span<double> s3 = stackalloc double[3];
        Span<double> vt3 = stackalloc double[9];
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatSvd(rank1, 3, 3, u3, s3, vt3, ref budget));
        Assert.Equal(14.0, s3[0], 1e-11);
        Assert.Equal(0.0, s3[1], 1e-11);
        Assert.Equal(0.0, s3[2], 1e-11);

        Span<double> ut3 = stackalloc double[9];
        Span<double> utu3 = stackalloc double[9];
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatTranspose(u3, 3, 3, ut3));
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatMul(ut3, 3, 3, u3, 3, 3, utu3, ref budget));
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                Assert.Equal(i == j ? 1.0 : 0.0, utu3[i * 3 + j], 1e-11);
            }
        }
    }

    [Fact]
    public void MatEigenGeneralFindsComplexConjugateAndRealEigenvalues()
    {
        DslExecutionBudget budget = DslExecutionBudget.CreateDefault();
        // Companion/rotation-coupled 3x3 matrix: characteristic poly (lambda - 2) * (lambda^2 + 1) = lambda^3 - 2*lambda^2 + lambda - 2
        // Eigenvalues: 2, +i, -i -> trace = 2, sum of |lambda|^2 or product
        ReadOnlySpan<double> a =
        [
            0.0, -1.0, 0.0,
            1.0,  0.0, 0.0,
            2.0,  3.0, 2.0,
        ];
        Span<double> re = stackalloc double[3];
        Span<double> im = stackalloc double[3];
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatEigenGeneral(a, 3, re, im, ref budget));

        double trace = re[0] + re[1] + re[2];
        double imagSum = im[0] + im[1] + im[2];
        Assert.Equal(2.0, trace, 1e-11);
        Assert.Equal(0.0, imagSum, 1e-11);

        bool foundRealTwo = false;
        bool foundPlusI = false;
        bool foundMinusI = false;
        for (int i = 0; i < 3; i++)
        {
            if (Math.Abs(re[i] - 2.0) < 1e-10 && Math.Abs(im[i]) < 1e-10) foundRealTwo = true;
            if (Math.Abs(re[i]) < 1e-10 && Math.Abs(im[i] - 1.0) < 1e-10) foundPlusI = true;
            if (Math.Abs(re[i]) < 1e-10 && Math.Abs(im[i] + 1.0) < 1e-10) foundMinusI = true;
        }

        Assert.True(foundRealTwo && foundPlusI && foundMinusI);
    }

    // ---- DslCurveMath (2D/3D Analytic Geometry) ----

    [Fact]
    public void DslCurveMathComputesLineIntersectionAndPointToPlaneDistance()
    {
        MathResult<DslPoint2D> hit = DslCurveMath.LineIntersect2D(
            new DslPoint2D(0m, 0m),
            new DslPoint2D(10m, 10m),
            new DslPoint2D(0m, 10m),
            new DslPoint2D(10m, 0m));
        Assert.True(hit.IsOk);
        Assert.Equal(5m, hit.Value.X);
        Assert.Equal(5m, hit.Value.Y);

        MathResult<DslPoint2D> parallel = DslCurveMath.LineIntersect2D(
            new DslPoint2D(0m, 0m),
            new DslPoint2D(1m, 1m),
            new DslPoint2D(0m, 2m),
            new DslPoint2D(1m, 3m));
        Assert.Equal(MathErrorCode.DivisionByZero, parallel.Error);

        MathResult<DslPoint2D> degen = DslCurveMath.LineIntersect2D(
            new DslPoint2D(1m, 1m),
            new DslPoint2D(1m, 1m),
            new DslPoint2D(0m, 0m),
            new DslPoint2D(1m, 0m));
        Assert.Equal(MathErrorCode.DomainError, degen.Error);

        // Distance from (1, 2, 2) to 3x + 4y + 0z - 1 = 0 is |3 + 8 - 1| / 5 = 2
        MathResult<decimal> dist = DslCurveMath.PointToPlaneDistance(1m, 2m, 2m, 3m, 4m, 0m, -1m);
        Assert.True(dist.IsOk);
        Assert.Equal(2m, dist.Value);

        MathResult<decimal> zeroNorm = DslCurveMath.PointToPlaneDistance(1m, 2m, 2m, 0m, 0m, 0m, -1m);
        Assert.Equal(MathErrorCode.DivisionByZero, zeroNorm.Error);
    }

    // ---- Zero-Allocation Hot Path Verification ----

    [Fact]
    public void StackAllocatedLinearAlgebraAndCurveKernelsAllocateZeroBytesAfterWarmup()
    {
        DslExecutionBudget budget = DslExecutionBudget.CreateDefault();
        Span<double> a = stackalloc double[16];
        Span<double> b = stackalloc double[16];
        Span<double> c = stackalloc double[16];
        Span<double> vals = stackalloc double[4];
        Span<double> vecs = stackalloc double[16];
        Span<double> imVals = stackalloc double[4];

        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                double val = i == j ? 10.0 + i : 1.0 / (1 + Math.Abs(i - j));
                a[i * 4 + j] = val;
                b[i * 4 + j] = i == j ? 2.0 : 0.25;
            }
        }

        // Warmup JIT
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatMul(a, 4, 4, b, 4, 4, c, ref budget));
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatChol(a, 4, c, ref budget));
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatEigenSym(a, 4, vals, vecs, ref budget));
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatSvd(a, 4, 4, c, vals, vecs, ref budget));
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatEigenGeneral(a, 4, vals, imVals, ref budget));
        _ = DslCurveMath.LineIntersect2D(new DslPoint2D(0m, 0m), new DslPoint2D(2m, 2m), new DslPoint2D(0m, 2m), new DslPoint2D(2m, 0m));

        MathErrorCode err1 = MathErrorCode.None;
        MathErrorCode err2 = MathErrorCode.None;
        MathErrorCode err3 = MathErrorCode.None;
        MathErrorCode err4 = MathErrorCode.None;
        MathErrorCode err5 = MathErrorCode.None;
        MathResult<DslPoint2D> pt = default;

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iter = 0; iter < 10; iter++)
        {
            budget = DslExecutionBudget.CreateDefault();
            err1 |= DslLinearAlgebraMath.MatMul(a, 4, 4, b, 4, 4, c, ref budget);
            err2 |= DslLinearAlgebraMath.MatChol(a, 4, c, ref budget);
            err3 |= DslLinearAlgebraMath.MatEigenSym(a, 4, vals, vecs, ref budget);
            err4 |= DslLinearAlgebraMath.MatSvd(a, 4, 4, c, vals, vecs, ref budget);
            err5 |= DslLinearAlgebraMath.MatEigenGeneral(a, 4, vals, imVals, ref budget);
            pt = DslCurveMath.LineIntersect2D(new DslPoint2D(0m, 0m), new DslPoint2D(2m, 2m), new DslPoint2D(0m, 2m), new DslPoint2D(2m, 0m));
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0L, allocated);
        Assert.Equal(MathErrorCode.None, err1);
        Assert.Equal(MathErrorCode.None, err2);
        Assert.Equal(MathErrorCode.None, err3);
        Assert.Equal(MathErrorCode.None, err4);
        Assert.Equal(MathErrorCode.None, err5);
        Assert.True(pt.IsOk);
    }

    [Fact]
    public void Step3Fixes_MaintainRelativeScaleAndZeroAllocationAcrossLinearAlgebraSolvers()
    {
        DslExecutionBudget budget = DslExecutionBudget.CreateDefault();

        // 1. MatChol: small SPD matrix 1e-14 * I_2 must succeed; ill-conditioned diag(1e12, 1e-6) must fail.
        ReadOnlySpan<double> tinySpd = [1e-14, 0.0, 0.0, 1e-14];
        Span<double> lower = stackalloc double[4];
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatChol(tinySpd, 2, lower, ref budget));
        Assert.Equal(1e-7, lower[0], 1e-14);
        Assert.Equal(0.0, lower[2], 1e-14);
        Assert.Equal(1e-7, lower[3], 1e-14);

        ReadOnlySpan<double> illCond = [1e12, 0.0, 0.0, 1e-6];
        Assert.Equal(MathErrorCode.SingularMatrix, DslLinearAlgebraMath.MatChol(illCond, 2, lower, ref budget));

        ReadOnlySpan<double> zeroMat = [0.0, 0.0, 0.0, 0.0];
        Assert.Equal(MathErrorCode.SingularMatrix, DslLinearAlgebraMath.MatChol(zeroMat, 2, lower, ref budget));

        // 2. MatDet: row equilibration preserves small uneliminated pivots like diag(1, 1e-13) while keeping rank-deficient 0.
        ReadOnlySpan<double> smallPivotDiag = [1.0, 0.0, 0.0, 1e-13];
        MathResult<double> detSmall = DslLinearAlgebraMath.MatDet(smallPivotDiag, 2, ref budget);
        Assert.True(detSmall.IsOk);
        Assert.Equal(1e-13, detSmall.Value, 1e-24);

        ReadOnlySpan<double> rankDef = [1.0, 2.0, 2.0, 4.0];
        MathResult<double> detZero = DslLinearAlgebraMath.MatDet(rankDef, 2, ref budget);
        Assert.True(detZero.IsOk);
        Assert.Equal(0.0, detZero.Value);

        // 3. SolveLinearSystem2x2: tiny 1e-13m..1e-16m scale avoids decimal Cramer det underflow/digit loss.
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.SolveLinearSystem2x2(
            1e-13m, 2e-13m,
            3e-13m, 4e-13m,
            5e-13m, 11e-13m,
            out decimal x13, out decimal y13));
        Assert.Equal(1m, x13);
        Assert.Equal(2m, y13);

        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.SolveLinearSystem2x2(
            1e-16m, 0m,
            0m, 2e-16m,
            3e-16m, 8e-16m,
            out decimal x16, out decimal y16));
        Assert.Equal(3m, x16);
        Assert.Equal(4m, y16);

        // Warm JIT then verify 0 heap allocation.
        long before = GC.GetAllocatedBytesForCurrentThread();
        MathErrorCode cholErr = DslLinearAlgebraMath.MatChol(tinySpd, 2, lower, ref budget);
        MathResult<double> detRes = DslLinearAlgebraMath.MatDet(smallPivotDiag, 2, ref budget);
        MathErrorCode solveErr = DslLinearAlgebraMath.SolveLinearSystem2x2(
            1e-16m, 2e-16m,
            3e-16m, 5e-16m,
            1e-16m, 2e-16m,
            out decimal _, out decimal _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0L, allocated);
        Assert.Equal(MathErrorCode.None, cholErr);
        Assert.True(detRes.IsOk);
        Assert.Equal(MathErrorCode.None, solveErr);

        // 4. End-to-end DSL tiny-scale solve2_x/solve2_y
        CalculatorOutcome dslSolveX = ExpressionCalculator.Evaluate(
            "solve2_x(0.0000000000000001, 0.0000000000000002, 0.0000000000000003, 0.0000000000000004, 0.0000000000000005, 0.0000000000000006)",
            Empty,
            CalculatorLimits.Default);
        Assert.Null(dslSolveX.Error);
        Assert.Equal(-4m, dslSolveX.Value);
    }
}
