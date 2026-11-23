using System;
using System.Buffers;

namespace StockAnalyzer.Core.MathUtils;

public static partial class DslOptimizationMath
{
    private const int MaxConstrainedDim = DslLinearAlgebraMath.MaxMatrixDim; // 16
    private const int MaxKktDim = MaxConstrainedDim * 2;                     // 32
    private const int MaxSimplexPivots = 500;
    private const int MaxAdmmIterations = 2_000;
    private const int MaxAdmmRefactorizations = 8;
    private const int AdmmRhoUpdateInterval = 25;
    private const double AdmmOverRelaxation = 1.5;
    private const double FixedVariableRelTol = 1e-14;

    /// <summary>
    /// Minimizes <c>c^T x</c> subject to <c>A x &lt;= b</c> and <c>lb &lt;= x &lt;= ub</c>
    /// via box-shifted Two-Phase Tableau Simplex with Bland's anti-cycling rule (<c>1 &lt;= n &lt;= 16</c>, <c>0 &lt;= m &lt;= 16</c>).
    /// </summary>
    public static MathErrorCode OptLp(
        scoped ReadOnlySpan<double> c,
        scoped ReadOnlySpan<double> a,
        scoped ReadOnlySpan<double> b,
        scoped ReadOnlySpan<double> lb,
        scoped ReadOnlySpan<double> ub,
        int n,
        int m,
        double tol,
        scoped Span<double> xOut,
        ref DslExecutionBudget budget)
    {
        if (n == 0)
        {
            return MathErrorCode.DomainError;
        }

        if (n < 1 ||
            n > MaxConstrainedDim ||
            m < 0 ||
            m > MaxConstrainedDim ||
            c.Length != n ||
            lb.Length != n ||
            ub.Length != n ||
            b.Length != m ||
            a.Length != m * n ||
            xOut.Length < n)
        {
            return MathErrorCode.DimensionMismatch;
        }

        if (!double.IsFinite(tol) || tol < DslTolerance.MinTol || tol > DslTolerance.MaxTol)
        {
            return MathErrorCode.DomainError;
        }

        if (DslLinearAlgebraMath.ValidateFinite(c) != MathErrorCode.None ||
            DslLinearAlgebraMath.ValidateFinite(a) != MathErrorCode.None ||
            DslLinearAlgebraMath.ValidateFinite(b) != MathErrorCode.None ||
            DslLinearAlgebraMath.ValidateFinite(lb) != MathErrorCode.None ||
            DslLinearAlgebraMath.ValidateFinite(ub) != MathErrorCode.None)
        {
            return MathErrorCode.NonFiniteResult;
        }

        for (int j = 0; j < n; j++)
        {
            if (lb[j] > ub[j])
            {
                return MathErrorCode.DomainError;
            }
        }

        if (m == 0)
        {
            for (int j = 0; j < n; j++)
            {
                double val = c[j] > 0.0
                    ? lb[j]
                    : (c[j] < 0.0 ? ub[j] : Math.Clamp(0.0, lb[j], ub[j]));
                xOut[j] = val == 0.0 ? 0.0 : val;
            }

            return MathErrorCode.None;
        }

        // Shift variables y = x - lb >= 0 with upper bounds u = ub - lb >= 0,
        // and equilibrate inequality rows (0..m-1) before building the simplex tableau.
        Span<double> hVec = stackalloc double[MaxConstrainedDim * 2];
        Span<double> rowScale = stackalloc double[m];
        int totalRows = m + n;
        double maxAbsH = 0.0;
        for (int i = 0; i < m; i++)
        {
            double bPrime = b[i];
            double rInf = 0.0;
            int rowOff = i * n;
            for (int j = 0; j < n; j++)
            {
                double aij = a[rowOff + j];
                double absAij = Math.Abs(aij);
                if (absAij > rInf) rInf = absAij;
                bPrime -= aij * lb[j];
            }

            double absBPrime = Math.Abs(bPrime);
            if (absBPrime > rInf) rInf = absBPrime;
            double s = rInf > DslTolerance.MinTol ? rInf : 1.0;
            rowScale[i] = s;

            double hScaled = bPrime / s;
            if (hScaled < 0.0 && hScaled >= -1e-14)
            {
                hScaled = 0.0;
            }

            hVec[i] = hScaled;
            double absVal = Math.Abs(hScaled);
            if (absVal > maxAbsH) maxAbsH = absVal;
        }

        for (int j = 0; j < n; j++)
        {
            double uj = ub[j] - lb[j];
            hVec[m + j] = uj;
            if (uj > maxAbsH) maxAbsH = uj;
        }

        int nArt = 0;
        for (int i = 0; i < totalRows; i++)
        {
            if (hVec[i] < 0.0)
            {
                nArt++;
            }
        }

        int numVars = n + totalRows + nArt;
        int stride = numVars + 1;
        int tableSize = (totalRows + 1) * stride;

        double[]? rentedTab = null;
        Span<double> tab = tableSize <= MathBufferLimits.StackAllocThreshold
            ? stackalloc double[MathBufferLimits.StackAllocThreshold].Slice(0, tableSize)
            : (rentedTab = ArrayPool<double>.Shared.Rent(tableSize)).AsSpan(0, tableSize);

        try
        {
            tab.Clear();
            Span<int> basis = stackalloc int[totalRows];
            Span<bool> activeRow = stackalloc bool[totalRows];
            activeRow.Fill(true);
            int artIdx = 0;
            int structuralVars = n + totalRows;

            for (int i = 0; i < totalRows; i++)
            {
                int rowBase = i * stride;
                double hi = hVec[i];
                bool negative = hi < 0.0;
                double sign = negative ? -1.0 : 1.0;

                if (i < m)
                {
                    int aRow = i * n;
                    double invScale = sign / rowScale[i];
                    for (int j = 0; j < n; j++)
                    {
                        tab[rowBase + j] = a[aRow + j] * invScale;
                    }
                }
                else
                {
                    tab[rowBase + (i - m)] = sign;
                }

                tab[rowBase + n + i] = sign;
                if (negative)
                {
                    int artCol = structuralVars + artIdx;
                    tab[rowBase + artCol] = 1.0;
                    tab[rowBase + numVars] = -hi;
                    basis[i] = artCol;
                    artIdx++;
                }
                else
                {
                    tab[rowBase + numVars] = hi;
                    basis[i] = n + i;
                }
            }

            int objRow = totalRows * stride;
            if (nArt > 0)
            {
                for (int k = structuralVars; k < numVars; k++)
                {
                    tab[objRow + k] = 1.0;
                }

                for (int i = 0; i < totalRows; i++)
                {
                    if (basis[i] >= structuralVars)
                    {
                        int rowBase = i * stride;
                        for (int k = 0; k <= numVars; k++)
                        {
                            tab[objRow + k] -= tab[rowBase + k];
                        }
                    }
                }

                MathErrorCode phase1Err = RunSimplexBland(
                    tab,
                    basis,
                    activeRow,
                    totalRows,
                    numVars,
                    activeCols: numVars,
                    stride,
                    enterTol: 1e-11,
                    ref budget);
                if (phase1Err != MathErrorCode.None)
                {
                    return phase1Err;
                }

                double artificialSum = -tab[objRow + numVars];
                if (artificialSum > tol * Math.Max(1.0, maxAbsH))
                {
                    return MathErrorCode.ConvergenceFailure;
                }

                // Drive degenerate artificial variables out of the basis; permanently invalidate zeroed redundant rows.
                for (int p = 0; p < totalRows; p++)
                {
                    if (basis[p] < structuralVars)
                    {
                        continue;
                    }

                    int rowBase = p * stride;
                    int bestCol = -1;
                    double bestAbs = 1e-10;
                    for (int q = 0; q < structuralVars; q++)
                    {
                        double absVal = Math.Abs(tab[rowBase + q]);
                        if (absVal > bestAbs)
                        {
                            bestAbs = absVal;
                            bestCol = q;
                        }
                    }

                    if (bestCol >= 0)
                    {
                        PivotTableau(tab, totalRows, numVars, stride, p, bestCol);
                        basis[p] = bestCol;
                    }
                    else
                    {
                        tab.Slice(rowBase, numVars + 1).Clear();
                        activeRow[p] = false;
                    }
                }
            }

            // Phase II: canonicalize normalized objective over non-artificial columns only.
            double cNormInf = InfinityNorm(c, n);
            double cScale = cNormInf > DslTolerance.MinTol ? cNormInf : 1.0;
            tab.Slice(objRow, stride).Clear();
            for (int j = 0; j < n; j++)
            {
                tab[objRow + j] = c[j] / cScale;
            }

            for (int i = 0; i < totalRows; i++)
            {
                if (!activeRow[i])
                {
                    continue;
                }

                int bCol = basis[i];
                if (bCol >= structuralVars)
                {
                    continue;
                }

                double coef = tab[objRow + bCol];
                if (coef != 0.0)
                {
                    int rowBase = i * stride;
                    for (int k = 0; k < structuralVars; k++)
                    {
                        tab[objRow + k] -= coef * tab[rowBase + k];
                    }

                    tab[objRow + numVars] -= coef * tab[rowBase + numVars];
                    tab[objRow + bCol] = 0.0;
                }
            }

            const double phase2EnterTol = 1e-11;
            MathErrorCode phase2Err = RunSimplexBland(
                tab,
                basis,
                activeRow,
                totalRows,
                numVars,
                activeCols: structuralVars,
                stride,
                phase2EnterTol,
                ref budget);
            if (phase2Err != MathErrorCode.None)
            {
                return phase2Err;
            }

            Span<double> x = stackalloc double[n];
            for (int j = 0; j < n; j++)
            {
                x[j] = lb[j];
            }

            for (int i = 0; i < totalRows; i++)
            {
                if (!activeRow[i])
                {
                    continue;
                }

                int bCol = basis[i];
                if ((uint)bCol < (uint)n)
                {
                    x[bCol] += tab[i * stride + numVars];
                }
            }

            for (int j = 0; j < n; j++)
            {
                x[j] = Math.Clamp(x[j], lb[j], ub[j]);
            }

            double aNormInf = DslLinearAlgebraMath.ComputeInfinityNorm(a, m, n);
            double bNormInf = InfinityNorm(b, m);
            double xNormInf = InfinityNorm(x, n);
            double feasTol = Math.Max(tol, 1e-7) * Math.Max(1.0, Math.Max(bNormInf, aNormInf * xNormInf));
            for (int i = 0; i < m; i++)
            {
                double rowSum = 0.0;
                int rowOff = i * n;
                for (int j = 0; j < n; j++)
                {
                    rowSum += a[rowOff + j] * x[j];
                }

                if (rowSum - b[i] > feasTol || !double.IsFinite(rowSum))
                {
                    return MathErrorCode.ConvergenceFailure;
                }
            }

            for (int j = 0; j < n; j++)
            {
                xOut[j] = x[j] == 0.0 ? 0.0 : x[j];
            }

            return MathErrorCode.None;
        }
        finally
        {
            if (rentedTab is not null)
            {
                ArrayPool<double>.Shared.Return(rentedTab, clearArray: false);
            }
        }
    }

    private static MathErrorCode RunSimplexBland(
        scoped Span<double> tab,
        scoped Span<int> basis,
        scoped ReadOnlySpan<bool> activeRow,
        int totalRows,
        int rhsCol,
        int activeCols,
        int stride,
        double enterTol,
        ref DslExecutionBudget budget)
    {
        int objRow = totalRows * stride;
        for (int iter = 0; iter < MaxSimplexPivots; iter++)
        {
            int enterCol = -1;
            for (int q = 0; q < activeCols; q++)
            {
                if (tab[objRow + q] < -enterTol)
                {
                    enterCol = q;
                    break;
                }
            }

            if (enterCol < 0)
            {
                return MathErrorCode.None;
            }

            if (!budget.TryConsume(1))
            {
                return MathErrorCode.BudgetExceeded;
            }

            int leaveRow = -1;
            double minRatio = double.PositiveInfinity;
            for (int p = 0; p < totalRows; p++)
            {
                if (!activeRow[p])
                {
                    continue;
                }

                int rowBase = p * stride;
                double pivot = tab[rowBase + enterCol];
                if (pivot <= 1e-12)
                {
                    continue;
                }

                double rhs = Math.Max(0.0, tab[rowBase + rhsCol]);
                double ratio = rhs / pivot;
                double tieTol = 1e-13 * Math.Max(1.0, minRatio);
                if (leaveRow < 0 ||
                    ratio < minRatio - tieTol ||
                    (Math.Abs(ratio - minRatio) <= tieTol && basis[p] < basis[leaveRow]))
                {
                    minRatio = ratio;
                    leaveRow = p;
                }
            }

            if (leaveRow < 0)
            {
                return MathErrorCode.ConvergenceFailure;
            }

            PivotTableau(tab, totalRows, rhsCol, stride, leaveRow, enterCol);
            basis[leaveRow] = enterCol;
        }

        return MathErrorCode.ConvergenceFailure;
    }

    private static void PivotTableau(
        scoped Span<double> tab,
        int totalRows,
        int rhsCol,
        int stride,
        int leaveRow,
        int enterCol)
    {
        int leaveBase = leaveRow * stride;
        double pivot = tab[leaveBase + enterCol];
        double invPivot = 1.0 / pivot;
        for (int k = 0; k <= rhsCol; k++)
        {
            tab[leaveBase + k] *= invPivot;
        }

        tab[leaveBase + enterCol] = 1.0;

        for (int i = 0; i <= totalRows; i++)
        {
            if (i == leaveRow)
            {
                continue;
            }

            int rowBase = i * stride;
            double factor = tab[rowBase + enterCol];
            if (factor == 0.0)
            {
                continue;
            }

            for (int k = 0; k <= rhsCol; k++)
            {
                tab[rowBase + k] -= factor * tab[leaveBase + k];
            }

            tab[rowBase + enterCol] = 0.0;
        }
    }

    /// <summary>
    /// Minimizes <c>0.5 * x^T H x + c^T x</c> subject to <c>Aeq x = beq</c> and <c>lb &lt;= x &lt;= ub</c>
    /// via scale-normalized over-relaxed ADMM (<c>1 &lt;= n &lt;= 16</c>, <c>1 &lt;= mEq &lt;= n</c>, symmetric PSD <c>H</c>,
    /// full row-rank <c>Aeq</c>, stack-allocated <c>dim &lt;= 32</c> KKT LU solver).
    /// </summary>
    public static MathErrorCode OptQpEq(
        scoped ReadOnlySpan<double> h,
        scoped ReadOnlySpan<double> c,
        scoped ReadOnlySpan<double> aEq,
        scoped ReadOnlySpan<double> bEq,
        scoped ReadOnlySpan<double> lb,
        scoped ReadOnlySpan<double> ub,
        int n,
        int mEq,
        double tol,
        scoped Span<double> xOut,
        ref DslExecutionBudget budget)
    {
        if (n == 0 || mEq == 0)
        {
            return MathErrorCode.DomainError;
        }

        int nn = n * n;
        int mn = mEq * n;
        if (n > MaxConstrainedDim ||
            mEq > n ||
            h.Length != nn ||
            c.Length != n ||
            aEq.Length != mn ||
            bEq.Length != mEq ||
            lb.Length != n ||
            ub.Length != n ||
            xOut.Length < n)
        {
            return MathErrorCode.DimensionMismatch;
        }

        if (!double.IsFinite(tol) || tol < DslTolerance.MinTol || tol > DslTolerance.MaxTol)
        {
            return MathErrorCode.DomainError;
        }

        if (DslLinearAlgebraMath.ValidateFinite(h) != MathErrorCode.None ||
            DslLinearAlgebraMath.ValidateFinite(c) != MathErrorCode.None ||
            DslLinearAlgebraMath.ValidateFinite(aEq) != MathErrorCode.None ||
            DslLinearAlgebraMath.ValidateFinite(bEq) != MathErrorCode.None ||
            DslLinearAlgebraMath.ValidateFinite(lb) != MathErrorCode.None ||
            DslLinearAlgebraMath.ValidateFinite(ub) != MathErrorCode.None)
        {
            return MathErrorCode.NonFiniteResult;
        }

        for (int j = 0; j < n; j++)
        {
            if (lb[j] > ub[j])
            {
                return MathErrorCode.DomainError;
            }
        }

        double hNormF = DslLinearAlgebraMath.ComputeFrobeniusNorm(h, nn);
        double symScale = Math.Max(1.0, hNormF);
        double symTol = DslLinearAlgebraMath.SymmetryTolerance * symScale;
        Span<double> hSym = stackalloc double[nn];
        for (int i = 0; i < n; i++)
        {
            hSym[i * n + i] = h[i * n + i];
            for (int j = i + 1; j < n; j++)
            {
                double hij = h[i * n + j];
                double hji = h[j * n + i];
                if (Math.Abs(hij - hji) > symTol)
                {
                    return MathErrorCode.DomainError;
                }

                double avg = 0.5 * (hij + hji);
                hSym[i * n + j] = avg;
                hSym[j * n + i] = avg;
            }
        }

        Span<double> evals = stackalloc double[n];
        Span<double> evecs = stackalloc double[nn];
        MathErrorCode eigErr = DslLinearAlgebraMath.MatEigenSym(hSym, n, evals, evecs, ref budget);
        if (eigErr != MathErrorCode.None)
        {
            return eigErr;
        }

        if (evals[n - 1] < -symTol)
        {
            return MathErrorCode.DomainError;
        }

        // Step 1: Check all original aEq rows for zero L2 norm before fixed-variable elimination (SingularMatrix).
        for (int i = 0; i < mEq; i++)
        {
            int rowOff = i * n;
            double rowSq = 0.0;
            for (int j = 0; j < n; j++)
            {
                double v = aEq[rowOff + j];
                rowSq += v * v;
            }

            double rowNorm = Math.Sqrt(rowSq);
            if (rowNorm <= DslLinearAlgebraMath.ZeroNormEpsilon || !double.IsFinite(rowNorm))
            {
                return MathErrorCode.SingularMatrix;
            }
        }

        // Step 2: Pre-check original box bounds against every equality constraint (ConvergenceFailure).
        double xBoxInf = 0.0;
        for (int j = 0; j < n; j++)
        {
            double aj = Math.Max(Math.Abs(lb[j]), Math.Abs(ub[j]));
            if (aj > xBoxInf) xBoxInf = aj;
        }

        double aEqInf = DslLinearAlgebraMath.ComputeInfinityNorm(aEq, mEq, n);
        double bEqInf = InfinityNorm(bEq, mEq);
        for (int i = 0; i < mEq; i++)
        {
            int rowOff = i * n;
            double rowMin = 0.0;
            double rowMax = 0.0;
            double rowL1 = 0.0;
            for (int j = 0; j < n; j++)
            {
                double aij = aEq[rowOff + j];
                rowL1 += Math.Abs(aij);
                if (aij >= 0.0)
                {
                    rowMin += aij * lb[j];
                    rowMax += aij * ub[j];
                }
                else
                {
                    rowMin += aij * ub[j];
                    rowMax += aij * lb[j];
                }
            }

            double rowScale = Math.Max(1.0, Math.Max(Math.Abs(bEq[i]), rowL1 * xBoxInf));
            double rowTol = tol * rowScale;
            if (bEq[i] < rowMin - rowTol || bEq[i] > rowMax + rowTol)
            {
                return MathErrorCode.ConvergenceFailure;
            }
        }

        // Step 3: Partition fixed variables F vs. free variables U and resolve fixed-only rows.
        Span<bool> isFixed = stackalloc bool[n];
        Span<int> freeIndices = stackalloc int[n];
        Span<double> xFix = stackalloc double[n];
        int nFree = 0;
        for (int j = 0; j < n; j++)
        {
            double fixScale = Math.Max(1.0, Math.Max(Math.Abs(lb[j]), Math.Abs(ub[j])));
            if (ub[j] - lb[j] <= FixedVariableRelTol * fixScale)
            {
                isFixed[j] = true;
                xFix[j] = 0.5 * (lb[j] + ub[j]);
            }
            else
            {
                isFixed[j] = false;
                xFix[j] = 0.0;
                freeIndices[nFree++] = j;
            }
        }

        if (nFree == 0)
        {
            double xFixInf = InfinityNorm(xFix, n);
            double fixedEqStop = tol * Math.Max(1.0, Math.Max(bEqInf, aEqInf * xFixInf));
            for (int i = 0; i < mEq; i++)
            {
                int rowOff = i * n;
                double dot = 0.0;
                for (int j = 0; j < n; j++)
                {
                    dot += aEq[rowOff + j] * xFix[j];
                }

                if (!double.IsFinite(dot) || Math.Abs(dot - bEq[i]) > fixedEqStop)
                {
                    return MathErrorCode.ConvergenceFailure;
                }
            }

            for (int j = 0; j < n; j++)
            {
                xOut[j] = xFix[j] == 0.0 ? 0.0 : xFix[j];
            }

            return MathErrorCode.None;
        }

        double xFixNormInf = InfinityNorm(xFix, n);
        Span<double> aEqFreeScaled = stackalloc double[mEq * nFree];
        Span<double> bEqFreeScaled = stackalloc double[mEq];
        int mEqEff = 0;

        for (int i = 0; i < mEq; i++)
        {
            int rowOff = i * n;
            double bRed = bEq[i];
            double fixedL1 = 0.0;
            double rowL1 = 0.0;
            for (int j = 0; j < n; j++)
            {
                double aij = aEq[rowOff + j];
                rowL1 += Math.Abs(aij);
                if (isFixed[j])
                {
                    double prod = aij * xFix[j];
                    bRed -= prod;
                    fixedL1 += Math.Abs(prod);
                }
            }

            double rowSqU = 0.0;
            for (int u = 0; u < nFree; u++)
            {
                double v = aEq[rowOff + freeIndices[u]];
                rowSqU += v * v;
            }

            double rowNormU = Math.Sqrt(rowSqU);
            if (rowNormU <= DslLinearAlgebraMath.ZeroNormEpsilon)
            {
                double rowScaleF = Math.Max(1.0, Math.Max(Math.Abs(bEq[i]), Math.Max(fixedL1, rowL1 * xFixNormInf)));
                if (!double.IsFinite(bRed) || Math.Abs(bRed) > tol * rowScaleF)
                {
                    return MathErrorCode.ConvergenceFailure;
                }

                continue;
            }

            double invNormU = 1.0 / rowNormU;
            int outRowOff = mEqEff * nFree;
            for (int u = 0; u < nFree; u++)
            {
                aEqFreeScaled[outRowOff + u] = aEq[rowOff + freeIndices[u]] * invNormU;
            }

            bEqFreeScaled[mEqEff] = bRed * invNormU;
            mEqEff++;
        }

        // Step 4: Verify full row rank over free subspace U via Gram matrix Cholesky (SingularMatrix).
        if (mEqEff > nFree)
        {
            return MathErrorCode.SingularMatrix;
        }

        if (mEqEff > 0)
        {
            Span<double> gram = stackalloc double[mEqEff * mEqEff];
            Span<double> chol = stackalloc double[mEqEff * mEqEff];
            for (int i = 0; i < mEqEff; i++)
            {
                int rowI = i * nFree;
                for (int j = i; j < mEqEff; j++)
                {
                    int rowJ = j * nFree;
                    double dot = 0.0;
                    for (int k = 0; k < nFree; k++)
                    {
                        dot += aEqFreeScaled[rowI + k] * aEqFreeScaled[rowJ + k];
                    }

                    gram[i * mEqEff + j] = dot;
                    gram[j * mEqEff + i] = dot;
                }
            }

            MathErrorCode rankErr = DslLinearAlgebraMath.MatChol(gram, mEqEff, chol, ref budget);
            if (rankErr == MathErrorCode.BudgetExceeded)
            {
                return MathErrorCode.BudgetExceeded;
            }

            if (rankErr != MathErrorCode.None)
            {
                return MathErrorCode.SingularMatrix;
            }
        }

        // Assemble reduced quadratic objective over U: H_UU, c_U + H_UF * x_F.
        int nfSq = nFree * nFree;
        Span<double> hRed = stackalloc double[nfSq];
        Span<double> cRed = stackalloc double[nFree];
        Span<double> lbRed = stackalloc double[nFree];
        Span<double> ubRed = stackalloc double[nFree];
        for (int u = 0; u < nFree; u++)
        {
            int ju = freeIndices[u];
            lbRed[u] = lb[ju];
            ubRed[u] = ub[ju];

            double cShift = c[ju];
            for (int j = 0; j < n; j++)
            {
                if (isFixed[j])
                {
                    cShift += hSym[ju * n + j] * xFix[j];
                }
            }

            cRed[u] = cShift;
            int outRow = u * nFree;
            int srcRow = ju * n;
            for (int v = 0; v < nFree; v++)
            {
                hRed[outRow + v] = hSym[srcRow + freeIndices[v]];
            }
        }

        double hRedNormF = DslLinearAlgebraMath.ComputeFrobeniusNorm(hRed, nfSq);
        double cRedInf = InfinityNorm(cRed, nFree);
        double objScaleRaw = Math.Max(hRedNormF, cRedInf);
        double sObj = objScaleRaw > DslTolerance.MinTol ? objScaleRaw : 1.0;

        Span<double> hHat = stackalloc double[nfSq];
        Span<double> cHat = stackalloc double[nFree];
        for (int i = 0; i < nfSq; i++)
        {
            hHat[i] = hRed[i] / sObj;
        }

        for (int u = 0; u < nFree; u++)
        {
            cHat[u] = cRed[u] / sObj;
        }

        double hHatNormF = DslLinearAlgebraMath.ComputeFrobeniusNorm(hHat, nfSq);
        double hHatInf = DslLinearAlgebraMath.ComputeInfinityNorm(hHat, nFree, nFree);
        double cHatInf = InfinityNorm(cHat, nFree);

        double rho = Math.Max(0.1, hHatNormF / Math.Sqrt(nFree));
        int kktDim = nFree + mEqEff;
        int kktSize = kktDim * kktDim;

        double[]? rentedKkt = null;
        double[]? rentedLu = null;
        Span<double> kkt = kktSize <= MathBufferLimits.StackAllocThreshold
            ? stackalloc double[MathBufferLimits.StackAllocThreshold].Slice(0, kktSize)
            : (rentedKkt = ArrayPool<double>.Shared.Rent(kktSize)).AsSpan(0, kktSize);
        Span<double> lu = kktSize <= MathBufferLimits.StackAllocThreshold
            ? stackalloc double[MathBufferLimits.StackAllocThreshold].Slice(0, kktSize)
            : (rentedLu = ArrayPool<double>.Shared.Rent(kktSize)).AsSpan(0, kktSize);

        try
        {
            Span<int> perm = stackalloc int[kktDim];
            MathErrorCode factErr = AssembleAndFactorKkt(hHat, aEqFreeScaled.Slice(0, mEqEff * nFree), nFree, mEqEff, rho, kkt, lu, perm, ref budget);
            if (factErr != MathErrorCode.None)
            {
                return factErr;
            }

            Span<double> z = stackalloc double[nFree];
            Span<double> zNew = stackalloc double[nFree];
            Span<double> uDual = stackalloc double[nFree];
            for (int u = 0; u < nFree; u++)
            {
                z[u] = Math.Clamp(0.0, lbRed[u], ubRed[u]);
                uDual[u] = 0.0;
            }

            Span<double> rhs = stackalloc double[kktDim];
            Span<double> sol = stackalloc double[kktDim];
            Span<double> xFull = stackalloc double[n];
            xFix.CopyTo(xFull);
            int refactors = 0;

            for (int iter = 1; iter <= MaxAdmmIterations; iter++)
            {
                if ((iter & 7) == 1 && !budget.TryConsume(1))
                {
                    return MathErrorCode.BudgetExceeded;
                }

                for (int u = 0; u < nFree; u++)
                {
                    rhs[u] = -cHat[u] + rho * (z[u] - uDual[u]);
                }

                for (int i = 0; i < mEqEff; i++)
                {
                    rhs[nFree + i] = bEqFreeScaled[i];
                }

                SolveKktLu(lu, kktDim, perm, rhs, sol);

                double rp = 0.0;
                double rd = 0.0;
                double zNewInf = 0.0;
                for (int u = 0; u < nFree; u++)
                {
                    double xj = sol[u];
                    double xHat = AdmmOverRelaxation * xj + (1.0 - AdmmOverRelaxation) * z[u];
                    double zjNew = Math.Clamp(xHat + uDual[u], lbRed[u], ubRed[u]);
                    uDual[u] = uDual[u] + xHat - zjNew;

                    double pDiff = Math.Abs(xj - zjNew);
                    if (pDiff > rp) rp = pDiff;
                    double dDiff = rho * Math.Abs(zjNew - z[u]);
                    if (dDiff > rd) rd = dDiff;
                    double az = Math.Abs(zjNew);
                    if (az > zNewInf) zNewInf = az;

                    zNew[u] = zjNew;
                    xFull[freeIndices[u]] = zjNew;
                }

                double xFullInf = Math.Max(xFixNormInf, zNewInf);
                double req = 0.0;
                for (int i = 0; i < mEq; i++)
                {
                    int rowOff = i * n;
                    double dot = 0.0;
                    for (int j = 0; j < n; j++)
                    {
                        dot += aEq[rowOff + j] * xFull[j];
                    }

                    double eDiff = Math.Abs(dot - bEq[i]);
                    if (eDiff > req) req = eDiff;
                }

                if (!double.IsFinite(rp) || !double.IsFinite(rd) || !double.IsFinite(req))
                {
                    return MathErrorCode.NonFiniteResult;
                }

                double pStop = tol * Math.Max(1.0, zNewInf);
                double dStop = tol * Math.Max(1.0, Math.Max(cHatInf, hHatInf * zNewInf));
                double eqStop = tol * Math.Max(1.0, Math.Max(bEqInf, aEqInf * xFullInf));
                if (rp <= pStop && rd <= dStop && req <= eqStop)
                {
                    double boxErr = 0.0;
                    for (int j = 0; j < n; j++)
                    {
                        double xj = xFull[j];
                        if (!double.IsFinite(xj))
                        {
                            return MathErrorCode.NonFiniteResult;
                        }

                        if (xj < lb[j]) boxErr = Math.Max(boxErr, lb[j] - xj);
                        else if (xj > ub[j]) boxErr = Math.Max(boxErr, xj - ub[j]);
                    }

                    if (boxErr == 0.0)
                    {
                        for (int j = 0; j < n; j++)
                        {
                            xOut[j] = xFull[j] == 0.0 ? 0.0 : xFull[j];
                        }

                        return MathErrorCode.None;
                    }
                }

                zNew.CopyTo(z);

                if (iter % AdmmRhoUpdateInterval == 0 && refactors < MaxAdmmRefactorizations)
                {
                    if (rp > 10.0 * rd)
                    {
                        rho *= 2.0;
                        for (int u = 0; u < nFree; u++) uDual[u] *= 0.5;
                        factErr = AssembleAndFactorKkt(hHat, aEqFreeScaled.Slice(0, mEqEff * nFree), nFree, mEqEff, rho, kkt, lu, perm, ref budget);
                        if (factErr != MathErrorCode.None) return factErr;
                        refactors++;
                    }
                    else if (rd > 10.0 * rp)
                    {
                        rho *= 0.5;
                        for (int u = 0; u < nFree; u++) uDual[u] *= 2.0;
                        factErr = AssembleAndFactorKkt(hHat, aEqFreeScaled.Slice(0, mEqEff * nFree), nFree, mEqEff, rho, kkt, lu, perm, ref budget);
                        if (factErr != MathErrorCode.None) return factErr;
                        refactors++;
                    }
                }
            }

            return MathErrorCode.ConvergenceFailure;
        }
        finally
        {
            if (rentedKkt is not null)
            {
                ArrayPool<double>.Shared.Return(rentedKkt, clearArray: false);
            }

            if (rentedLu is not null)
            {
                ArrayPool<double>.Shared.Return(rentedLu, clearArray: false);
            }
        }
    }

    private static MathErrorCode AssembleAndFactorKkt(
        scoped ReadOnlySpan<double> hHat,
        scoped ReadOnlySpan<double> aEqScaled,
        int n,
        int mEq,
        double rho,
        scoped Span<double> kkt,
        scoped Span<double> lu,
        scoped Span<int> perm,
        ref DslExecutionBudget budget)
    {
        int dim = n + mEq;
        kkt.Slice(0, dim * dim).Clear();

        for (int i = 0; i < n; i++)
        {
            int kktRow = i * dim;
            int hRow = i * n;
            for (int j = 0; j < n; j++)
            {
                kkt[kktRow + j] = hHat[hRow + j];
            }

            kkt[kktRow + i] += rho;
        }

        for (int i = 0; i < mEq; i++)
        {
            int aRow = i * n;
            int eqKktRow = (n + i) * dim;
            for (int j = 0; j < n; j++)
            {
                double aij = aEqScaled[aRow + j];
                kkt[j * dim + (n + i)] = aij;
                kkt[eqKktRow + j] = aij;
            }
        }

        return FactorKktLu(kkt, dim, lu, perm, ref budget);
    }

    private static MathErrorCode FactorKktLu(
        scoped ReadOnlySpan<double> kkt,
        int dim,
        scoped Span<double> lu,
        scoped Span<int> perm,
        ref DslExecutionBudget budget)
    {
        int ops = Math.Max(1, (dim * dim * dim) / (3 * DslExecutionBudget.CheapOperationsPerUnit));
        if (!budget.TryConsume(ops))
        {
            return MathErrorCode.BudgetExceeded;
        }

        int dd = dim * dim;
        kkt.Slice(0, dd).CopyTo(lu);
        for (int i = 0; i < dim; i++)
        {
            perm[i] = i;
        }

        double pivotScale = DslLinearAlgebraMath.PivotTolerance * Math.Max(1.0, DslLinearAlgebraMath.ComputeInfinityNorm(kkt, dim, dim));
        for (int k = 0; k < dim; k++)
        {
            int pivotRow = k;
            double maxAbs = Math.Abs(lu[k * dim + k]);
            for (int i = k + 1; i < dim; i++)
            {
                double cand = Math.Abs(lu[i * dim + k]);
                if (cand > maxAbs)
                {
                    maxAbs = cand;
                    pivotRow = i;
                }
            }

            if (maxAbs <= pivotScale || !double.IsFinite(maxAbs))
            {
                return MathErrorCode.SingularMatrix;
            }

            if (pivotRow != k)
            {
                int kRow = k * dim;
                int pRow = pivotRow * dim;
                for (int j = 0; j < dim; j++)
                {
                    (lu[kRow + j], lu[pRow + j]) = (lu[pRow + j], lu[kRow + j]);
                }

                (perm[k], perm[pivotRow]) = (perm[pivotRow], perm[k]);
            }

            double diag = lu[k * dim + k];
            for (int i = k + 1; i < dim; i++)
            {
                int iRow = i * dim;
                int kRow = k * dim;
                double multiplier = lu[iRow + k] / diag;
                lu[iRow + k] = multiplier;
                for (int j = k + 1; j < dim; j++)
                {
                    lu[iRow + j] -= multiplier * lu[kRow + j];
                }
            }
        }

        return MathErrorCode.None;
    }

    private static void SolveKktLu(
        scoped ReadOnlySpan<double> lu,
        int dim,
        scoped ReadOnlySpan<int> perm,
        scoped ReadOnlySpan<double> rhs,
        scoped Span<double> sol)
    {
        for (int i = 0; i < dim; i++)
        {
            double sum = rhs[perm[i]];
            int rowOff = i * dim;
            for (int j = 0; j < i; j++)
            {
                sum -= lu[rowOff + j] * sol[j];
            }

            sol[i] = sum;
        }

        for (int i = dim - 1; i >= 0; i--)
        {
            double sum = sol[i];
            int rowOff = i * dim;
            for (int j = i + 1; j < dim; j++)
            {
                sum -= lu[rowOff + j] * sol[j];
            }

            sol[i] = sum / lu[rowOff + i];
        }
    }
}
