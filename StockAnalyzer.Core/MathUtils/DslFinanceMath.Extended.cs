using System;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.MathUtils;

public static partial class DslFinanceMath
{
    private const int MaxBondTotalPeriods = 1200;
    private const decimal BondPeriodIntegerTolerance = 1e-9m;
    private const double BondPriceZeroThreshold = 1e-15;
    private const double BondDv01Scale = 1e-4;
    private const double BondYtmMaxPerPeriod = 10.0;
    private const double BondYtmMinPerPeriodFloor = 1e-6;
    private const double MaxLogGrowthExponent = 500.0;
    private const int MaxBinomialSteps = 256;
    private const double PortfolioVolZeroThreshold = 1e-12;
    private const double MinVarSumFloor = 1e-12;

    // ---- Fixed Income / Bonds ----

    public static MathResult<decimal> BondPrice(
        decimal face,
        decimal cRate,
        decimal ytm,
        decimal years,
        decimal freq,
        ref DslExecutionBudget budget)
    {
        MathErrorCode err = ValidateBondInputs(face, cRate, years, freq, ytm, out int n, out int m, out double y);
        if (err != MathErrorCode.None)
        {
            return MathResult<decimal>.Fail(err);
        }

        if (!budget.TryConsume(Math.Max(1, n / DslExecutionBudget.CheapOperationsPerUnit)))
        {
            return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
        }

        double price = ComputeBondPriceDouble((double)face, (double)cRate, y, n, m);
        return DslElementaryMath.NarrowDouble(price);
    }

    /// <summary>
    /// Dirty (full) fixed-rate bond price with real-valued <c>0 &lt;= daysSinceCoupon &lt; daysInPeriod</c>
    /// matching <see cref="BondPrice"/> when <c>daysSinceCoupon == 0</c>.
    /// </summary>
    public static MathResult<decimal> BondPriceDirty(
        decimal face,
        decimal cRate,
        decimal ytm,
        decimal daysSinceCoupon,
        decimal daysInPeriod,
        decimal remainingCoupons,
        decimal freq,
        ref DslExecutionBudget budget)
    {
        if (face <= 0m ||
            cRate < 0m ||
            daysInPeriod <= 0m ||
            daysSinceCoupon < 0m ||
            daysSinceCoupon >= daysInPeriod ||
            !DslMathValidation.TryReadInt32InRange(remainingCoupons, 1, MaxBondTotalPeriods, out int n) ||
            !TryReadBondFrequency(freq, out int m))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double y = (double)ytm / m;
        if (!double.IsFinite(y) || y <= -1.0 || y > BondYtmMaxPerPeriod)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double log1pY = DslElementaryMath.Log1pDouble(y);
        if (!double.IsFinite(log1pY) || n * log1pY <= -MaxLogGrowthExponent)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (!budget.TryConsume(Math.Max(1, n / DslExecutionBudget.CheapOperationsPerUnit)))
        {
            return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
        }

        double p0 = ComputeBondPriceDouble((double)face, (double)cRate, y, n, m);
        double alpha = (double)daysSinceCoupon / (double)daysInPeriod;
        double dirty = p0 * Math.Exp(alpha * log1pY);
        return DslElementaryMath.NarrowDouble(dirty);
    }

    public static MathResult<decimal> BondYtm(
        decimal price,
        decimal face,
        decimal cRate,
        decimal years,
        decimal freq,
        decimal tol,
        ref DslExecutionBudget budget)
    {
        if (price <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double tolDouble = (double)tol;
        if (!double.IsFinite(tolDouble) || tolDouble < DslTolerance.MinTol || tolDouble > DslTolerance.MaxTol)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        MathErrorCode err = ValidateBondInputs(face, cRate, years, freq, 0m, out int n, out int m, out _);
        if (err != MathErrorCode.None)
        {
            return MathResult<decimal>.Fail(err);
        }

        if (!budget.TryConsume(Math.Max(1, n / DslExecutionBudget.CheapOperationsPerUnit)))
        {
            return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
        }

        double yMax = BondYtmMaxPerPeriod * m;
        double safeMinPerPeriod = Math.Exp(-MaxLogGrowthExponent / n) - 1.0;
        double yMin = Math.Max(-m + BondYtmMinPerPeriodFloor, m * safeMinPerPeriod);
        if (yMin >= yMax)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        var objective = new BondPriceObjective((double)face, (double)cRate, (double)price, n, m);
        MathResult<double> root = DslNumericalAnalysisMath.RootBrent(objective, yMin, yMax, tolDouble, ref budget);
        if (!root.IsOk)
        {
            return MathResult<decimal>.Fail(root.Error);
        }

        return DslElementaryMath.NarrowDouble(root.Value);
    }

    public static MathResult<decimal> BondCurrentYield(decimal face, decimal cRate, decimal price)
    {
        if (face <= 0m || cRate < 0m || price < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (price == 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DivisionByZero);
        }

        try
        {
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(checked((face * cRate) / price)));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<decimal> BondAccruedInterest(
        decimal face,
        decimal cRate,
        decimal daysAccrued,
        decimal daysInPeriod,
        decimal freq)
    {
        if (face <= 0m ||
            cRate < 0m ||
            daysAccrued < 0m ||
            daysInPeriod <= 0m ||
            daysAccrued > daysInPeriod ||
            !TryReadBondFrequency(freq, out int m))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        try
        {
            decimal accrued = checked((face * cRate * daysAccrued) / (m * daysInPeriod));
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(accrued));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<decimal> BondMacDuration(
        decimal face,
        decimal cRate,
        decimal ytm,
        decimal years,
        decimal freq,
        ref DslExecutionBudget budget)
    {
        MathErrorCode err = TryComputeBondAnalytics(face, cRate, ytm, years, freq, ref budget, out double macDuration, out _, out _, out _);
        return err == MathErrorCode.None
            ? DslElementaryMath.NarrowDouble(macDuration)
            : MathResult<decimal>.Fail(err);
    }

    public static MathResult<decimal> BondModDuration(
        decimal face,
        decimal cRate,
        decimal ytm,
        decimal years,
        decimal freq,
        ref DslExecutionBudget budget)
    {
        MathErrorCode err = TryComputeBondAnalytics(face, cRate, ytm, years, freq, ref budget, out _, out double modDuration, out _, out _);
        return err == MathErrorCode.None
            ? DslElementaryMath.NarrowDouble(modDuration)
            : MathResult<decimal>.Fail(err);
    }

    public static MathResult<decimal> BondConvexity(
        decimal face,
        decimal cRate,
        decimal ytm,
        decimal years,
        decimal freq,
        ref DslExecutionBudget budget)
    {
        MathErrorCode err = TryComputeBondAnalytics(face, cRate, ytm, years, freq, ref budget, out _, out _, out double convexity, out _);
        return err == MathErrorCode.None
            ? DslElementaryMath.NarrowDouble(convexity)
            : MathResult<decimal>.Fail(err);
    }

    public static MathResult<decimal> BondDv01(
        decimal face,
        decimal cRate,
        decimal ytm,
        decimal years,
        decimal freq,
        ref DslExecutionBudget budget)
    {
        MathErrorCode err = TryComputeBondAnalytics(face, cRate, ytm, years, freq, ref budget, out _, out _, out _, out double dv01);
        return err == MathErrorCode.None
            ? DslElementaryMath.NarrowDouble(dv01)
            : MathResult<decimal>.Fail(err);
    }

    // ---- Foreign Exchange / FX ----

    public static MathResult<decimal> FxCrossRate(decimal rateAB, decimal rateBC, decimal invertSecond)
    {
        if (rateAB <= 0m || rateBC <= 0m || !DslMathValidation.TryReadInt32InRange(invertSecond, 0, 1, out int invert))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        try
        {
            decimal res = invert == 0 ? checked(rateAB * rateBC) : checked(rateAB / rateBC);
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(res));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<decimal> FxForwardRate(
        decimal spot,
        decimal rQuote,
        decimal rBase,
        decimal days,
        decimal dayCount)
    {
        if (!TryComputeFxForwardComponents(spot, rQuote, rBase, days, dayCount, out double fwd, out _))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return DslElementaryMath.NarrowDouble(fwd);
    }

    public static MathResult<decimal> FxSwapPoints(
        decimal spot,
        decimal rQuote,
        decimal rBase,
        decimal days,
        decimal dayCount,
        decimal pipSize)
    {
        if (pipSize <= 0m || !TryComputeFxForwardComponents(spot, rQuote, rBase, days, dayCount, out _, out double diff))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return DslElementaryMath.NarrowDouble(diff / (double)pipSize);
    }

    public static MathResult<decimal> FxPipValue(decimal pipSize, decimal lotUnits, decimal quoteToAcctRate)
    {
        if (pipSize <= 0m || lotUnits <= 0m || quoteToAcctRate <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        try
        {
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(checked(pipSize * lotUnits * quoteToAcctRate)));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<decimal> FxRequiredMargin(decimal notionalUnits, decimal rate, decimal leverage)
    {
        if (notionalUnits <= 0m || rate <= 0m || leverage <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        try
        {
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(checked((notionalUnits * rate) / leverage)));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<decimal> FxEffectiveLeverage(decimal positionValue, decimal equity)
    {
        if (positionValue < 0m || equity < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (equity == 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DivisionByZero);
        }

        try
        {
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(checked(positionValue / equity)));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<decimal> FxStopoutRate(
        decimal entryRate,
        decimal equity,
        decimal units,
        decimal isLongDec,
        decimal marginRate,
        decimal stopoutRatio)
    {
        if (entryRate <= 0m ||
            equity <= 0m ||
            units <= 0m ||
            marginRate <= 0m ||
            marginRate > 1m ||
            stopoutRatio < 0m ||
            stopoutRatio > 1m ||
            !DslMathValidation.TryReadInt32InRange(isLongDec, 0, 1, out int isLong))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        try
        {
            decimal factor = checked(stopoutRatio * marginRate);
            decimal num = isLong == 1 ? checked((units * entryRate) - equity) : checked((units * entryRate) + equity);
            decimal den = isLong == 1 ? checked(units * (1m - factor)) : checked(units * (1m + factor));
            if (den == 0m)
            {
                return MathResult<decimal>.Fail(MathErrorCode.DivisionByZero);
            }

            decimal stopout = checked(num / den);
            if (stopout <= 0m)
            {
                return MathResult<decimal>.Fail(MathErrorCode.DomainError);
            }

            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(stopout));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    // ---- Portfolio Analytics ----

    public static MathResult<decimal> PortReturn(
        scoped ReadOnlySpan<decimal> weights,
        scoped ReadOnlySpan<decimal> returns)
    {
        if (weights.Length != returns.Length)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DimensionMismatch);
        }

        if (weights.Length < 1)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        try
        {
            decimal sum = 0m;
            for (int i = 0; i < weights.Length; i++)
            {
                sum = checked(sum + checked(weights[i] * returns[i]));
            }

            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(sum));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<decimal> PortVariance(
        scoped ReadOnlySpan<double> weights,
        scoped ReadOnlySpan<double> covMatrix,
        ref DslExecutionBudget budget)
    {
        MathErrorCode err = TryComputePortfolioVariance(weights, covMatrix, ref budget, out _, out double variance);
        return err == MathErrorCode.None
            ? DslElementaryMath.NarrowDouble(variance)
            : MathResult<decimal>.Fail(err);
    }

    public static MathResult<decimal> PortVol(
        scoped ReadOnlySpan<double> weights,
        scoped ReadOnlySpan<double> covMatrix,
        double periodsPerYear,
        ref DslExecutionBudget budget)
    {
        if (!double.IsFinite(periodsPerYear) || periodsPerYear <= 0.0)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        MathErrorCode err = TryComputePortfolioVariance(weights, covMatrix, ref budget, out _, out double variance);
        return err == MathErrorCode.None
            ? DslElementaryMath.NarrowDouble(Math.Sqrt(variance * periodsPerYear))
            : MathResult<decimal>.Fail(err);
    }

    public static MathErrorCode PortMrc(
        scoped ReadOnlySpan<double> weights,
        scoped ReadOnlySpan<double> covMatrix,
        scoped Span<double> mrcOut,
        ref DslExecutionBudget budget)
    {
        int n = weights.Length;
        if (n == 0)
        {
            return MathErrorCode.DomainError;
        }

        if (n > DslLinearAlgebraMath.MaxMatrixDim || covMatrix.Length != n * n || mrcOut.Length < n)
        {
            return MathErrorCode.DimensionMismatch;
        }

        Span<double> symCov = stackalloc double[n * n];
        MathErrorCode err = TryComputePortfolioVariance(weights, covMatrix, ref budget, symCov, out double variance);
        if (err != MathErrorCode.None)
        {
            return err;
        }

        double sigmaP = Math.Sqrt(variance);
        if (!double.IsFinite(sigmaP) || sigmaP <= PortfolioVolZeroThreshold)
        {
            return MathErrorCode.DivisionByZero;
        }

        for (int i = 0; i < n; i++)
        {
            double marginal = 0.0;
            int rowOffset = i * n;
            for (int j = 0; j < n; j++)
            {
                marginal += symCov[rowOffset + j] * weights[j];
            }

            double val = marginal / sigmaP;
            if (!double.IsFinite(val))
            {
                return MathErrorCode.ArithmeticOverflow;
            }

            mrcOut[i] = val == 0.0 ? 0.0 : val;
        }

        return MathErrorCode.None;
    }

    public static MathErrorCode PortCrc(
        scoped ReadOnlySpan<double> weights,
        scoped ReadOnlySpan<double> covMatrix,
        scoped Span<double> crcOut,
        ref DslExecutionBudget budget)
    {
        MathErrorCode err = PortMrc(weights, covMatrix, crcOut, ref budget);
        if (err != MathErrorCode.None)
        {
            return err;
        }

        for (int i = 0; i < weights.Length; i++)
        {
            double val = weights[i] * crcOut[i];
            if (!double.IsFinite(val))
            {
                return MathErrorCode.ArithmeticOverflow;
            }

            crcOut[i] = val == 0.0 ? 0.0 : val;
        }

        return MathErrorCode.None;
    }

    public static MathResult<double> TrackingError(
        scoped ReadOnlySpan<double> portReturns,
        scoped ReadOnlySpan<double> benchReturns,
        double periodsPerYear)
    {
        MathErrorCode err = TryComputeDifferenceMoments(portReturns, benchReturns, periodsPerYear, out WindowMoments mom);
        if (err != MathErrorCode.None)
        {
            return MathResult<double>.Fail(err);
        }

        if (mom.IsFlat)
        {
            return MathResult<double>.Ok(0.0);
        }

        double te = Math.Sqrt((mom.M2 / (portReturns.Length - 1)) * periodsPerYear);
        return double.IsFinite(te)
            ? MathResult<double>.Ok(te == 0.0 ? 0.0 : te)
            : MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
    }

    public static MathResult<decimal> KellyFraction(decimal winProb, decimal winLossRatio)
    {
        if (winProb < 0m || winProb > 1m || winLossRatio <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        try
        {
            decimal f = checked(winProb - ((1m - winProb) / winLossRatio));
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(f));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathErrorCode PortMinVarWeights(
        scoped ReadOnlySpan<double> covMatrix,
        scoped Span<double> weightsOut,
        out int dimension,
        ref DslExecutionBudget budget)
    {
        dimension = 0;
        if (covMatrix.Length == 0)
        {
            return MathErrorCode.DomainError;
        }

        int n = (int)Math.Round(Math.Sqrt(covMatrix.Length));
        if (n < 1 || n > DslLinearAlgebraMath.MaxMatrixDim || n * n != covMatrix.Length || weightsOut.Length < n)
        {
            return MathErrorCode.DimensionMismatch;
        }

        Span<double> symCov = stackalloc double[n * n];
        MathErrorCode valErr = ValidateAndSymmetrizeCovariance(covMatrix, n, symCov, out double normInf);
        if (valErr != MathErrorCode.None)
        {
            return valErr;
        }

        Span<double> chol = stackalloc double[n * n];
        MathErrorCode cholErr = DslLinearAlgebraMath.MatChol(symCov, n, chol, ref budget);
        if (cholErr == MathErrorCode.DomainError || cholErr == MathErrorCode.SingularMatrix)
        {
            return MathErrorCode.SingularMatrix;
        }

        if (cholErr != MathErrorCode.None)
        {
            return cholErr;
        }

        Span<double> y = stackalloc double[n];
        for (int i = 0; i < n; i++)
        {
            double s = 1.0;
            int rowOffset = i * n;
            for (int j = 0; j < i; j++)
            {
                s -= chol[rowOffset + j] * y[j];
            }

            double yi = s / chol[rowOffset + i];
            if (!double.IsFinite(yi))
            {
                return MathErrorCode.ArithmeticOverflow;
            }

            y[i] = yi;
        }

        Span<double> x = stackalloc double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            double s = y[i];
            for (int j = i + 1; j < n; j++)
            {
                s -= chol[(j * n) + i] * x[j];
            }

            double xi = s / chol[(i * n) + i];
            if (!double.IsFinite(xi))
            {
                return MathErrorCode.ArithmeticOverflow;
            }

            x[i] = xi;
        }

        double sumX = 0.0;
        for (int i = 0; i < n; i++)
        {
            sumX += x[i];
        }

        if (!double.IsFinite(sumX) || normInf <= 0.0 || sumX <= MinVarSumFloor / normInf)
        {
            return MathErrorCode.SingularMatrix;
        }

        for (int i = 0; i < n; i++)
        {
            double w = x[i] / sumX;
            if (!double.IsFinite(w))
            {
                return MathErrorCode.ArithmeticOverflow;
            }

            weightsOut[i] = w == 0.0 ? 0.0 : w;
        }

        dimension = n;
        return MathErrorCode.None;
    }

    // ---- Extended Derivatives ----

    public static MathResult<decimal> BinomialOptionPrice(
        double s,
        double k,
        double t,
        double r,
        double sigma,
        bool isCall,
        bool isAmerican,
        int steps,
        ref DslExecutionBudget budget)
    {
        if (!double.IsFinite(s) ||
            !double.IsFinite(k) ||
            !double.IsFinite(t) ||
            !double.IsFinite(r) ||
            !double.IsFinite(sigma) ||
            s <= 0.0 ||
            k <= 0.0 ||
            t <= 0.0 ||
            sigma <= 0.0 ||
            steps < 1 ||
            steps > MaxBinomialSteps)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        int workOps = (steps * (steps + 1)) / 2;
        if (!budget.TryConsume(Math.Max(1, workOps / DslExecutionBudget.CheapOperationsPerUnit)))
        {
            return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
        }

        double dt = t / steps;
        double sigmaSqrtDt = sigma * Math.Sqrt(dt);
        double u = Math.Exp(sigmaSqrtDt);
        double d = 1.0 / u;
        double df = Math.Exp(-r * dt);
        double growth = Math.Exp(r * dt);
        if (!double.IsFinite(u) || !double.IsFinite(d) || !double.IsFinite(df) || !double.IsFinite(growth) || u <= d)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double p = (growth - d) / (u - d);
        if (!double.IsFinite(p) || p <= 0.0 || p >= 1.0)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double oneMinusP = 1.0 - p;
        Span<double> values = stackalloc double[steps + 1];
        for (int j = 0; j <= steps; j++)
        {
            double spotTerminal = s * Math.Exp(((2 * j) - steps) * sigmaSqrtDt);
            values[j] = isCall ? Math.Max(0.0, spotTerminal - k) : Math.Max(0.0, k - spotTerminal);
        }

        for (int i = steps - 1; i >= 0; i--)
        {
            for (int j = 0; j <= i; j++)
            {
                double hold = df * ((p * values[j + 1]) + (oneMinusP * values[j]));
                if (isAmerican)
                {
                    double spotNode = s * Math.Exp(((2 * j) - i) * sigmaSqrtDt);
                    double intrinsic = isCall ? Math.Max(0.0, spotNode - k) : Math.Max(0.0, k - spotNode);
                    hold = Math.Max(hold, intrinsic);
                }

                values[j] = hold;
            }
        }

        return DslElementaryMath.NarrowDouble(values[0]);
    }

    /// <summary>
    /// CRR binomial option pricing with continuous dividend yield <c>q</c>, unified <c>style in {0..3}</c>
    /// (<c>0=EuroCall, 1=EuroPut, 2=AmerCall, 3=AmerPut</c>), and intrinsic value return at <c>T == 0</c>.
    /// </summary>
    public static MathResult<decimal> BinomialOptionPriceDiv(
        decimal sDec,
        decimal kDec,
        decimal tDec,
        decimal rDec,
        decimal qDec,
        decimal sigmaDec,
        decimal stepsDec,
        decimal styleDec,
        ref DslExecutionBudget budget)
    {
        if (sDec <= 0m ||
            kDec <= 0m ||
            tDec < 0m ||
            sigmaDec <= 0m ||
            !DslMathValidation.TryReadInt32InRange(stepsDec, 1, MaxBinomialSteps, out int steps) ||
            !DslMathValidation.TryReadInt32InRange(styleDec, 0, 3, out int style))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        bool isCall = (style & 1) == 0;
        bool isAmerican = style >= 2;
        if (tDec == 0m)
        {
            decimal intrinsic = isCall ? Math.Max(sDec - kDec, 0m) : Math.Max(kDec - sDec, 0m);
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(intrinsic));
        }

        int workOps = (steps * (steps + 1)) / 2;
        if (!budget.TryConsume(Math.Max(1, workOps / DslExecutionBudget.CheapOperationsPerUnit)))
        {
            return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
        }

        double s = (double)sDec;
        double k = (double)kDec;
        double t = (double)tDec;
        double r = (double)rDec;
        double q = (double)qDec;
        double sigma = (double)sigmaDec;
        if (!double.IsFinite(s) || !double.IsFinite(k) || !double.IsFinite(t) ||
            !double.IsFinite(r) || !double.IsFinite(q) || !double.IsFinite(sigma))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double dt = t / steps;
        double sigmaSqrtDt = sigma * Math.Sqrt(dt);
        double u = Math.Exp(sigmaSqrtDt);
        double d = 1.0 / u;
        double df = Math.Exp(-r * dt);
        double growth = Math.Exp((r - q) * dt);
        if (!double.IsFinite(u) || !double.IsFinite(d) || !double.IsFinite(df) || !double.IsFinite(growth) || u <= d)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double p = (growth - d) / (u - d);
        if (!double.IsFinite(p) || p <= 0.0 || p >= 1.0)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double oneMinusP = 1.0 - p;
        Span<double> values = stackalloc double[steps + 1];
        for (int j = 0; j <= steps; j++)
        {
            double spotTerminal = s * Math.Exp(((2 * j) - steps) * sigmaSqrtDt);
            values[j] = isCall ? Math.Max(0.0, spotTerminal - k) : Math.Max(0.0, k - spotTerminal);
        }

        for (int i = steps - 1; i >= 0; i--)
        {
            for (int j = 0; j <= i; j++)
            {
                double hold = df * ((p * values[j + 1]) + (oneMinusP * values[j]));
                if (isAmerican)
                {
                    double spotNode = s * Math.Exp(((2 * j) - i) * sigmaSqrtDt);
                    double intrinsic = isCall ? Math.Max(0.0, spotNode - k) : Math.Max(0.0, k - spotNode);
                    hold = Math.Max(hold, intrinsic);
                }

                values[j] = hold;
            }
        }

        return DslElementaryMath.NarrowDouble(values[0]);
    }

    public static MathResult<decimal> FuturesFairValue(decimal spot, decimal r, decimal q, decimal t)
    {
        if (spot <= 0m || t < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double f = (double)spot * Math.Exp(((double)r - (double)q) * (double)t);
        return DslElementaryMath.NarrowDouble(f);
    }

    public static MathResult<decimal> PutCallParityPut(decimal callPrice, decimal s, decimal k, decimal t, decimal r, decimal q)
    {
        if (callPrice < 0m || s <= 0m || k <= 0m || t < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double put = (double)callPrice - ((double)s * Math.Exp(-(double)q * (double)t)) + ((double)k * Math.Exp(-(double)r * (double)t));
        return DslElementaryMath.NarrowDouble(put);
    }

    public static MathResult<decimal> PutCallParityCall(decimal putPrice, decimal s, decimal k, decimal t, decimal r, decimal q)
    {
        if (putPrice < 0m || s <= 0m || k <= 0m || t < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double call = (double)putPrice + ((double)s * Math.Exp(-(double)q * (double)t)) - ((double)k * Math.Exp(-(double)r * (double)t));
        return DslElementaryMath.NarrowDouble(call);
    }

    public static MathResult<decimal> IrsFixedRate(
        scoped ReadOnlySpan<decimal> discountFactors,
        scoped ReadOnlySpan<decimal> accrualFractions)
    {
        if (discountFactors.Length != accrualFractions.Length)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DimensionMismatch);
        }

        int n = discountFactors.Length;
        if (n < 1)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        for (int i = 0; i < n; i++)
        {
            if (discountFactors[i] <= 0m || accrualFractions[i] <= 0m)
            {
                return MathResult<decimal>.Fail(MathErrorCode.DomainError);
            }
        }

        try
        {
            decimal num = checked(1m - discountFactors[n - 1]);
            decimal den = 0m;
            for (int i = 0; i < n; i++)
            {
                den = checked(den + checked(accrualFractions[i] * discountFactors[i]));
            }

            if (den == 0m)
            {
                return MathResult<decimal>.Fail(MathErrorCode.DivisionByZero);
            }

            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(checked(num / den)));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    // ---- Shared Helpers ----

    private static bool TryReadBondFrequency(decimal freq, out int m)
    {
        if (!DslMathValidation.TryReadInt32InRange(freq, 1, 12, out m))
        {
            return false;
        }

        return m is 1 or 2 or 4 or 12;
    }

    private static MathErrorCode ValidateBondInputs(
        decimal face,
        decimal cRate,
        decimal years,
        decimal freq,
        decimal ytm,
        out int n,
        out int m,
        out double y)
    {
        n = 0;
        m = 0;
        y = 0.0;

        if (face <= 0m || cRate < 0m || years <= 0m || !TryReadBondFrequency(freq, out m))
        {
            return MathErrorCode.DomainError;
        }

        decimal totalDec;
        try
        {
            totalDec = checked(years * m);
        }
        catch (OverflowException)
        {
            return MathErrorCode.DomainError;
        }

        decimal rounded = decimal.Round(totalDec, 0, MidpointRounding.AwayFromZero);
        if (totalDec < 1m - BondPeriodIntegerTolerance ||
            totalDec > MaxBondTotalPeriods + BondPeriodIntegerTolerance ||
            Math.Abs(totalDec - rounded) > BondPeriodIntegerTolerance)
        {
            return MathErrorCode.DomainError;
        }

        n = (int)rounded;
        if (n < 1 || n > MaxBondTotalPeriods)
        {
            return MathErrorCode.DomainError;
        }

        y = (double)ytm / m;
        if (!double.IsFinite(y) || y <= -1.0)
        {
            return MathErrorCode.DomainError;
        }

        return MathErrorCode.None;
    }

    private static double ComputeBondPriceDouble(double face, double cRate, double y, int n, int m)
    {
        double coupon = face * (cRate / m);
        if (y == 0.0)
        {
            return (coupon * n) + face;
        }

        double negNLog = -n * DslElementaryMath.Log1pDouble(y);
        if (negNLog > 709.0)
        {
            return double.PositiveInfinity;
        }

        double terminalDf = Math.Exp(negNLog);
        double ann = -DslElementaryMath.Expm1Double(negNLog) / y;
        return (coupon * ann) + (face * terminalDf);
    }

    private static MathErrorCode TryComputeBondAnalytics(
        decimal face,
        decimal cRate,
        decimal ytm,
        decimal years,
        decimal freq,
        ref DslExecutionBudget budget,
        out double macDuration,
        out double modDuration,
        out double convexity,
        out double dv01)
    {
        macDuration = 0.0;
        modDuration = 0.0;
        convexity = 0.0;
        dv01 = 0.0;

        MathErrorCode err = ValidateBondInputs(face, cRate, years, freq, ytm, out int n, out int m, out double y);
        if (err != MathErrorCode.None)
        {
            return err;
        }

        if (!budget.TryConsume(Math.Max(1, n / DslExecutionBudget.CheapOperationsPerUnit)))
        {
            return MathErrorCode.BudgetExceeded;
        }

        double faceD = (double)face;
        double coupon = faceD * ((double)cRate / m);
        double inv = 1.0 / (1.0 + y);
        double df = 1.0;
        double price = 0.0;
        double macNum = 0.0;
        double convNum = 0.0;

        for (int k = 1; k <= n; k++)
        {
            df *= inv;
            if (!double.IsFinite(df))
            {
                return MathErrorCode.ArithmeticOverflow;
            }

            double cf = k == n ? coupon + faceD : coupon;
            double pvCf = cf * df;
            price += pvCf;
            macNum += k * pvCf;
            convNum += (double)k * (k + 1) * pvCf;
        }

        if (!double.IsFinite(price) || !double.IsFinite(macNum) || !double.IsFinite(convNum))
        {
            return MathErrorCode.ArithmeticOverflow;
        }

        if (price <= BondPriceZeroThreshold)
        {
            return MathErrorCode.DivisionByZero;
        }

        macDuration = macNum / (price * m);
        modDuration = macDuration / (1.0 + y);
        convexity = convNum / (price * (1.0 + y) * (1.0 + y) * m * m);
        dv01 = price * modDuration * BondDv01Scale;

        if (!double.IsFinite(macDuration) ||
            !double.IsFinite(modDuration) ||
            !double.IsFinite(convexity) ||
            !double.IsFinite(dv01))
        {
            return MathErrorCode.ArithmeticOverflow;
        }

        return MathErrorCode.None;
    }

    private readonly struct BondPriceObjective(double face, double cRate, double targetPrice, int n, int m) : IScalarFunction
    {
        public bool TryEvaluate(double ytm, ref DslExecutionBudget budget, out double value)
        {
            double y = ytm / m;
            if (!double.IsFinite(y) || y <= -1.0)
            {
                value = double.NaN;
                return false;
            }

            value = ComputeBondPriceDouble(face, cRate, y, n, m) - targetPrice;
            return double.IsFinite(value);
        }
    }

    private static bool TryComputeFxForwardComponents(
        decimal spot,
        decimal rQuote,
        decimal rBase,
        decimal days,
        decimal dayCount,
        out double forwardRate,
        out double forwardMinusSpot)
    {
        forwardRate = 0.0;
        forwardMinusSpot = 0.0;

        if (spot <= 0m || days < 0m || !DslMathValidation.TryReadInt32InRange(dayCount, 360, 365, out int dc) || (dc is not (360 or 365)))
        {
            return false;
        }

        double t = (double)days / dc;
        double num = 1.0 + ((double)rQuote * t);
        double den = 1.0 + ((double)rBase * t);
        if (!double.IsFinite(num) || !double.IsFinite(den) || num <= 0.0 || den <= 0.0)
        {
            return false;
        }

        double s = (double)spot;
        forwardRate = s * (num / den);
        forwardMinusSpot = s * (((double)(rQuote - rBase) * t) / den);
        return double.IsFinite(forwardRate) && double.IsFinite(forwardMinusSpot);
    }

    private static MathErrorCode ValidateAndSymmetrizeCovariance(
        scoped ReadOnlySpan<double> covMatrix,
        int n,
        scoped Span<double> symCov,
        out double normInf)
    {
        normInf = 0.0;
        int nn = n * n;
        if (covMatrix.Length != nn || symCov.Length < nn)
        {
            return MathErrorCode.DimensionMismatch;
        }

        for (int i = 0; i < nn; i++)
        {
            double val = covMatrix[i];
            if (!double.IsFinite(val))
            {
                return MathErrorCode.NonFiniteResult;
            }

            double abs = Math.Abs(val);
            if (abs > normInf)
            {
                normInf = abs;
            }
        }

        double symTol = normInf > 0.0
            ? DslLinearAlgebraMath.SymmetryTolerance * normInf
            : DslLinearAlgebraMath.SymmetryTolerance;

        for (int i = 0; i < n; i++)
        {
            double diag = covMatrix[(i * n) + i];
            if (diag < 0.0)
            {
                return MathErrorCode.DomainError;
            }

            symCov[(i * n) + i] = diag;
            for (int j = i + 1; j < n; j++)
            {
                double aij = covMatrix[(i * n) + j];
                double aji = covMatrix[(j * n) + i];
                if (Math.Abs(aij - aji) > symTol)
                {
                    return MathErrorCode.DomainError;
                }

                double avg = 0.5 * (aij + aji);
                symCov[(i * n) + j] = avg;
                symCov[(j * n) + i] = avg;
            }
        }

        return MathErrorCode.None;
    }

    private static MathErrorCode TryComputePortfolioVariance(
        scoped ReadOnlySpan<double> weights,
        scoped ReadOnlySpan<double> covMatrix,
        ref DslExecutionBudget budget,
        out double normInf,
        out double variance)
    {
        int n = weights.Length;
        if (n == 0)
        {
            variance = 0.0;
            normInf = 0.0;
            return MathErrorCode.DomainError;
        }

        if (n > DslLinearAlgebraMath.MaxMatrixDim || covMatrix.Length != n * n)
        {
            variance = 0.0;
            normInf = 0.0;
            return MathErrorCode.DimensionMismatch;
        }

        Span<double> symCov = stackalloc double[n * n];
        return TryComputePortfolioVariance(weights, covMatrix, ref budget, symCov, out variance, out normInf);
    }

    private static MathErrorCode TryComputePortfolioVariance(
        scoped ReadOnlySpan<double> weights,
        scoped ReadOnlySpan<double> covMatrix,
        ref DslExecutionBudget budget,
        scoped Span<double> symCov,
        out double variance)
        => TryComputePortfolioVariance(weights, covMatrix, ref budget, symCov, out variance, out _);

    private static MathErrorCode TryComputePortfolioVariance(
        scoped ReadOnlySpan<double> weights,
        scoped ReadOnlySpan<double> covMatrix,
        ref DslExecutionBudget budget,
        scoped Span<double> symCov,
        out double variance,
        out double normInf)
    {
        variance = 0.0;
        normInf = 0.0;

        int n = weights.Length;
        if (n == 0)
        {
            return MathErrorCode.DomainError;
        }

        if (n > DslLinearAlgebraMath.MaxMatrixDim || covMatrix.Length != n * n)
        {
            return MathErrorCode.DimensionMismatch;
        }

        MathErrorCode symErr = ValidateAndSymmetrizeCovariance(covMatrix, n, symCov, out normInf);
        if (symErr != MathErrorCode.None)
        {
            return symErr;
        }

        if (!budget.TryConsume(Math.Max(1, (n * n) / DslExecutionBudget.CheapOperationsPerUnit)))
        {
            return MathErrorCode.BudgetExceeded;
        }

        double v = 0.0;
        double wNormSq = 0.0;
        for (int i = 0; i < n; i++)
        {
            double wi = weights[i];
            if (!double.IsFinite(wi))
            {
                return MathErrorCode.NonFiniteResult;
            }

            wNormSq += wi * wi;
            double rowDot = 0.0;
            int rowOffset = i * n;
            for (int j = 0; j < n; j++)
            {
                rowDot += symCov[rowOffset + j] * weights[j];
            }

            v += wi * rowDot;
        }

        if (!double.IsFinite(v) || !double.IsFinite(wNormSq))
        {
            return MathErrorCode.ArithmeticOverflow;
        }

        // Scale-invariant global PSD validation matching MatValidateCov:
        // a zero matrix is trivially PSD; non-zero matrices must pass MatEigenSym within SymmetryTolerance * ||Sigma||_inf.
        if (normInf == 0.0)
        {
            variance = 0.0;
            return MathErrorCode.None;
        }

        Span<double> evals = stackalloc double[n];
        Span<double> evecs = stackalloc double[n * n];
        MathErrorCode eigErr = DslLinearAlgebraMath.MatEigenSym(symCov, n, evals, evecs, ref budget);
        if (eigErr != MathErrorCode.None)
        {
            return eigErr;
        }

        double minEval = evals[0];
        for (int i = 1; i < n; i++)
        {
            if (evals[i] < minEval)
            {
                minEval = evals[i];
            }
        }

        if (minEval < -DslLinearAlgebraMath.SymmetryTolerance * normInf)
        {
            return MathErrorCode.DomainError;
        }

        double psdFloor = -DslLinearAlgebraMath.SymmetryTolerance * Math.Max(1, n) * normInf * wNormSq;
        if (v < psdFloor)
        {
            return MathErrorCode.DomainError;
        }

        variance = v < 0.0 ? 0.0 : v;
        return MathErrorCode.None;
    }
}
