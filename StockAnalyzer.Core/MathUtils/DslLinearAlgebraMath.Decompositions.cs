using System;

namespace StockAnalyzer.Core.MathUtils;

public static partial class DslLinearAlgebraMath
{
    private const double DecompositionEps = 1e-14;

    // ---- Cholesky & QR Decompositions ----

    /// <summary>
    /// Computes the lower-triangular Cholesky factor <c>L</c> (<c>A = L L^T</c>) for symmetric positive-definite <c>n x n</c> matrices.
    /// Enforces symmetry (<c>|A_ij - A_ji| &lt;= 1e-10 * ||A||_F</c> -> <see cref="MathErrorCode.DomainError"/>)
    /// and strict positive-definiteness (<c>diag &lt;= 1e-12</c> -> <see cref="MathErrorCode.SingularMatrix"/>).
    /// </summary>
    public static MathErrorCode MatChol(
        scoped ReadOnlySpan<double> a,
        int n,
        scoped Span<double> lower,
        ref DslExecutionBudget budget)
    {
        int nn = n * n;
        if (!IsValidDim(n) || a.Length < nn || lower.Length < nn)
        {
            return MathErrorCode.DimensionMismatch;
        }

        MathErrorCode finiteErr = ValidateFinite(a.Slice(0, nn));
        if (finiteErr != MathErrorCode.None)
        {
            return finiteErr;
        }

        if (!budget.TryConsume(Math.Max(1, (n * n * n) / 3)))
        {
            return MathErrorCode.BudgetExceeded;
        }

        double normF = ComputeFrobeniusNorm(a, nn);
        if (normF == 0.0)
        {
            return MathErrorCode.SingularMatrix;
        }

        double symErr = ValidateSymmetric(a, n);
        if (symErr > 0.0)
        {
            return MathErrorCode.DomainError;
        }

        Span<double> work = stackalloc double[nn];
        work.Clear();

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j <= i; j++)
            {
                double sum = j == i ? a[i * n + i] : 0.5 * (a[i * n + j] + a[j * n + i]);
                for (int k = 0; k < j; k++)
                {
                    sum -= work[i * n + k] * work[j * n + k];
                }

                if (i == j)
                {
                    if (!double.IsFinite(sum))
                    {
                        return MathErrorCode.ArithmeticOverflow;
                    }

                    double pivotScale = Math.Max(normF, Math.Abs(a[i * n + i]));
                    if (sum <= PivotTolerance * pivotScale)
                    {
                        return MathErrorCode.SingularMatrix;
                    }

                    work[i * n + i] = Math.Sqrt(sum);
                }
                else
                {
                    work[i * n + j] = sum / work[j * n + j];
                }
            }
        }

        work.CopyTo(lower);
        return MathErrorCode.None;
    }

    /// <summary>
    /// Computes the Householder QR factorization <c>A = Q R</c> for <c>rows &gt;= cols</c>.
    /// When <paramref name="fullQ"/> is <c>false</c>, computes thin QR (<c>Q: rows x cols</c>, <c>R: cols x cols</c>);
    /// when <c>true</c>, computes full QR (<c>Q: rows x rows</c>, <c>R: rows x cols</c>).
    /// </summary>
    public static MathErrorCode MatQr(
        scoped ReadOnlySpan<double> a,
        int rows,
        int cols,
        scoped Span<double> q,
        scoped Span<double> r,
        ref DslExecutionBudget budget,
        bool fullQ = false)
    {
        int qCols = fullQ ? rows : cols;
        int rRows = fullQ ? rows : cols;
        if (!IsValidDims(rows, cols) ||
            rows < cols ||
            a.Length < rows * cols ||
            q.Length < rows * qCols ||
            r.Length < rRows * cols)
        {
            return MathErrorCode.DimensionMismatch;
        }

        MathErrorCode finiteErr = ValidateFinite(a.Slice(0, rows * cols));
        if (finiteErr != MathErrorCode.None)
        {
            return finiteErr;
        }

        if (!budget.TryConsume(Math.Max(1, rows * cols * cols)))
        {
            return MathErrorCode.BudgetExceeded;
        }

        Span<double> rWork = stackalloc double[rows * cols];
        Span<double> qWork = stackalloc double[rows * rows];
        a.Slice(0, rows * cols).CopyTo(rWork);
        SetIdentity(qWork, rows);

        double frob = ComputeFrobeniusNorm(rWork, rows * cols);
        double zeroScale = SmallMatrixMath.RankTolerance * frob;

        for (int k = 0; k < cols; k++)
        {
            double normSq = 0.0;
            for (int i = k; i < rows; i++)
            {
                double val = rWork[i * cols + k];
                normSq += val * val;
            }

            double norm = Math.Sqrt(normSq);
            if (norm <= zeroScale || norm == 0.0)
            {
                for (int i = k + 1; i < rows; i++)
                {
                    rWork[i * cols + k] = 0.0;
                }

                continue;
            }

            double alpha = rWork[k * cols + k] >= 0.0 ? -norm : norm;
            double head = rWork[k * cols + k] - alpha;
            double vNormSq = head * head;
            for (int i = k + 1; i < rows; i++)
            {
                double val = rWork[i * cols + k];
                vNormSq += val * val;
            }

            if (vNormSq > 0.0)
            {
                double beta = 2.0 / vNormSq;
                for (int j = k + 1; j < cols; j++)
                {
                    double dot = head * rWork[k * cols + j];
                    for (int i = k + 1; i < rows; i++)
                    {
                        dot += rWork[i * cols + k] * rWork[i * cols + j];
                    }

                    double factor = beta * dot;
                    rWork[k * cols + j] -= factor * head;
                    for (int i = k + 1; i < rows; i++)
                    {
                        rWork[i * cols + j] -= factor * rWork[i * cols + k];
                    }
                }

                // Accumulate Q <- Q * H_k across rows 0..rows-1.
                for (int i = 0; i < rows; i++)
                {
                    int rowOff = i * rows;
                    double dot = qWork[rowOff + k] * head;
                    for (int l = k + 1; l < rows; l++)
                    {
                        dot += qWork[rowOff + l] * rWork[l * cols + k];
                    }

                    double factor = beta * dot;
                    qWork[rowOff + k] -= factor * head;
                    for (int l = k + 1; l < rows; l++)
                    {
                        qWork[rowOff + l] -= factor * rWork[l * cols + k];
                    }
                }
            }

            rWork[k * cols + k] = alpha;
            for (int i = k + 1; i < rows; i++)
            {
                rWork[i * cols + k] = 0.0;
            }
        }

        for (int i = 0; i < rows; i++)
        {
            for (int j = 0; j < qCols; j++)
            {
                q[i * qCols + j] = qWork[i * rows + j];
            }
        }

        for (int i = 0; i < rRows; i++)
        {
            for (int j = 0; j < cols; j++)
            {
                r[i * cols + j] = i <= j ? rWork[i * cols + j] : 0.0;
            }
        }

        return MathErrorCode.None;
    }

    // ---- Symmetric Eigenvalue Decomposition (Cyclic Jacobi, Descending PCA Order) ----

    /// <summary>
    /// Computes all eigenvalues and orthonormal eigenvectors (<c>A * V = V * diag(lambda)</c>, <c>V^T V = I</c>)
    /// of a real symmetric <c>n x n</c> matrix via cyclic Jacobi rotations (max <c>30 * n</c> sweeps).
    /// Eigenvalues and corresponding eigenvector columns (stored row-major in <paramref name="eigenvectors"/>)
    /// are returned in <strong>descending</strong> order (<c>lambda_0 &gt;= lambda_1 &gt;= ... &gt;= lambda_{n-1}</c>)
    /// in accordance with PCA covariance and SVD conventions (unlike ascending LAPACK/NumPy <c>eigh</c>).
    /// </summary>
    public static MathErrorCode MatEigenSym(
        scoped ReadOnlySpan<double> a,
        int n,
        scoped Span<double> eigenvalues,
        scoped Span<double> eigenvectors,
        ref DslExecutionBudget budget)
    {
        int nn = n * n;
        if (!IsValidDim(n) || a.Length < nn || eigenvalues.Length < n || eigenvectors.Length < nn)
        {
            return MathErrorCode.DimensionMismatch;
        }

        MathErrorCode finiteErr = ValidateFinite(a.Slice(0, nn));
        if (finiteErr != MathErrorCode.None)
        {
            return finiteErr;
        }

        double symErr = ValidateSymmetric(a, n);
        if (symErr > 0.0)
        {
            return MathErrorCode.DomainError;
        }

        if (!budget.TryConsume(Math.Max(1, n * n)))
        {
            return MathErrorCode.BudgetExceeded;
        }

        Span<double> d = stackalloc double[nn];
        Span<double> v = stackalloc double[nn];
        for (int i = 0; i < n; i++)
        {
            d[i * n + i] = a[i * n + i];
            for (int j = i + 1; j < n; j++)
            {
                double sym = 0.5 * (a[i * n + j] + a[j * n + i]);
                d[i * n + j] = sym;
                d[j * n + i] = sym;
            }
        }

        SetIdentity(v, n);

        double normF = ComputeFrobeniusNorm(d, nn);
        double stopThreshold = DecompositionEps * Math.Max(1e-30, normF);
        int maxSweeps = 30 * n;
        bool converged = n == 1;

        for (int sweep = 0; sweep < maxSweeps && !converged; sweep++)
        {
            if (!budget.TryConsume(Math.Max(1, n)))
            {
                return MathErrorCode.BudgetExceeded;
            }

            double offMax = 0.0;
            for (int p = 0; p < n - 1; p++)
            {
                for (int q = p + 1; q < n; q++)
                {
                    double apq = Math.Abs(d[p * n + q]);
                    if (apq > offMax)
                    {
                        offMax = apq;
                    }
                }
            }

            if (offMax <= stopThreshold)
            {
                converged = true;
                break;
            }

            for (int p = 0; p < n - 1; p++)
            {
                for (int q = p + 1; q < n; q++)
                {
                    double g = d[p * n + q];
                    if (Math.Abs(g) <= stopThreshold * 0.1)
                    {
                        continue;
                    }

                    double app = d[p * n + p];
                    double aqq = d[q * n + q];
                    double h = aqq - app;
                    double t;
                    if (Math.Abs(h) + 100.0 * Math.Abs(g) == Math.Abs(h))
                    {
                        t = g / h;
                    }
                    else
                    {
                        double theta = 0.5 * h / g;
                        t = 1.0 / (Math.Abs(theta) + Math.Sqrt(1.0 + theta * theta));
                        if (theta < 0.0)
                        {
                            t = -t;
                        }
                    }

                    double c = 1.0 / Math.Sqrt(1.0 + t * t);
                    double s = t * c;
                    double tau = s / (1.0 + c);

                    d[p * n + p] = app - t * g;
                    d[q * n + q] = aqq + t * g;
                    d[p * n + q] = 0.0;
                    d[q * n + p] = 0.0;

                    for (int r = 0; r < p; r++)
                    {
                        JacobiRotatePair(ref d[r * n + p], ref d[r * n + q], s, tau);
                        d[p * n + r] = d[r * n + p];
                        d[q * n + r] = d[r * n + q];
                    }

                    for (int r = p + 1; r < q; r++)
                    {
                        JacobiRotatePair(ref d[p * n + r], ref d[r * n + q], s, tau);
                        d[r * n + p] = d[p * n + r];
                        d[q * n + r] = d[r * n + q];
                    }

                    for (int r = q + 1; r < n; r++)
                    {
                        JacobiRotatePair(ref d[p * n + r], ref d[q * n + r], s, tau);
                        d[r * n + p] = d[p * n + r];
                        d[r * n + q] = d[q * n + r];
                    }

                    for (int r = 0; r < n; r++)
                    {
                        JacobiRotatePair(ref v[r * n + p], ref v[r * n + q], s, tau);
                    }
                }
            }
        }

        if (!converged)
        {
            double offMax = 0.0;
            for (int p = 0; p < n - 1; p++)
            {
                for (int q = p + 1; q < n; q++)
                {
                    double apq = Math.Abs(d[p * n + q]);
                    if (apq > offMax)
                    {
                        offMax = apq;
                    }
                }
            }

            if (offMax > stopThreshold * 100.0)
            {
                return MathErrorCode.ConvergenceFailure;
            }
        }

        Span<double> vals = stackalloc double[n];
        for (int i = 0; i < n; i++)
        {
            double val = d[i * n + i];
            if (!double.IsFinite(val))
            {
                return MathErrorCode.ArithmeticOverflow;
            }

            vals[i] = val == 0.0 ? 0.0 : val;
        }

        // Sort eigenvalues descending and permute eigenvector columns to match.
        for (int i = 0; i < n - 1; i++)
        {
            int best = i;
            for (int j = i + 1; j < n; j++)
            {
                if (vals[j] > vals[best])
                {
                    best = j;
                }
            }

            if (best != i)
            {
                (vals[i], vals[best]) = (vals[best], vals[i]);
                for (int r = 0; r < n; r++)
                {
                    (v[r * n + i], v[r * n + best]) = (v[r * n + best], v[r * n + i]);
                }
            }
        }

        vals.CopyTo(eigenvalues);
        v.CopyTo(eigenvectors);
        return MathErrorCode.None;
    }

    // ---- Singular Value Decomposition (One-Sided Hestenes-Jacobi, Descending) ----

    /// <summary>
    /// Computes the thin Singular Value Decomposition <c>A = U diag(S) V^T</c> for <c>1 &lt;= rows, cols &lt;= 16</c>
    /// using one-sided Hestenes-Jacobi orthogonalization on stack-allocated buffers.
    /// Singular values <paramref name="singularValues"/> (length <c>k = min(rows, cols)</c>) are sorted descending,
    /// <paramref name="u"/> is row-major <c>rows x k</c>, and <paramref name="vt"/> is row-major <c>k x cols</c>.
    /// </summary>
    public static MathErrorCode MatSvd(
        scoped ReadOnlySpan<double> a,
        int rows,
        int cols,
        scoped Span<double> u,
        scoped Span<double> singularValues,
        scoped Span<double> vt,
        ref DslExecutionBudget budget)
    {
        int k = Math.Min(rows, cols);
        if (!IsValidDims(rows, cols) ||
            a.Length < rows * cols ||
            u.Length < rows * k ||
            singularValues.Length < k ||
            vt.Length < k * cols)
        {
            return MathErrorCode.DimensionMismatch;
        }

        MathErrorCode finiteErr = ValidateFinite(a.Slice(0, rows * cols));
        if (finiteErr != MathErrorCode.None)
        {
            return finiteErr;
        }

        bool transposed = rows < cols;
        int m = transposed ? cols : rows;
        int n = transposed ? rows : cols;

        Span<double> w = stackalloc double[m * n];
        if (transposed)
        {
            MathErrorCode trErr = MatTranspose(a, rows, cols, w);
            if (trErr != MathErrorCode.None)
            {
                return trErr;
            }
        }
        else
        {
            a.Slice(0, m * n).CopyTo(w);
        }

        Span<double> v = stackalloc double[n * n];
        SetIdentity(v, n);

        double scaleF = ComputeFrobeniusNorm(w, m * n);
        double tol = 1e-13 * Math.Max(1e-30, scaleF * scaleF);
        int maxSweeps = 40 * n;
        bool converged = n == 1;

        for (int sweep = 0; sweep < maxSweeps && !converged; sweep++)
        {
            if (!budget.TryConsume(Math.Max(1, n)))
            {
                return MathErrorCode.BudgetExceeded;
            }

            double maxOffDiag = 0.0;
            for (int p = 0; p < n - 1; p++)
            {
                for (int q = p + 1; q < n; q++)
                {
                    double alpha = 0.0;
                    double beta = 0.0;
                    double gamma = 0.0;
                    for (int i = 0; i < m; i++)
                    {
                        double wp = w[i * n + p];
                        double wq = w[i * n + q];
                        alpha += wp * wp;
                        beta += wq * wq;
                        gamma += wp * wq;
                    }

                    if (Math.Abs(gamma) > maxOffDiag)
                    {
                        maxOffDiag = Math.Abs(gamma);
                    }

                    if (Math.Abs(gamma) <= tol)
                    {
                        continue;
                    }

                    double zeta = (beta - alpha) / (2.0 * gamma);
                    double t = zeta >= 0.0
                        ? 1.0 / (zeta + Math.Sqrt(1.0 + zeta * zeta))
                        : -1.0 / (-zeta + Math.Sqrt(1.0 + zeta * zeta));
                    double c = 1.0 / Math.Sqrt(1.0 + t * t);
                    double s = t * c;

                    for (int i = 0; i < m; i++)
                    {
                        int off = i * n;
                        double wp = w[off + p];
                        double wq = w[off + q];
                        w[off + p] = c * wp - s * wq;
                        w[off + q] = s * wp + c * wq;
                    }

                    for (int i = 0; i < n; i++)
                    {
                        int off = i * n;
                        double vp = v[off + p];
                        double vq = v[off + q];
                        v[off + p] = c * vp - s * vq;
                        v[off + q] = s * vp + c * vq;
                    }
                }
            }

            if (maxOffDiag <= tol)
            {
                converged = true;
            }
        }

        if (!converged)
        {
            return MathErrorCode.ConvergenceFailure;
        }

        Span<double> sigma = stackalloc double[n];
        double zeroColumnEps = 1e-13 * Math.Max(1.0, scaleF);
        Span<bool> activeCol = stackalloc bool[n];
        for (int j = 0; j < n; j++)
        {
            double colNormSq = 0.0;
            for (int i = 0; i < m; i++)
            {
                double val = w[i * n + j];
                colNormSq += val * val;
            }

            double colNorm = Math.Sqrt(colNormSq);
            if (!double.IsFinite(colNorm))
            {
                return MathErrorCode.ArithmeticOverflow;
            }

            sigma[j] = colNorm;
            if (colNorm > zeroColumnEps)
            {
                activeCol[j] = true;
                double inv = 1.0 / colNorm;
                for (int i = 0; i < m; i++)
                {
                    w[i * n + j] *= inv;
                }
            }
        }

        // Complete zero-singular-value columns of U using modified Gram-Schmidt on standard basis vectors.
        for (int j = 0; j < n; j++)
        {
            if (activeCol[j])
            {
                continue;
            }

            for (int basis = 0; basis < m; basis++)
            {
                for (int i = 0; i < m; i++)
                {
                    w[i * n + j] = i == basis ? 1.0 : 0.0;
                }

                for (int prev = 0; prev < n; prev++)
                {
                    if (prev == j || !activeCol[prev])
                    {
                        continue;
                    }

                    double dot = 0.0;
                    for (int i = 0; i < m; i++)
                    {
                        dot += w[i * n + j] * w[i * n + prev];
                    }

                    for (int i = 0; i < m; i++)
                    {
                        w[i * n + j] -= dot * w[i * n + prev];
                    }
                }

                double normSq = 0.0;
                for (int i = 0; i < m; i++)
                {
                    double val = w[i * n + j];
                    normSq += val * val;
                }

                double norm = Math.Sqrt(normSq);
                if (norm > 0.5)
                {
                    double inv = 1.0 / norm;
                    for (int i = 0; i < m; i++)
                    {
                        w[i * n + j] *= inv;
                    }

                    activeCol[j] = true;
                    break;
                }
            }
        }

        // Sort singular values descending and permute columns of U (w) and V (v).
        for (int i = 0; i < n - 1; i++)
        {
            int best = i;
            for (int j = i + 1; j < n; j++)
            {
                if (sigma[j] > sigma[best])
                {
                    best = j;
                }
            }

            if (best != i)
            {
                (sigma[i], sigma[best]) = (sigma[best], sigma[i]);
                for (int r = 0; r < m; r++)
                {
                    (w[r * n + i], w[r * n + best]) = (w[r * n + best], w[r * n + i]);
                }

                for (int r = 0; r < n; r++)
                {
                    (v[r * n + i], v[r * n + best]) = (v[r * n + best], v[r * n + i]);
                }
            }
        }

        sigma.CopyTo(singularValues);
        if (!transposed)
        {
            w.CopyTo(u);
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    vt[i * n + j] = v[j * n + i];
                }
            }
        }
        else
        {
            // Since A^T = W diag(sigma) V^T, A = V diag(sigma) W^T.
            v.CopyTo(u);
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < m; j++)
                {
                    vt[i * m + j] = w[j * n + i];
                }
            }
        }

        return MathErrorCode.None;
    }

    // ---- General Real Eigenvalues (Householder Hessenberg + Francis Double-Shift QR) ----

    /// <summary>
    /// Computes all complex eigenvalues (<c>lambda_i = real_i + i * imag_i</c>) of a general real square
    /// <c>n x n</c> matrix via Householder upper-Hessenberg reduction and Francis implicit double-shift QR
    /// (max <c>40 * n</c> total QR iterations). Unconverged inputs return <see cref="MathErrorCode.ConvergenceFailure"/>.
    /// </summary>
    public static MathErrorCode MatEigenGeneral(
        scoped ReadOnlySpan<double> a,
        int n,
        scoped Span<double> realEigenvalues,
        scoped Span<double> imagEigenvalues,
        ref DslExecutionBudget budget)
    {
        int nn = n * n;
        if (!IsValidDim(n) || a.Length < nn || realEigenvalues.Length < n || imagEigenvalues.Length < n)
        {
            return MathErrorCode.DimensionMismatch;
        }

        MathErrorCode finiteErr = ValidateFinite(a.Slice(0, nn));
        if (finiteErr != MathErrorCode.None)
        {
            return finiteErr;
        }

        if (!budget.TryConsume(Math.Max(1, n * n)))
        {
            return MathErrorCode.BudgetExceeded;
        }

        if (n == 1)
        {
            realEigenvalues[0] = a[0] == 0.0 ? 0.0 : a[0];
            imagEigenvalues[0] = 0.0;
            return MathErrorCode.None;
        }

        Span<double> h = stackalloc double[nn];
        a.Slice(0, nn).CopyTo(h);

        if (n >= 3)
        {
            ReduceToUpperHessenberg(h, n);
        }

        return FrancisDoubleShiftQr(h, n, realEigenvalues, imagEigenvalues, ref budget);
    }

    private static void ReduceToUpperHessenberg(Span<double> h, int n)
    {
        for (int k = 0; k < n - 2; k++)
        {
            double scale = 0.0;
            for (int i = k + 1; i < n; i++)
            {
                scale += Math.Abs(h[i * n + k]);
            }

            if (scale == 0.0)
            {
                continue;
            }

            double invScale = 1.0 / scale;
            double normSq = 0.0;
            for (int i = k + 1; i < n; i++)
            {
                double scaled = h[i * n + k] * invScale;
                h[i * n + k] = scaled;
                normSq += scaled * scaled;
            }

            double f = h[(k + 1) * n + k];
            double g = f >= 0.0 ? -Math.Sqrt(normSq) : Math.Sqrt(normSq);
            double denom = normSq - f * g;
            h[(k + 1) * n + k] = f - g;

            // Left multiply: H <- (I - u u^T / denom) * H
            for (int j = k + 1; j < n; j++)
            {
                double dot = 0.0;
                for (int i = k + 1; i < n; i++)
                {
                    dot += h[i * n + k] * h[i * n + j];
                }

                double factor = dot / denom;
                for (int i = k + 1; i < n; i++)
                {
                    h[i * n + j] -= factor * h[i * n + k];
                }
            }

            // Right multiply: H <- H * (I - u u^T / denom)
            for (int i = 0; i < n; i++)
            {
                double dot = 0.0;
                for (int j = k + 1; j < n; j++)
                {
                    dot += h[i * n + j] * h[j * n + k];
                }

                double factor = dot / denom;
                for (int j = k + 1; j < n; j++)
                {
                    h[i * n + j] -= factor * h[j * n + k];
                }
            }

            h[(k + 1) * n + k] = scale * g;
            for (int i = k + 2; i < n; i++)
            {
                h[i * n + k] = 0.0;
            }
        }
    }

    private static MathErrorCode FrancisDoubleShiftQr(
        scoped Span<double> h,
        int n,
        scoped Span<double> wr,
        scoped Span<double> wi,
        ref DslExecutionBudget budget)
    {
        double normF = ComputeFrobeniusNorm(h, n * n);
        int maxTotalIterations = 40 * n;
        int totalIterations = 0;
        int localIterations = 0;
        int nn = n - 1;
        double tShift = 0.0;

        while (nn >= 0)
        {
            int l = nn;
            while (l > 0)
            {
                double s = Math.Abs(h[(l - 1) * n + (l - 1)]) + Math.Abs(h[l * n + l]);
                if (s == 0.0)
                {
                    s = normF;
                }

                if (Math.Abs(h[l * n + (l - 1)]) <= DecompositionEps * Math.Max(1e-30, s))
                {
                    h[l * n + (l - 1)] = 0.0;
                    break;
                }

                l--;
            }

            if (l == nn)
            {
                double val = h[nn * n + nn];
                wr[nn] = val == 0.0 ? 0.0 : val;
                wi[nn] = 0.0;
                nn--;
                localIterations = 0;
                continue;
            }

            if (l == nn - 1)
            {
                double p = 0.5 * (h[(nn - 1) * n + (nn - 1)] - h[nn * n + nn]);
                double q = p * p + h[(nn - 1) * n + nn] * h[nn * n + (nn - 1)];
                double avg = h[nn * n + nn] + p;
                if (q >= 0.0)
                {
                    double z = Math.Sqrt(q);
                    z = p >= 0.0 ? p + z : p - z;
                    double r1 = h[nn * n + nn] + z;
                    double r2 = z != 0.0 ? h[nn * n + nn] - (h[(nn - 1) * n + nn] * h[nn * n + (nn - 1)]) / z : r1;
                    wr[nn - 1] = r1 == 0.0 ? 0.0 : r1;
                    wr[nn] = r2 == 0.0 ? 0.0 : r2;
                    wi[nn - 1] = 0.0;
                    wi[nn] = 0.0;
                }
                else
                {
                    double z = Math.Sqrt(-q);
                    wr[nn - 1] = avg == 0.0 ? 0.0 : avg;
                    wr[nn] = wr[nn - 1];
                    wi[nn - 1] = z;
                    wi[nn] = -z;
                }

                nn -= 2;
                localIterations = 0;
                continue;
            }

            if (++totalIterations > maxTotalIterations)
            {
                return MathErrorCode.ConvergenceFailure;
            }

            if (!budget.TryConsume(Math.Max(1, nn - l + 1)))
            {
                return MathErrorCode.BudgetExceeded;
            }

            double x = h[nn * n + nn];
            double y = h[(nn - 1) * n + (nn - 1)];
            double w = h[nn * n + (nn - 1)] * h[(nn - 1) * n + nn];

            localIterations++;
            if (localIterations % 10 == 0)
            {
                tShift += x;
                for (int i = 0; i <= nn; i++)
                {
                    h[i * n + i] -= x;
                }

                double adhoc = Math.Abs(h[nn * n + (nn - 1)]) + Math.Abs(h[(nn - 1) * n + (nn - 2)]);
                x = 0.75 * adhoc;
                y = x;
                w = -0.4375 * adhoc * adhoc;
            }

            int m = nn - 2;
            double pVal = 0.0;
            double qVal = 0.0;
            double rVal = 0.0;
            while (m >= l)
            {
                double z = h[m * n + m];
                double rShift = x - z;
                double sShift = y - z;
                pVal = (rShift * sShift - w) / h[(m + 1) * n + m] + h[m * n + (m + 1)];
                qVal = h[(m + 1) * n + (m + 1)] - z - rShift - sShift;
                rVal = h[(m + 2) * n + (m + 1)];
                double sNorm = Math.Abs(pVal) + Math.Abs(qVal) + Math.Abs(rVal);
                if (sNorm == 0.0)
                {
                    if (m == l)
                    {
                        pVal = 1.0;
                        qVal = 0.0;
                        rVal = 0.0;
                        break;
                    }

                    m--;
                    continue;
                }

                pVal /= sNorm;
                qVal /= sNorm;
                rVal /= sNorm;
                if (m == l)
                {
                    break;
                }

                double uCheck = Math.Abs(h[m * n + (m - 1)]) * (Math.Abs(qVal) + Math.Abs(rVal));
                double vCheck = Math.Abs(pVal) * (Math.Abs(h[(m - 1) * n + (m - 1)]) + Math.Abs(z) + Math.Abs(h[(m + 1) * n + (m + 1)]));
                if (uCheck <= DecompositionEps * vCheck)
                {
                    break;
                }

                m--;
            }

            for (int i = m + 2; i <= nn; i++)
            {
                h[i * n + (i - 2)] = 0.0;
                if (i != m + 2)
                {
                    h[i * n + (i - 3)] = 0.0;
                }
            }

            for (int k = m; k <= nn - 1; k++)
            {
                bool notLast = k != nn - 1;
                if (k != m)
                {
                    pVal = h[k * n + (k - 1)];
                    qVal = h[(k + 1) * n + (k - 1)];
                    rVal = notLast ? h[(k + 2) * n + (k - 1)] : 0.0;
                    double scaleX = Math.Abs(pVal) + Math.Abs(qVal) + Math.Abs(rVal);
                    if (scaleX == 0.0)
                    {
                        continue;
                    }

                    pVal /= scaleX;
                    qVal /= scaleX;
                    rVal /= scaleX;
                    double sSign = Math.Sqrt(pVal * pVal + qVal * qVal + rVal * rVal);
                    if (pVal < 0.0)
                    {
                        sSign = -sSign;
                    }

                    h[k * n + (k - 1)] = -sSign * scaleX;
                    ApplyBulgeStep(h, n, nn, l, k, notLast, pVal, qVal, rVal, sSign);
                }
                else
                {
                    double sSign = Math.Sqrt(pVal * pVal + qVal * qVal + rVal * rVal);
                    if (sSign == 0.0)
                    {
                        continue;
                    }

                    if (pVal < 0.0)
                    {
                        sSign = -sSign;
                    }

                    ApplyBulgeStep(h, n, nn, l, k, notLast, pVal, qVal, rVal, sSign);
                }
            }
        }

        if (tShift != 0.0)
        {
            for (int i = 0; i < n; i++)
            {
                wr[i] += tShift;
            }
        }

        return MathErrorCode.None;
    }

    private static void ApplyBulgeStep(
        Span<double> h,
        int n,
        int nn,
        int l,
        int k,
        bool notLast,
        double pVal,
        double qVal,
        double rVal,
        double sSign)
    {
        double pPlusS = pVal + sSign;
        if (pPlusS == 0.0)
        {
            return;
        }

        double x = qVal / pPlusS;
        double y = rVal / pPlusS;
        double px = pPlusS / sSign;
        double qPx = x * px;
        double rPx = y * px;

        for (int j = k; j <= nn; j++)
        {
            double p = h[k * n + j] + x * h[(k + 1) * n + j];
            if (notLast)
            {
                p += y * h[(k + 2) * n + j];
                h[(k + 2) * n + j] -= p * rPx;
            }

            h[(k + 1) * n + j] -= p * qPx;
            h[k * n + j] -= p * px;
        }

        int rowEnd = Math.Min(nn, k + 3);
        for (int i = l; i <= rowEnd; i++)
        {
            double p = px * h[i * n + k] + qPx * h[i * n + (k + 1)];
            if (notLast)
            {
                p += rPx * h[i * n + (k + 2)];
                h[i * n + (k + 2)] -= p * y;
            }

            h[i * n + (k + 1)] -= p * x;
            h[i * n + k] -= p;
        }
    }

    // ---- Shared Decomposition Helpers ----

    private static double ValidateSymmetric(ReadOnlySpan<double> a, int n)
    {
        double normF = ComputeFrobeniusNorm(a, n * n);
        double symLimit = SymmetryTolerance * normF;
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                double diff = Math.Abs(a[i * n + j] - a[j * n + i]);
                if (diff > symLimit)
                {
                    return diff;
                }
            }
        }

        return 0.0;
    }

    private static void SetIdentity(Span<double> matrix, int n)
    {
        matrix.Slice(0, n * n).Clear();
        for (int i = 0; i < n; i++)
        {
            matrix[i * n + i] = 1.0;
        }
    }

    private static void JacobiRotatePair(ref double g, ref double h, double s, double tau)
    {
        double gv = g;
        double hv = h;
        g = gv - s * (hv + gv * tau);
        h = hv + s * (gv - hv * tau);
    }
}
