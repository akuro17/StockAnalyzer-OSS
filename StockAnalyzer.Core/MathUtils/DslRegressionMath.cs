using System;
using System.Buffers;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Zero-allocation weighted statistics, Mahalanobis distance, centered/column-scaled OLS and Ridge regression
/// (delegating to <see cref="SmallMatrixMath"/>), causal polynomial extrapolation, and PCA projection
/// (delegating to <see cref="DslLinearAlgebraMath.MatEigenSym"/>).
/// All flattened 2D matrices use Row-Major layout (<c>row * cols + col</c>).
/// </summary>
public static class DslRegressionMath
{
    public const int MaxFeatures = DslLinearAlgebraMath.MaxMatrixDim; // 16
    public const int MaxObservations = DslArrayArena.MaxArrayLength;  // 512
    public const int MaxPolyDegree = 6;
    public const int MaxPolyHorizon = 10;
    public const double MinRidgeLambda = 1e-12;

    // ---- Weighted Mean & Variance ----

    private static MathErrorCode TryGetMaxNonNegativeWeight(ReadOnlySpan<decimal> weights, out decimal maxW)
    {
        maxW = 0m;
        for (int i = 0; i < weights.Length; i++)
        {
            decimal w = weights[i];
            if (w < 0m)
            {
                return MathErrorCode.DomainError;
            }

            if (w > maxW)
            {
                maxW = w;
            }
        }

        if (maxW <= 0m)
        {
            return MathErrorCode.DivisionByZero;
        }

        return MathErrorCode.None;
    }

    public static MathResult<decimal> WeightedMean(ReadOnlySpan<decimal> weights, ReadOnlySpan<decimal> values)
    {
        if (weights.Length != values.Length)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DimensionMismatch);
        }

        int n = weights.Length;
        if (n < 1 || n > MaxObservations)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        MathErrorCode wErr = TryGetMaxNonNegativeWeight(weights, out decimal maxW);
        if (wErr != MathErrorCode.None)
        {
            return MathResult<decimal>.Fail(wErr);
        }

        try
        {
            decimal sumW = 0m;
            decimal sumWx = 0m;
            for (int i = 0; i < n; i++)
            {
                decimal nw = weights[i] / maxW;
                sumW = checked(sumW + nw);
                sumWx = checked(sumWx + checked(nw * values[i]));
            }

            decimal mean = checked(sumWx / sumW);
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(mean));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<decimal> WeightedVariance(ReadOnlySpan<decimal> weights, ReadOnlySpan<decimal> values)
    {
        MathResult<decimal> meanRes = WeightedMean(weights, values);
        if (!meanRes.IsOk)
        {
            return meanRes;
        }

        MathErrorCode wErr = TryGetMaxNonNegativeWeight(weights, out decimal maxW);
        if (wErr != MathErrorCode.None)
        {
            return MathResult<decimal>.Fail(wErr);
        }

        decimal mean = meanRes.Value;
        int n = weights.Length;
        try
        {
            decimal sumW = 0m;
            decimal sumWDevSq = 0m;
            for (int i = 0; i < n; i++)
            {
                decimal nw = weights[i] / maxW;
                decimal dev = checked(values[i] - mean);
                sumW = checked(sumW + nw);
                sumWDevSq = checked(sumWDevSq + checked(nw * checked(dev * dev)));
            }

            decimal variance = checked(sumWDevSq / sumW);
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(variance));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    // ---- Mahalanobis Distance ----

    public static MathResult<double> Mahalanobis(
        scoped ReadOnlySpan<double> x,
        scoped ReadOnlySpan<double> mu,
        scoped ReadOnlySpan<double> covInv,
        int p,
        ref DslExecutionBudget budget)
    {
        if (!DslLinearAlgebraMath.IsValidDim(p) || x.Length != p || mu.Length != p || covInv.Length != p * p)
        {
            return MathResult<double>.Fail(MathErrorCode.DimensionMismatch);
        }

        if (DslLinearAlgebraMath.ValidateFinite(x) != MathErrorCode.None ||
            DslLinearAlgebraMath.ValidateFinite(mu) != MathErrorCode.None ||
            DslLinearAlgebraMath.ValidateFinite(covInv) != MathErrorCode.None)
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        if (!budget.TryConsume(Math.Max(1, p * p)))
        {
            return MathResult<double>.Fail(MathErrorCode.BudgetExceeded);
        }

        double normInf = DslLinearAlgebraMath.ComputeInfinityNorm(covInv, p, p);
        double symTol = DslLinearAlgebraMath.DefaultMatrixTolerance * (normInf > 0.0 ? normInf : 1.0);
        for (int i = 0; i < p; i++)
        {
            for (int j = i + 1; j < p; j++)
            {
                if (Math.Abs(covInv[i * p + j] - covInv[j * p + i]) > symTol)
                {
                    return MathResult<double>.Fail(MathErrorCode.DomainError);
                }
            }
        }

        Span<double> sym = stackalloc double[p * p];
        for (int i = 0; i < p; i++)
        {
            sym[i * p + i] = covInv[i * p + i];
            for (int j = i + 1; j < p; j++)
            {
                double symVal = 0.5 * (covInv[i * p + j] + covInv[j * p + i]);
                sym[i * p + j] = symVal;
                sym[j * p + i] = symVal;
            }
        }

        Span<double> chol = stackalloc double[p * p];
        MathErrorCode cholErr = DslLinearAlgebraMath.MatChol(sym, p, chol, ref budget);
        if (cholErr != MathErrorCode.None)
        {
            return MathResult<double>.Fail(
                cholErr == MathErrorCode.SingularMatrix ? MathErrorCode.DomainError : cholErr);
        }

        Span<double> diff = stackalloc double[p];
        double normSq = 0.0;
        for (int i = 0; i < p; i++)
        {
            double d = x[i] - mu[i];
            diff[i] = d;
            normSq += d * d;
        }

        double quad = 0.0;
        for (int i = 0; i < p; i++)
        {
            double rowDot = 0.0;
            for (int j = 0; j < p; j++)
            {
                rowDot += sym[i * p + j] * diff[j];
            }

            quad += diff[i] * rowDot;
        }

        if (!double.IsFinite(quad))
        {
            return MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
        }

        double negTol = DslLinearAlgebraMath.PivotTolerance * normInf * Math.Max(1.0, normSq);
        if (quad < -negTol)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        double dist = Math.Sqrt(Math.Max(0.0, quad));
        return double.IsFinite(dist)
            ? MathResult<double>.Ok(dist == 0.0 ? 0.0 : dist)
            : MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
    }

    // ---- OLS & Ridge Regression (Centered + Column-Scaled Householder QR) ----

    /// <summary>
    /// Fits OLS regression <c>y = beta_0 + sum_{j=1..p} beta_j x_j</c> using column centering,
    /// standard-deviation scaling, and <see cref="SmallMatrixMath.TryHouseholderQr"/>.
    /// Outputs <c>[beta_0, beta_1, ..., beta_p]</c> into <paramref name="betaOut"/> (length <c>p + 1</c>).
    /// </summary>
    public static MathErrorCode OlsFit(
        scoped ReadOnlySpan<double> x,
        int nRows,
        int nCols,
        scoped ReadOnlySpan<double> y,
        scoped Span<double> betaOut,
        ref DslExecutionBudget budget)
    {
        if (nCols < 1 || nCols > MaxFeatures || nRows > MaxObservations ||
            x.Length != nRows * nCols || y.Length != nRows || betaOut.Length < nCols + 1)
        {
            return MathErrorCode.DimensionMismatch;
        }

        if (nRows < nCols + 2)
        {
            return MathErrorCode.DomainError;
        }

        return FitLinearCore(x, nRows, nCols, y, ridgeLambda: 0.0, isRidge: false, betaOut, ref budget);
    }

    /// <summary>
    /// Fits Ridge regression <c>min ||y - (beta_0 + X beta)||_2^2 + lambda ||beta||_2^2</c> with
    /// column centering/scaling and augmented Householder QR (intercept unpenalized).
    /// </summary>
    public static MathErrorCode RidgeFit(
        scoped ReadOnlySpan<double> x,
        int nRows,
        int nCols,
        scoped ReadOnlySpan<double> y,
        double lambda,
        scoped Span<double> betaOut,
        ref DslExecutionBudget budget)
    {
        if (nCols < 1 || nCols > MaxFeatures || nRows > MaxObservations ||
            x.Length != nRows * nCols || y.Length != nRows || betaOut.Length < nCols + 1)
        {
            return MathErrorCode.DimensionMismatch;
        }

        if (!double.IsFinite(lambda))
        {
            return MathErrorCode.NonFiniteResult;
        }

        if (nRows < 2 || lambda < MinRidgeLambda)
        {
            return MathErrorCode.DomainError;
        }

        return FitLinearCore(x, nRows, nCols, y, ridgeLambda: lambda, isRidge: true, betaOut, ref budget);
    }

    public static MathResult<decimal> OlsPredict(ReadOnlySpan<decimal> xNew, ReadOnlySpan<decimal> beta)
    {
        int p = xNew.Length;
        if (p < 1 || p > MaxFeatures || beta.Length != p + 1)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DimensionMismatch);
        }

        try
        {
            decimal acc = beta[0];
            for (int j = 0; j < p; j++)
            {
                acc = checked(acc + checked(beta[j + 1] * xNew[j]));
            }

            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(acc));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    private static MathErrorCode FitLinearCore(
        scoped ReadOnlySpan<double> x,
        int nRows,
        int nCols,
        scoped ReadOnlySpan<double> y,
        double ridgeLambda,
        bool isRidge,
        scoped Span<double> betaOut,
        ref DslExecutionBudget budget)
    {
        if (DslLinearAlgebraMath.ValidateFinite(x) != MathErrorCode.None ||
            DslLinearAlgebraMath.ValidateFinite(y) != MathErrorCode.None)
        {
            return MathErrorCode.NonFiniteResult;
        }

        int totalRows = isRidge ? nRows + nCols : nRows;
        int opCost = Math.Max(1, totalRows * nCols * nCols / 4);
        if (!budget.TryConsume(opCost))
        {
            return MathErrorCode.BudgetExceeded;
        }

        Span<double> xMean = stackalloc double[nCols];
        Span<double> xScale = stackalloc double[nCols];
        double ySum = 0.0;
        for (int i = 0; i < nRows; i++)
        {
            ySum += y[i];
            int rowBase = i * nCols;
            for (int j = 0; j < nCols; j++)
            {
                xMean[j] += x[rowBase + j];
            }
        }

        double yMean = ySum / nRows;
        for (int j = 0; j < nCols; j++)
        {
            xMean[j] /= nRows;
        }

        for (int i = 0; i < nRows; i++)
        {
            int rowBase = i * nCols;
            for (int j = 0; j < nCols; j++)
            {
                double d = x[rowBase + j] - xMean[j];
                xScale[j] += d * d;
            }
        }

        for (int j = 0; j < nCols; j++)
        {
            double popDev = Math.Sqrt(xScale[j] / nRows);
            double flatThreshold = Math.Max(
                ComparisonSeriesDefaults.FlatWindowDeviationEpsilon,
                WindowMoments.SeriesRelativeFlatEpsilon * Math.Abs(xMean[j]));
            if (!double.IsFinite(popDev) || popDev < flatThreshold)
            {
                if (!isRidge)
                {
                    return MathErrorCode.SingularMatrix;
                }

                xScale[j] = 0.0;
            }
            else
            {
                xScale[j] = popDev;
            }
        }

        int matrixLen = totalRows * nCols;
        int needed = matrixLen + totalRows;
        double[]? rented = needed <= MathBufferLimits.StackAllocThreshold
            ? null
            : ArrayPool<double>.Shared.Rent(needed);
        Span<double> work = rented is null
            ? stackalloc double[needed]
            : rented.AsSpan(0, needed);

        try
        {
            Span<double> mat = work.Slice(0, matrixLen);
            Span<double> rhs = work.Slice(matrixLen, totalRows);
            mat.Clear();
            rhs.Clear();

            for (int i = 0; i < nRows; i++)
            {
                int rowBase = i * nCols;
                for (int j = 0; j < nCols; j++)
                {
                    mat[rowBase + j] = xScale[j] > 0.0
                        ? (x[rowBase + j] - xMean[j]) / xScale[j]
                        : 0.0;
                }

                rhs[i] = y[i] - yMean;
            }

            if (isRidge)
            {
                double sqrtLambda = Math.Sqrt(ridgeLambda);
                for (int j = 0; j < nCols; j++)
                {
                    mat[(nRows + j) * nCols + j] = sqrtLambda;
                }
            }

            if (!SmallMatrixMath.TryHouseholderQr(mat, totalRows, nCols, rhs, out _))
            {
                return MathErrorCode.SingularMatrix;
            }

            Span<double> scaledSlopes = stackalloc double[nCols];
            SmallMatrixMath.SolveUpperTriangular(mat, nCols, rhs, scaledSlopes);

            double intercept = yMean;
            for (int j = 0; j < nCols; j++)
            {
                double slope = xScale[j] > 0.0 ? scaledSlopes[j] / xScale[j] : 0.0;
                if (!double.IsFinite(slope))
                {
                    return MathErrorCode.ArithmeticOverflow;
                }

                betaOut[j + 1] = slope == 0.0 ? 0.0 : slope;
                intercept -= slope * xMean[j];
            }

            if (!double.IsFinite(intercept))
            {
                return MathErrorCode.ArithmeticOverflow;
            }

            betaOut[0] = intercept == 0.0 ? 0.0 : intercept;
            return MathErrorCode.None;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<double>.Shared.Return(rented);
            }
        }
    }

    // ---- Causal Polynomial Extrapolation ----

    /// <summary>
    /// Fits a degree-<paramref name="degree"/> (<c>1..6</c>) polynomial to causal historical series
    /// mapped onto normalized coordinates <c>[-1, 1]</c> and extrapolates <paramref name="horizon"/> (<c>0..10</c>) steps ahead.
    /// </summary>
    public static MathResult<double> PolyProject(
        scoped ReadOnlySpan<double> series,
        int degree,
        int horizon,
        ref DslExecutionBudget budget)
    {
        int n = series.Length;
        if (degree < 1 || degree > MaxPolyDegree || horizon < 0 || horizon > MaxPolyHorizon || n < degree + 2 || n > MaxObservations)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        if (DslLinearAlgebraMath.ValidateFinite(series) != MathErrorCode.None)
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        int cols = degree + 1;
        int opCost = Math.Max(1, n * cols);
        if (!budget.TryConsume(opCost))
        {
            return MathResult<double>.Fail(MathErrorCode.BudgetExceeded);
        }

        int matrixLen = n * cols;
        int needed = matrixLen + n;
        double[]? rented = needed <= MathBufferLimits.StackAllocThreshold
            ? null
            : ArrayPool<double>.Shared.Rent(needed);
        Span<double> work = rented is null
            ? stackalloc double[needed]
            : rented.AsSpan(0, needed);

        try
        {
            Span<double> vandermonde = work.Slice(0, matrixLen);
            Span<double> rhs = work.Slice(matrixLen, n);

            double invSpan = 2.0 / (n - 1);
            for (int i = 0; i < n; i++)
            {
                double t = i * invSpan - 1.0;
                double pow = 1.0;
                int rowBase = i * cols;
                for (int d = 0; d <= degree; d++)
                {
                    vandermonde[rowBase + d] = pow;
                    pow *= t;
                }

                rhs[i] = series[i];
            }

            if (!SmallMatrixMath.TryHouseholderQr(vandermonde, n, cols, rhs, out _))
            {
                return MathResult<double>.Fail(MathErrorCode.SingularMatrix);
            }

            Span<double> coeffs = stackalloc double[cols];
            SmallMatrixMath.SolveUpperTriangular(vandermonde, cols, rhs, coeffs);

            double tTarget = (n - 1 + horizon) * invSpan - 1.0;
            double value = coeffs[degree];
            for (int d = degree - 1; d >= 0; d--)
            {
                value = value * tTarget + coeffs[d];
            }

            return double.IsFinite(value)
                ? MathResult<double>.Ok(value == 0.0 ? 0.0 : value)
                : MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<double>.Shared.Return(rented);
            }
        }
    }

    // ---- Principal Component Analysis (PCA) Projection ----

    /// <summary>
    /// Centers columns of row-major <paramref name="x"/> (<c>nRows x nCols</c>), computes the descending
    /// eigensystem of the sample covariance matrix via <see cref="DslLinearAlgebraMath.MatEigenSym"/>,
    /// enforces deterministic column-sign normalization, and writes the <c>nRows x k</c> score matrix into <paramref name="zOut"/>.
    /// </summary>
    public static MathErrorCode PcaProject(
        scoped ReadOnlySpan<double> x,
        int nRows,
        int nCols,
        int k,
        scoped Span<double> zOut,
        ref DslExecutionBudget budget)
    {
        if (nCols < 1 || nCols > MaxFeatures || nRows > MaxObservations ||
            k < 1 || k > nCols || x.Length != nRows * nCols || zOut.Length < nRows * k)
        {
            return MathErrorCode.DimensionMismatch;
        }

        if (nRows < 2)
        {
            return MathErrorCode.DomainError;
        }

        if (DslLinearAlgebraMath.ValidateFinite(x) != MathErrorCode.None)
        {
            return MathErrorCode.NonFiniteResult;
        }

        int opCost = Math.Max(1, nRows * nCols * nCols / 4);
        if (!budget.TryConsume(opCost))
        {
            return MathErrorCode.BudgetExceeded;
        }

        Span<double> means = stackalloc double[nCols];
        for (int i = 0; i < nRows; i++)
        {
            int rowBase = i * nCols;
            for (int j = 0; j < nCols; j++)
            {
                means[j] += x[rowBase + j];
            }
        }

        for (int j = 0; j < nCols; j++)
        {
            means[j] /= nRows;
        }

        Span<double> cov = stackalloc double[nCols * nCols];
        cov.Clear();
        double invNMinus1 = 1.0 / (nRows - 1);
        for (int i = 0; i < nRows; i++)
        {
            int rowBase = i * nCols;
            for (int c1 = 0; c1 < nCols; c1++)
            {
                double d1 = x[rowBase + c1] - means[c1];
                for (int c2 = c1; c2 < nCols; c2++)
                {
                    cov[c1 * nCols + c2] += d1 * (x[rowBase + c2] - means[c2]);
                }
            }
        }

        for (int c1 = 0; c1 < nCols; c1++)
        {
            for (int c2 = c1; c2 < nCols; c2++)
            {
                double val = cov[c1 * nCols + c2] * invNMinus1;
                cov[c1 * nCols + c2] = val;
                cov[c2 * nCols + c1] = val;
            }
        }

        Span<double> evals = stackalloc double[nCols];
        Span<double> evecs = stackalloc double[nCols * nCols];
        MathErrorCode eigErr = DslLinearAlgebraMath.MatEigenSym(cov, nCols, evals, evecs, ref budget);
        if (eigErr != MathErrorCode.None)
        {
            return eigErr;
        }

        // Deterministic eigenvector sign convention: largest-magnitude loading per component is positive.
        for (int comp = 0; comp < k; comp++)
        {
            int maxRow = 0;
            double maxAbs = Math.Abs(evecs[comp]);
            for (int r = 1; r < nCols; r++)
            {
                double cand = Math.Abs(evecs[r * nCols + comp]);
                if (cand > maxAbs * (1.0 + 1e-12))
                {
                    maxAbs = cand;
                    maxRow = r;
                }
            }

            if (evecs[maxRow * nCols + comp] < 0.0)
            {
                for (int r = 0; r < nCols; r++)
                {
                    evecs[r * nCols + comp] = -evecs[r * nCols + comp];
                }
            }
        }

        for (int i = 0; i < nRows; i++)
        {
            int rowBase = i * nCols;
            int outBase = i * k;
            for (int comp = 0; comp < k; comp++)
            {
                double sum = 0.0;
                for (int j = 0; j < nCols; j++)
                {
                    sum += (x[rowBase + j] - means[j]) * evecs[j * nCols + comp];
                }

                if (!double.IsFinite(sum))
                {
                    return MathErrorCode.ArithmeticOverflow;
                }

                zOut[outBase + comp] = sum == 0.0 ? 0.0 : sum;
            }
        }

        return MathErrorCode.None;
    }
}
