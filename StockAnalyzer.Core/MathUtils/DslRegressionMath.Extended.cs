using System;
using System.Buffers;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.MathUtils;

public static partial class DslRegressionMath
{
    private const double ActiveWeightEpsilon = 1e-15;
    private const int MaxLogisticIterations = 100;
    private const double LogisticHessianRidge = 1e-10;
    private const double LogisticWeightFloor = 1e-12;
    private const double LogisticDetFloor = 1e-24;
    private const double LogisticEtaClamp = 35.0;
    private const double LogisticDivergenceLimit = 30.0;
    private const double LogisticMaxStep = 10.0;
    private const double LogisticGradientMinTol = 1e-8;

    private enum TransformedFitKind : byte
    {
        Exponential,
        Logarithmic,
        Power,
    }

    /// <summary>
    /// Fits intercept-included Weighted Least Squares <c>y = beta_0 + sum_{j=1..p} beta_j x_j</c>
    /// using weighted centering, weighted column scaling, and <see cref="SmallMatrixMath.TryHouseholderQr"/>.
    /// Outputs <c>[beta_0, beta_1, ..., beta_p]</c> into <paramref name="betaOut"/> (length <c>p + 1</c>).
    /// </summary>
    public static MathErrorCode WlsFit(
        scoped ReadOnlySpan<double> x,
        int nRows,
        int nCols,
        scoped ReadOnlySpan<double> y,
        scoped ReadOnlySpan<double> weights,
        scoped Span<double> betaOut,
        ref DslExecutionBudget budget)
    {
        if (nRows < 1 || nCols < 1 || nCols > MaxFeatures || nRows > MaxObservations ||
            x.Length != nRows * nCols || y.Length != nRows || weights.Length != nRows || betaOut.Length < nCols + 1)
        {
            return MathErrorCode.DimensionMismatch;
        }

        if (DslLinearAlgebraMath.ValidateFinite(x) != MathErrorCode.None ||
            DslLinearAlgebraMath.ValidateFinite(y) != MathErrorCode.None ||
            DslLinearAlgebraMath.ValidateFinite(weights) != MathErrorCode.None)
        {
            return MathErrorCode.NonFiniteResult;
        }

        MathErrorCode wErr = ValidateAndMeasureWeights(weights, nRows, out double wMax, out double sumU, out int nEff);
        if (wErr != MathErrorCode.None)
        {
            return wErr;
        }

        if (nEff < nCols + 2)
        {
            return MathErrorCode.DomainError;
        }

        int opCost = Math.Max(1, nRows * nCols * nCols / 4);
        if (!budget.TryConsume(opCost))
        {
            return MathErrorCode.BudgetExceeded;
        }

        Span<double> xMean = stackalloc double[nCols];
        Span<double> xScale = stackalloc double[nCols];
        xMean.Clear();
        xScale.Clear();

        double ySum = 0.0;
        for (int i = 0; i < nRows; i++)
        {
            double u = weights[i] / wMax;
            if (u <= ActiveWeightEpsilon)
            {
                continue;
            }

            ySum += u * y[i];
            int rowBase = i * nCols;
            for (int j = 0; j < nCols; j++)
            {
                xMean[j] += u * x[rowBase + j];
            }
        }

        double yMean = ySum / sumU;
        for (int j = 0; j < nCols; j++)
        {
            xMean[j] /= sumU;
        }

        for (int i = 0; i < nRows; i++)
        {
            double u = weights[i] / wMax;
            if (u <= ActiveWeightEpsilon)
            {
                continue;
            }

            int rowBase = i * nCols;
            for (int j = 0; j < nCols; j++)
            {
                double d = x[rowBase + j] - xMean[j];
                xScale[j] += u * d * d;
            }
        }

        for (int j = 0; j < nCols; j++)
        {
            double wDev = Math.Sqrt(xScale[j] / sumU);
            double flatThreshold = Math.Max(
                ComparisonSeriesDefaults.FlatWindowDeviationEpsilon,
                WindowMoments.SeriesRelativeFlatEpsilon * Math.Abs(xMean[j]));
            if (!double.IsFinite(wDev) || wDev < flatThreshold)
            {
                return MathErrorCode.SingularMatrix;
            }

            xScale[j] = wDev;
        }

        int matrixLen = nRows * nCols;
        int needed = matrixLen + nRows;
        double[]? rented = needed <= MathBufferLimits.StackAllocThreshold
            ? null
            : ArrayPool<double>.Shared.Rent(needed);
        Span<double> work = rented is null
            ? stackalloc double[needed]
            : rented.AsSpan(0, needed);

        try
        {
            Span<double> mat = work.Slice(0, matrixLen);
            Span<double> rhs = work.Slice(matrixLen, nRows);

            for (int i = 0; i < nRows; i++)
            {
                double u = weights[i] / wMax;
                int rowBase = i * nCols;
                if (u <= ActiveWeightEpsilon)
                {
                    mat.Slice(rowBase, nCols).Clear();
                    rhs[i] = 0.0;
                    continue;
                }

                double sqrtU = Math.Sqrt(u);
                for (int j = 0; j < nCols; j++)
                {
                    mat[rowBase + j] = sqrtU * ((x[rowBase + j] - xMean[j]) / xScale[j]);
                }

                rhs[i] = sqrtU * (y[i] - yMean);
            }

            if (!SmallMatrixMath.TryHouseholderQr(mat, nRows, nCols, rhs, out _))
            {
                return MathErrorCode.SingularMatrix;
            }

            Span<double> scaledSlopes = stackalloc double[nCols];
            SmallMatrixMath.SolveUpperTriangular(mat, nCols, rhs, scaledSlopes);

            double intercept = yMean;
            for (int j = 0; j < nCols; j++)
            {
                double slope = scaledSlopes[j] / xScale[j];
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

    /// <summary>
    /// Computes weighted or unweighted regression diagnostics <c>[r2, adj_r2, rmse, se_reg, sse]</c>
    /// for an explicit intercept-included parameter vector <paramref name="beta"/> (<c>length == nCols + 1</c>).
    /// Note: <c>r2</c> may be negative for an arbitrary caller-supplied <paramref name="beta"/>, and <c>sse</c> scales
    /// linearly with raw <paramref name="weights"/> while the other four metrics are invariant to positive weight scaling.
    /// </summary>
    public static MathErrorCode WlsStats(
        scoped ReadOnlySpan<double> x,
        int nRows,
        int nCols,
        scoped ReadOnlySpan<double> y,
        scoped ReadOnlySpan<double> beta,
        scoped ReadOnlySpan<double> weights,
        bool hasWeights,
        scoped Span<double> result,
        ref DslExecutionBudget budget)
    {
        if (result.Length < 5 || nRows < 1 || nCols < 1 || nCols > MaxFeatures || nRows > MaxObservations ||
            x.Length != nRows * nCols || y.Length != nRows || beta.Length != nCols + 1 ||
            (hasWeights && weights.Length != nRows))
        {
            return MathErrorCode.DimensionMismatch;
        }

        if (DslLinearAlgebraMath.ValidateFinite(x) != MathErrorCode.None ||
            DslLinearAlgebraMath.ValidateFinite(y) != MathErrorCode.None ||
            DslLinearAlgebraMath.ValidateFinite(beta) != MathErrorCode.None ||
            (hasWeights && DslLinearAlgebraMath.ValidateFinite(weights) != MathErrorCode.None))
        {
            return MathErrorCode.NonFiniteResult;
        }

        double wMax = 1.0;
        double sumU = nRows;
        int nEff = nRows;
        if (hasWeights)
        {
            MathErrorCode wErr = ValidateAndMeasureWeights(weights, nRows, out wMax, out sumU, out nEff);
            if (wErr != MathErrorCode.None)
            {
                return wErr;
            }
        }

        if (nEff < nCols + 2)
        {
            return MathErrorCode.DomainError;
        }

        int opCost = Math.Max(1, (nRows * nCols) / DslExecutionBudget.CheapOperationsPerUnit);
        if (!budget.TryConsume(opCost))
        {
            return MathErrorCode.BudgetExceeded;
        }

        double ySum = 0.0;
        for (int i = 0; i < nRows; i++)
        {
            double u = hasWeights ? weights[i] / wMax : 1.0;
            if (u <= ActiveWeightEpsilon)
            {
                continue;
            }

            ySum += u * y[i];
        }

        double yMean = ySum / sumU;
        double sstNorm = 0.0;
        double sseNorm = 0.0;
        for (int i = 0; i < nRows; i++)
        {
            double u = hasWeights ? weights[i] / wMax : 1.0;
            if (u <= ActiveWeightEpsilon)
            {
                continue;
            }

            double devY = y[i] - yMean;
            sstNorm += u * devY * devY;

            double yHat = beta[0];
            int rowBase = i * nCols;
            for (int j = 0; j < nCols; j++)
            {
                yHat += beta[j + 1] * x[rowBase + j];
            }

            double resid = y[i] - yHat;
            sseNorm += u * resid * resid;
        }

        if (!double.IsFinite(sstNorm) || !double.IsFinite(sseNorm))
        {
            return MathErrorCode.NonFiniteResult;
        }

        double yDev = Math.Sqrt(sstNorm / sumU);
        double flatFloor = Math.Max(
            ComparisonSeriesDefaults.FlatWindowDeviationEpsilon,
            WindowMoments.SeriesRelativeFlatEpsilon * Math.Abs(yMean));
        if (!double.IsFinite(yDev) || yDev < flatFloor)
        {
            return MathErrorCode.DivisionByZero;
        }

        double r2 = 1.0 - (sseNorm / sstNorm);
        double dfRatio = (nEff - 1.0) / (nEff - nCols - 1.0);
        double adjR2 = 1.0 - ((1.0 - r2) * dfRatio);
        double mse = sseNorm / sumU;
        double rmse = Math.Sqrt(mse);
        double seReg = Math.Sqrt(mse * ((double)nEff / (nEff - nCols - 1.0)));
        double sseRaw = sseNorm * wMax;

        if (!double.IsFinite(r2) || !double.IsFinite(adjR2) || !double.IsFinite(rmse) ||
            !double.IsFinite(seReg) || !double.IsFinite(sseRaw))
        {
            return MathErrorCode.ArithmeticOverflow;
        }

        result[0] = r2 == 0.0 ? 0.0 : r2;
        result[1] = adjR2 == 0.0 ? 0.0 : adjR2;
        result[2] = rmse == 0.0 ? 0.0 : rmse;
        result[3] = seReg == 0.0 ? 0.0 : seReg;
        result[4] = sseRaw == 0.0 ? 0.0 : sseRaw;
        return MathErrorCode.None;
    }

    /// <summary>Fits exponential model <c>y = a * exp(b * x)</c> returning <c>[a, b, r2_log]</c>.</summary>
    public static MathErrorCode RegExpFit(
        scoped ReadOnlySpan<double> x,
        scoped ReadOnlySpan<double> y,
        scoped ReadOnlySpan<double> weights,
        bool hasWeights,
        scoped Span<double> result,
        ref DslExecutionBudget budget)
        => FitTransformed1DLinear(x, y, weights, hasWeights, TransformedFitKind.Exponential, result, ref budget);

    /// <summary>Fits logarithmic model <c>y = a + b * ln(x)</c> returning <c>[a, b, r2]</c>.</summary>
    public static MathErrorCode RegLogFit(
        scoped ReadOnlySpan<double> x,
        scoped ReadOnlySpan<double> y,
        scoped ReadOnlySpan<double> weights,
        bool hasWeights,
        scoped Span<double> result,
        ref DslExecutionBudget budget)
        => FitTransformed1DLinear(x, y, weights, hasWeights, TransformedFitKind.Logarithmic, result, ref budget);

    /// <summary>Fits power-law model <c>y = a * x^b</c> returning <c>[a, b, r2_log]</c>.</summary>
    public static MathErrorCode RegPowFit(
        scoped ReadOnlySpan<double> x,
        scoped ReadOnlySpan<double> y,
        scoped ReadOnlySpan<double> weights,
        bool hasWeights,
        scoped Span<double> result,
        ref DslExecutionBudget budget)
        => FitTransformed1DLinear(x, y, weights, hasWeights, TransformedFitKind.Power, result, ref budget);

    private static MathErrorCode FitTransformed1DLinear(
        scoped ReadOnlySpan<double> x,
        scoped ReadOnlySpan<double> y,
        scoped ReadOnlySpan<double> weights,
        bool hasWeights,
        TransformedFitKind kind,
        scoped Span<double> result,
        ref DslExecutionBudget budget)
    {
        if (result.Length < 3 || x.Length != y.Length || (hasWeights && weights.Length != x.Length))
        {
            return MathErrorCode.DimensionMismatch;
        }

        int n = x.Length;
        if (n < 3 || n > MaxObservations)
        {
            return MathErrorCode.DomainError;
        }

        if (DslLinearAlgebraMath.ValidateFinite(x) != MathErrorCode.None ||
            DslLinearAlgebraMath.ValidateFinite(y) != MathErrorCode.None ||
            (hasWeights && DslLinearAlgebraMath.ValidateFinite(weights) != MathErrorCode.None))
        {
            return MathErrorCode.NonFiniteResult;
        }

        double wMax = 1.0;
        double sumU = n;
        if (hasWeights)
        {
            MathErrorCode wErr = ValidateAndMeasureWeights(weights, n, out wMax, out sumU, out int nEff);
            if (wErr != MathErrorCode.None)
            {
                return wErr;
            }

            if (nEff < 3)
            {
                return MathErrorCode.DomainError;
            }
        }

        if (!budget.TryConsume(Math.Max(1, n / DslExecutionBudget.CheapOperationsPerUnit)))
        {
            return MathErrorCode.BudgetExceeded;
        }

        int needed = n * 3;
        double[]? rented = needed <= MathBufferLimits.StackAllocThreshold
            ? null
            : ArrayPool<double>.Shared.Rent(needed);
        Span<double> work = rented is null
            ? stackalloc double[needed]
            : rented.AsSpan(0, needed);
        try
        {
            Span<double> tx = work.Slice(0, n);
            Span<double> ty = work.Slice(n, n);
            Span<double> uBuf = work.Slice(n * 2, n);
            double meanX = 0.0;
            double meanY = 0.0;

            for (int i = 0; i < n; i++)
            {
                double u = hasWeights ? weights[i] / wMax : 1.0;
                uBuf[i] = u;
                if (u <= ActiveWeightEpsilon)
                {
                    tx[i] = 0.0;
                    ty[i] = 0.0;
                    continue;
                }

                double xi = x[i];
                double yi = y[i];
                switch (kind)
                {
                    case TransformedFitKind.Exponential:
                        if (yi <= 0.0)
                        {
                            return MathErrorCode.DomainError;
                        }

                        tx[i] = xi;
                        ty[i] = Math.Log(yi);
                        break;

                    case TransformedFitKind.Logarithmic:
                        if (xi <= 0.0)
                        {
                            return MathErrorCode.DomainError;
                        }

                        tx[i] = Math.Log(xi);
                        ty[i] = yi;
                        break;

                    default:
                        if (xi <= 0.0 || yi <= 0.0)
                        {
                            return MathErrorCode.DomainError;
                        }

                        tx[i] = Math.Log(xi);
                        ty[i] = Math.Log(yi);
                        break;
                }

                meanX += u * tx[i];
                meanY += u * ty[i];
            }

            meanX /= sumU;
            meanY /= sumU;

            double sxx = 0.0;
            double syy = 0.0;
            double sxy = 0.0;
            for (int i = 0; i < n; i++)
            {
                double u = uBuf[i];
                if (u <= ActiveWeightEpsilon)
                {
                    continue;
                }

                double dx = tx[i] - meanX;
                double dy = ty[i] - meanY;
                sxx += u * dx * dx;
                syy += u * dy * dy;
                sxy += u * dx * dy;
            }

            double xDev = Math.Sqrt(sxx / sumU);
            double xFlat = Math.Max(ComparisonSeriesDefaults.FlatWindowDeviationEpsilon, WindowMoments.SeriesRelativeFlatEpsilon * Math.Abs(meanX));
            if (!double.IsFinite(xDev) || xDev < xFlat)
            {
                return MathErrorCode.SingularMatrix;
            }

            double yDev = Math.Sqrt(syy / sumU);
            double yFlat = Math.Max(ComparisonSeriesDefaults.FlatWindowDeviationEpsilon, WindowMoments.SeriesRelativeFlatEpsilon * Math.Abs(meanY));
            if (!double.IsFinite(yDev) || yDev < yFlat)
            {
                return MathErrorCode.DivisionByZero;
            }

            double b = sxy / sxx;
            double beta0 = meanY - (b * meanX);
            double sse = 0.0;
            for (int i = 0; i < n; i++)
            {
                double u = uBuf[i];
                if (u <= ActiveWeightEpsilon)
                {
                    continue;
                }

                double resid = ty[i] - (beta0 + (b * tx[i]));
                sse += u * resid * resid;
            }

            double rawR2 = 1.0 - (sse / syy);
            double r2 = rawR2 >= -ActiveWeightEpsilon && rawR2 < 0.0 ? 0.0 : rawR2;
            double a = kind is TransformedFitKind.Exponential or TransformedFitKind.Power
                ? Math.Exp(beta0)
                : beta0;

            if (!double.IsFinite(a) || !double.IsFinite(b) || !double.IsFinite(r2))
            {
                return MathErrorCode.ArithmeticOverflow;
            }

            result[0] = a == 0.0 ? 0.0 : a;
            result[1] = b == 0.0 ? 0.0 : b;
            result[2] = r2 == 0.0 ? 0.0 : r2;
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

    /// <summary>
    /// Fits univariate binary/probability logistic regression <c>p(x) = 1 / (1 + exp(-(b0 + b1*x)))</c>
    /// via standardized damped IRLS (Newton-Raphson) returning <c>[b0, b1, log_likelihood]</c>.
    /// </summary>
    public static MathErrorCode RegLogisticFit(
        scoped ReadOnlySpan<double> x,
        scoped ReadOnlySpan<double> y,
        double tol,
        scoped Span<double> result,
        ref DslExecutionBudget budget)
    {
        if (result.Length < 3 || x.Length != y.Length)
        {
            return MathErrorCode.DimensionMismatch;
        }

        int n = x.Length;
        if (n < 3 || n > MaxObservations || !double.IsFinite(tol) || tol < DslTolerance.MinTol || tol > DslTolerance.MaxTol)
        {
            return MathErrorCode.DomainError;
        }

        for (int i = 0; i < n; i++)
        {
            if (!double.IsFinite(x[i]) || !double.IsFinite(y[i]))
            {
                return MathErrorCode.NonFiniteResult;
            }

            if (y[i] < 0.0 || y[i] > 1.0)
            {
                return MathErrorCode.DomainError;
            }
        }

        if (!WindowMoments.TryCompute(x, FlatWindowRule.Series, out WindowMoments xMoments) ||
            !WindowMoments.TryCompute(y, FlatWindowRule.Series, out WindowMoments yMoments))
        {
            return MathErrorCode.NonFiniteResult;
        }

        if (xMoments.IsFlat || yMoments.IsFlat)
        {
            return MathErrorCode.DomainError;
        }

        double xMean = xMoments.Mean;
        double sx = Math.Sqrt(xMoments.M2 / n);
        double[]? rentedZ = n <= MathBufferLimits.StackAllocThreshold
            ? null
            : ArrayPool<double>.Shared.Rent(n);
        Span<double> z = rentedZ is null
            ? stackalloc double[n]
            : rentedZ.AsSpan(0, n);
        try
        {
            for (int i = 0; i < n; i++)
            {
                z[i] = (x[i] - xMean) / sx;
            }

            double yBar = Math.Clamp(yMoments.Mean, 1e-6, 1.0 - 1e-6);
            double gamma0 = Math.Log(yBar / (1.0 - yBar));
            double gamma1 = 0.0;
            int iterCost = Math.Max(1, n / DslExecutionBudget.CheapOperationsPerUnit);
            bool converged = false;

            for (int iter = 0; iter < MaxLogisticIterations; iter++)
            {
                if (!budget.TryConsume(iterCost))
                {
                    return MathErrorCode.BudgetExceeded;
                }

                double grad0 = 0.0;
                double grad1 = 0.0;
                double h00 = LogisticHessianRidge;
                double h01 = 0.0;
                double h11 = LogisticHessianRidge;

                for (int i = 0; i < n; i++)
                {
                    double zi = z[i];
                    double eta = ComputeLogisticEta(gamma0, gamma1, zi);
                    double p = 1.0 / (1.0 + Math.Exp(-eta));
                    double w = Math.Max(p * (1.0 - p), LogisticWeightFloor);
                    double err = y[i] - p;

                    grad0 += err;
                    grad1 += err * zi;
                    h00 += w;
                    h01 += w * zi;
                    h11 += w * zi * zi;
                }

                double det = (h00 * h11) - (h01 * h01);
                if (!double.IsFinite(det) || det <= LogisticDetFloor)
                {
                    return MathErrorCode.SingularMatrix;
                }

                double d0 = ((h11 * grad0) - (h01 * grad1)) / det;
                double d1 = ((h00 * grad1) - (h01 * grad0)) / det;
                if (!double.IsFinite(d0) || !double.IsFinite(d1))
                {
                    return MathErrorCode.NonFiniteResult;
                }

                double stepMax = Math.Max(Math.Abs(d0), Math.Abs(d1));
                if (stepMax > LogisticMaxStep)
                {
                    double scale = LogisticMaxStep / stepMax;
                    d0 *= scale;
                    d1 *= scale;
                }

                gamma0 += d0;
                gamma1 += d1;

                if (Math.Abs(gamma0) > LogisticDivergenceLimit || Math.Abs(gamma1) > LogisticDivergenceLimit)
                {
                    return MathErrorCode.ConvergenceFailure;
                }

                double gradTol = Math.Max(tol, LogisticGradientMinTol) * n;
                if (Math.Max(Math.Abs(d0), Math.Abs(d1)) <= tol &&
                    Math.Max(Math.Abs(grad0), Math.Abs(grad1)) <= gradTol)
                {
                    converged = true;
                    break;
                }
            }

            if (!converged)
            {
                return MathErrorCode.ConvergenceFailure;
            }

            double b1 = gamma1 / sx;
            double b0 = gamma0 - (b1 * xMean);
            double logLik = 0.0;
            for (int i = 0; i < n; i++)
            {
                double eta = ComputeLogisticEta(gamma0, gamma1, z[i]);
                double logOnePlusExpEta = Math.Max(eta, 0.0) + DslElementaryMath.Log1pDouble(Math.Exp(-Math.Abs(eta)));
                logLik += (y[i] * eta) - logOnePlusExpEta;
            }

            if (!double.IsFinite(b0) || !double.IsFinite(b1) || !double.IsFinite(logLik))
            {
                return MathErrorCode.ArithmeticOverflow;
            }

            result[0] = b0 == 0.0 ? 0.0 : b0;
            result[1] = b1 == 0.0 ? 0.0 : b1;
            result[2] = logLik == 0.0 ? 0.0 : logLik;
            return MathErrorCode.None;
        }
        finally
        {
            if (rentedZ is not null)
            {
                ArrayPool<double>.Shared.Return(rentedZ);
            }
        }
    }

    private static MathErrorCode ValidateAndMeasureWeights(
        scoped ReadOnlySpan<double> weights,
        int n,
        out double wMax,
        out double sumU,
        out int nEff)
    {
        wMax = 0.0;
        for (int i = 0; i < n; i++)
        {
            double wi = weights[i];
            if (wi < 0.0)
            {
                sumU = 0.0;
                nEff = 0;
                return MathErrorCode.DomainError;
            }

            if (wi > wMax)
            {
                wMax = wi;
            }
        }

        if (!(wMax > 0.0))
        {
            sumU = 0.0;
            nEff = 0;
            return MathErrorCode.DomainError;
        }

        sumU = 0.0;
        nEff = 0;
        for (int i = 0; i < n; i++)
        {
            double u = weights[i] / wMax;
            if (u > ActiveWeightEpsilon)
            {
                sumU += u;
                nEff++;
            }
        }

        return MathErrorCode.None;
    }

    private static double ComputeLogisticEta(double gamma0, double gamma1, double zi)
        => Math.Clamp(gamma0 + (gamma1 * zi), -LogisticEtaClamp, LogisticEtaClamp);
}
