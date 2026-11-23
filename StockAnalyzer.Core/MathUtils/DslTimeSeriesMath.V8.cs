using System;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.MathUtils;

public static partial class DslTimeSeriesMath
{
    public const int MinJohansenLength = 10;
    public const int MaxArimaOrder = ArimaMath.MaxArOrder;
    public const int MaxArimaDiffOrder = ArimaMath.MaxDiffOrder;
    public const int MaxArimaForecastHorizon = 64;

    /// <summary>
    /// Joseph-stabilized 1D constant-coefficient random-walk Kalman filter (<c>F = H = 1</c>).
    /// When <paramref name="initialIsPosterior"/> is <c>true</c> (default omitted <c>initX</c>/<c>initP</c>),
    /// <paramref name="initX"/> and <paramref name="initP"/> represent the posterior after observing <c>series[0]</c>
    /// and Bayesian updates start at <c>t = 1</c> to prevent double-counting <c>series[0]</c>.
    /// </summary>
    public static MathErrorCode Kalman1DFilter(
        scoped ReadOnlySpan<double> series,
        double q,
        double r,
        double initX,
        double initP,
        scoped Span<double> xOut,
        ref DslExecutionBudget budget,
        bool initialIsPosterior = false)
    {
        int n = series.Length;
        if (n < 1 || n > DslArrayArena.MaxArrayLength || xOut.Length < n)
        {
            return n == 0 ? MathErrorCode.DomainError : MathErrorCode.DimensionMismatch;
        }

        if (!double.IsFinite(q) || !double.IsFinite(r) || !double.IsFinite(initX) || !double.IsFinite(initP))
        {
            return MathErrorCode.NonFiniteResult;
        }

        if (DslLinearAlgebraMath.ValidateFinite(series) != MathErrorCode.None)
        {
            return MathErrorCode.NonFiniteResult;
        }

        if (q < 0.0 || r <= 0.0 || initP <= 0.0)
        {
            return MathErrorCode.DomainError;
        }

        int cost = Math.Max(1, n / DslExecutionBudget.CheapOperationsPerUnit);
        if (!budget.TryConsume(cost))
        {
            return MathErrorCode.BudgetExceeded;
        }

        double x = initX;
        double p = initP;
        int startIdx = 0;
        if (initialIsPosterior)
        {
            xOut[0] = x == 0.0 ? 0.0 : x;
            startIdx = 1;
        }

        for (int t = startIdx; t < n; t++)
        {
            double pPred = p + q;
            double denom = pPred + r;
            if (!(denom > 0.0) || !double.IsFinite(denom))
            {
                return MathErrorCode.DivisionByZero;
            }

            double k = pPred / denom;
            x += k * (series[t] - x);
            double oneMinusK = 1.0 - k;
            p = (oneMinusK * oneMinusK * pPred) + (k * k * r);
            if (!double.IsFinite(x) || !double.IsFinite(p))
            {
                return MathErrorCode.NonFiniteResult;
            }

            xOut[t] = x == 0.0 ? 0.0 : x;
        }

        return MathErrorCode.None;
    }

    /// <summary>
    /// Bivariate Granger causality F-test (<c>H0: causeX does not Granger-cause targetY</c>) over stationary series,
    /// returning <c>[F, pValue, df1, df2]</c> synchronized with <see cref="IndicatorDefaultConstants"/>.
    /// </summary>
    public static MathErrorCode GrangerTest(
        scoped ReadOnlySpan<double> causeX,
        scoped ReadOnlySpan<double> targetY,
        int lagOrder,
        scoped Span<double> resultOut,
        ref DslExecutionBudget budget)
    {
        if (causeX.Length != targetY.Length)
        {
            return MathErrorCode.DimensionMismatch;
        }

        if (resultOut.Length < 4 ||
            lagOrder < IndicatorDefaultConstants.GrangerCausalityMinLagOrder ||
            lagOrder > IndicatorDefaultConstants.GrangerCausalityMaxLagOrder)
        {
            return MathErrorCode.DomainError;
        }

        int n = causeX.Length;
        int df1 = lagOrder;
        int df2 = n - (3 * lagOrder) - 1;
        if (df2 < IndicatorDefaultConstants.GrangerCausalityMinDf2)
        {
            return MathErrorCode.DomainError;
        }

        if (!WindowMoments.TryCompute(causeX, FlatWindowRule.Series, out WindowMoments momX) ||
            !WindowMoments.TryCompute(targetY, FlatWindowRule.Series, out WindowMoments momY))
        {
            return MathErrorCode.NonFiniteResult;
        }

        if (momX.IsFlat || momY.IsFlat)
        {
            return MathErrorCode.DivisionByZero;
        }

        int cost = Math.Max(1, (n * lagOrder) / DslExecutionBudget.CheapOperationsPerUnit);
        if (!budget.TryConsume(cost))
        {
            return MathErrorCode.BudgetExceeded;
        }

        double fStat = GrangerDccMath.ComputeGrangerFStatisticOnSpan(targetY, causeX, lagOrder, df2);
        if (!double.IsFinite(fStat))
        {
            return MathErrorCode.SingularMatrix;
        }

        double xBeta = (double)df2 / (df2 + (df1 * fStat));
        double pVal = DslSpecialFunctionsMath.IncBetaDouble(xBeta, 0.5 * df2, 0.5 * df1);
        if (!double.IsFinite(pVal))
        {
            return MathErrorCode.ConvergenceFailure;
        }

        resultOut[0] = fStat == 0.0 ? 0.0 : fStat;
        resultOut[1] = Math.Clamp(pVal, 0.0, 1.0);
        resultOut[2] = df1;
        resultOut[3] = df2;
        return MathErrorCode.None;
    }

    /// <summary>
    /// Bivariate Johansen cointegration test on raw levels <c>(useLogPrices = false)</c>,
    /// returning <c>[traceStat, maxEigenStat, spreadZScore]</c> via <see cref="JohansenMath.ComputeWindow"/>.
    /// </summary>
    public static MathErrorCode JohansenTest(
        scoped ReadOnlySpan<double> x,
        scoped ReadOnlySpan<double> y,
        scoped Span<double> resultOut,
        ref DslExecutionBudget budget)
    {
        if (x.Length != y.Length)
        {
            return MathErrorCode.DimensionMismatch;
        }

        int n = x.Length;
        if (resultOut.Length < 3 || n < MinJohansenLength || n > DslArrayArena.MaxArrayLength)
        {
            return MathErrorCode.DomainError;
        }

        if (!WindowMoments.TryCompute(x, FlatWindowRule.Series, out WindowMoments mx) ||
            !WindowMoments.TryCompute(y, FlatWindowRule.Series, out WindowMoments my))
        {
            return MathErrorCode.NonFiniteResult;
        }

        if (mx.IsFlat || my.IsFlat)
        {
            return MathErrorCode.DivisionByZero;
        }

        int dLen = n - 1;
        Span<double> diffBuf = stackalloc double[dLen];
        for (int i = 0; i < dLen; i++)
        {
            diffBuf[i] = x[i + 1] - x[i];
        }

        if (!WindowMoments.TryCompute(diffBuf, FlatWindowRule.Series, out WindowMoments mdx))
        {
            return MathErrorCode.NonFiniteResult;
        }

        for (int i = 0; i < dLen; i++)
        {
            diffBuf[i] = y[i + 1] - y[i];
        }

        if (!WindowMoments.TryCompute(diffBuf, FlatWindowRule.Series, out WindowMoments mdy))
        {
            return MathErrorCode.NonFiniteResult;
        }

        if (mdx.IsFlat || mdy.IsFlat)
        {
            return MathErrorCode.DivisionByZero;
        }

        int cost = Math.Max(1, n / DslExecutionBudget.CheapOperationsPerUnit);
        if (!budget.TryConsume(cost))
        {
            return MathErrorCode.BudgetExceeded;
        }

        // Standardize by strictly positive population standard deviations so JohansenMomentRidge (1e-10)
        // never dominates small-scale inputs while leaving JohansenMath.ComputeWindow unmodified.
        double sigmaX = Math.Sqrt(Math.Max(0.0, mx.PopulationVariance));
        double sigmaY = Math.Sqrt(Math.Max(0.0, my.PopulationVariance));
        if (!(sigmaX > 0.0) || !(sigmaY > 0.0) || !double.IsFinite(sigmaX) || !double.IsFinite(sigmaY))
        {
            return MathErrorCode.DivisionByZero;
        }

        double invX = 1.0 / sigmaX;
        double invY = 1.0 / sigmaY;
        Span<double> xScaled = stackalloc double[n];
        Span<double> yScaled = stackalloc double[n];
        for (int i = 0; i < n; i++)
        {
            double sx = x[i] * invX;
            double sy = y[i] * invY;
            if (!double.IsFinite(sx) || !double.IsFinite(sy))
            {
                return MathErrorCode.NonFiniteResult;
            }

            xScaled[i] = sx;
            yScaled[i] = sy;
        }

        JohansenMath.ComputeWindow(xScaled, yScaled, out double traceStat, out double maxEigenStat, out double spreadZScore);
        if (!double.IsFinite(traceStat) || !double.IsFinite(maxEigenStat) || !double.IsFinite(spreadZScore))
        {
            return MathErrorCode.SingularMatrix;
        }

        resultOut[0] = traceStat == 0.0 ? 0.0 : traceStat;
        resultOut[1] = maxEigenStat == 0.0 ? 0.0 : maxEigenStat;
        resultOut[2] = spreadZScore == 0.0 ? 0.0 : spreadZScore;
        return MathErrorCode.None;
    }

    /// <summary>
    /// Fits ARIMA(p, d, q) and outputs <c>[ar_1..ar_p, ma_1..ma_q, sigma2, aic]</c>.
    /// Uses additive MA sign convention, Schur-Cohn root-radius stabilization (reflection bound <c>0.98</c>),
    /// Hannan-Rissanen OLS / Levinson-Durbin, and relative AIC scaled to differenced series length <c>n = w - d</c>.
    /// </summary>
    public static MathErrorCode ArimaFit(
        scoped ReadOnlySpan<double> series,
        int p,
        int d,
        int q,
        scoped Span<double> resultOut,
        ref DslExecutionBudget budget)
    {
        if (!ArimaMath.IsValidOrderSpec(p, d, q) || p + q < 1 || resultOut.Length < p + q + 2)
        {
            return MathErrorCode.DomainError;
        }

        int nDiff = series.Length - d;
        double[]? rentedDiff = nDiff <= MathBufferLimits.StackAllocThreshold
            ? null
            : System.Buffers.ArrayPool<double>.Shared.Rent(Math.Max(1, nDiff));
        Span<double> diff = nDiff <= 0
            ? Span<double>.Empty
            : (rentedDiff is null ? stackalloc double[nDiff] : rentedDiff.AsSpan(0, nDiff));

        try
        {
            MathErrorCode prepErr = TryPrepareDifferencedArimaWindow(series, p, d, q, horizon: 0, diff, ref budget);
            if (prepErr != MathErrorCode.None)
            {
                return prepErr;
            }

            Span<double> phi = p > 0 ? stackalloc double[p] : Span<double>.Empty;
            Span<double> theta = q > 0 ? stackalloc double[q] : Span<double>.Empty;
            MathErrorCode err = ArimaMath.TryFitArimaDifferenced(diff, p, d, q, phi, theta, out double sigma2, out double aic);
            if (err != MathErrorCode.None)
            {
                return err;
            }

            int idx = 0;
            for (int i = 0; i < p; i++)
            {
                resultOut[idx++] = phi[i] == 0.0 ? 0.0 : phi[i];
            }

            for (int j = 0; j < q; j++)
            {
                resultOut[idx++] = theta[j] == 0.0 ? 0.0 : theta[j];
            }

            resultOut[idx++] = sigma2;
            resultOut[idx] = aic == 0.0 ? 0.0 : aic;
            return MathErrorCode.None;
        }
        finally
        {
            if (rentedDiff is not null)
            {
                System.Buffers.ArrayPool<double>.Shared.Return(rentedDiff, clearArray: false);
            }
        }
    }

    /// <summary>
    /// Fits ARIMA(p, d, q) via <see cref="ArimaMath.TryForecastArimaDifferenced"/> (sharing the <c>arima_fit</c> stabilized estimation kernel)
    /// and writes <c>[yhat_1..yhat_h, se_1..se_h]</c> (length <c>2 * horizon</c>, <c>horizon in [1, MaxArimaForecastHorizon]</c>).
    /// </summary>
    public static MathErrorCode ArimaForecast(
        scoped ReadOnlySpan<double> series,
        int p,
        int d,
        int q,
        int horizon,
        scoped Span<double> resultOut,
        ref DslExecutionBudget budget)
    {
        if (!ArimaMath.IsValidOrderSpec(p, d, q) || p + q < 1 ||
            horizon < 1 || horizon > MaxArimaForecastHorizon ||
            resultOut.Length < horizon * 2)
        {
            return MathErrorCode.DomainError;
        }

        int w = series.Length;
        int nDiff = w - d;
        double[]? rentedDiff = nDiff <= MathBufferLimits.StackAllocThreshold
            ? null
            : System.Buffers.ArrayPool<double>.Shared.Rent(Math.Max(1, nDiff));
        Span<double> diff = nDiff <= 0
            ? Span<double>.Empty
            : (rentedDiff is null ? stackalloc double[nDiff] : rentedDiff.AsSpan(0, nDiff));

        try
        {
            MathErrorCode prepErr = TryPrepareDifferencedArimaWindow(series, p, d, q, horizon, diff, ref budget);
            if (prepErr != MathErrorCode.None)
            {
                return prepErr;
            }

            double last1 = series[w - 1];
            double last2 = w >= 2 ? series[w - 2] : series[w - 1];
            Span<double> forecasts = resultOut.Slice(0, horizon);
            Span<double> stdErrors = resultOut.Slice(horizon, horizon);
            MathErrorCode err = ArimaMath.TryForecastArimaDifferenced(diff, last1, last2, p, d, q, horizon, forecasts, stdErrors);
            if (err != MathErrorCode.None)
            {
                return err;
            }

            for (int i = 0; i < horizon * 2; i++)
            {
                if (resultOut[i] == 0.0)
                {
                    resultOut[i] = 0.0;
                }
            }

            return MathErrorCode.None;
        }
        finally
        {
            if (rentedDiff is not null)
            {
                System.Buffers.ArrayPool<double>.Shared.Return(rentedDiff, clearArray: false);
            }
        }
    }

    private static MathErrorCode TryPrepareDifferencedArimaWindow(
        scoped ReadOnlySpan<double> series,
        int p,
        int d,
        int q,
        int horizon,
        scoped Span<double> diffOut,
        ref DslExecutionBudget budget)
    {
        int w = series.Length;
        int minWindow = ArimaMath.ComputeMinimumStabilizedWindow(p, d, q);
        if (w < minWindow || w > DslArrayArena.MaxArrayLength)
        {
            return MathErrorCode.DomainError;
        }

        if (DslLinearAlgebraMath.ValidateFinite(series) != MathErrorCode.None)
        {
            return MathErrorCode.NonFiniteResult;
        }

        ArimaMath.Difference(series, diffOut, d);
        if (!WindowMoments.TryCompute(diffOut, FlatWindowRule.Series, out WindowMoments diffMom))
        {
            return MathErrorCode.NonFiniteResult;
        }

        if (diffMom.IsFlat)
        {
            return MathErrorCode.DivisionByZero;
        }

        int cost = ComputeArimaBudgetUnits(w, p, d, q, horizon);
        return budget.TryConsume(cost) ? MathErrorCode.None : MathErrorCode.BudgetExceeded;
    }

    private static int ComputeArimaBudgetUnits(int w, int p, int d, int q, int horizon)
    {
        long estOps;
        if (q == 0)
        {
            estOps = ((long)w * (p + 2)) + ((long)p * p) + ((long)horizon * (p + d + 1));
        }
        else
        {
            int nEffDiff = w - d;
            int mAux = Math.Min(nEffDiff / 4, Math.Max(p + q + 2, 8));
            int kTot = p + q;
            int tStart = Math.Max(p, mAux + q);
            int nEff = Math.Max(1, nEffDiff - tStart);
            estOps = (2L * mAux * nEffDiff)
                   + ((long)mAux * mAux)
                   + (2L * nEff * kTot * kTot)
                   + (20L * ((p * (p + 1)) + (q * (q + 1))))
                   + ((long)kTot * nEffDiff)
                   + ((long)horizon * (p + d + q + 1));
        }

        return (int)Math.Clamp(estOps / DslExecutionBudget.CheapOperationsPerUnit, 1L, int.MaxValue);
    }
}
