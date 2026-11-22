using System;
using System.Buffers;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Zero-allocation deterministic financial mathematical kernels for StockAnalyzer DSL v6:
/// savings, compound interest, Excel-parity TVM/loan amortization, equity valuation,
/// and risk-adjusted portfolio performance metrics.
/// </summary>
public static partial class DslFinanceMath
{
    internal const decimal TvmDefaultGuess = 0.1m;
    private const double DegenerateDenominatorThreshold = 1e-15;
    private const double BetaZeroThreshold = 1e-12;
    private const double TvmScanMinRate = -0.9999;
    private const double TvmScanMaxRate = 10.0;
    private const int TvmScanIntervals = 64;
    private const int TvmMaxNewtonIterations = 100;
    private const double TvmFunctionTolerance = 1e-12;
    private const double TvmLooseFunctionTolerance = 1e-8;
    private const double TvmStepTolerance = 1e-10;
    private const double TvmDerivativeFloor = 1e-15;
    private const double TvmSolverTolerance = 1e-10;
    private const double TvmSmallProductThreshold = 1e-3;
    private const double TvmTieBreakEpsilon = 1e-12;
    private const int MaxCompoundingPeriodsPerYear = 1_000_000;
    private const int MaxLoanPeriods = 1_000_000;

    // ---- Savings & Interest ----

    public static MathResult<decimal> SimpleInterest(decimal principal, decimal rate, decimal periods)
    {
        if (principal < 0m || periods < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        try
        {
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(checked(principal * rate * periods)));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<decimal> CompoundAmount(decimal principal, decimal rate, decimal periods, decimal compPerPeriod = 1m)
    {
        if (principal < 0m || periods < 0m || compPerPeriod <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double r = (double)rate;
        double m = (double)compPerPeriod;
        double baseFactor = 1.0 + (r / m);
        if (!double.IsFinite(baseFactor) || baseFactor <= 0.0)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (principal == 0m || periods == 0m || rate == 0m)
        {
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(principal));
        }

        double exponent = m * (double)periods;
        double amount = (double)principal * Math.Exp(exponent * DslElementaryMath.Log1pDouble(r / m));
        return DslElementaryMath.NarrowDouble(amount);
    }

    public static MathResult<decimal> CompoundInterest(decimal principal, decimal rate, decimal periods, decimal compPerPeriod = 1m)
    {
        if (principal < 0m || periods < 0m || compPerPeriod <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double r = (double)rate;
        double m = (double)compPerPeriod;
        double baseFactor = 1.0 + (r / m);
        if (!double.IsFinite(baseFactor) || baseFactor <= 0.0)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (principal == 0m || periods == 0m || rate == 0m)
        {
            return MathResult<decimal>.Ok(0m);
        }

        double exponent = m * (double)periods;
        double interest = (double)principal * DslElementaryMath.Expm1Double(exponent * DslElementaryMath.Log1pDouble(r / m));
        return DslElementaryMath.NarrowDouble(interest);
    }

    public static MathResult<decimal> ContinuousCompound(decimal principal, decimal rate, decimal periods)
    {
        if (principal < 0m || periods < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (principal == 0m || periods == 0m || rate == 0m)
        {
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(principal));
        }

        return DslElementaryMath.NarrowDouble((double)principal * Math.Exp((double)rate * (double)periods));
    }

    public static MathResult<decimal> EffectiveRate(decimal nominalRate, decimal compPerYear)
    {
        if (!DslMathValidation.TryReadInt32InRange(compPerYear, 1, MaxCompoundingPeriodsPerYear, out int m))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double rNom = (double)nominalRate;
        double baseFactor = 1.0 + (rNom / m);
        if (!double.IsFinite(baseFactor) || baseFactor <= 0.0)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (nominalRate == 0m)
        {
            return MathResult<decimal>.Ok(0m);
        }

        return DslElementaryMath.NarrowDouble(DslElementaryMath.Expm1Double(m * DslElementaryMath.Log1pDouble(rNom / m)));
    }

    public static MathResult<decimal> NominalRate(decimal effectiveRate, decimal compPerYear)
    {
        if (!DslMathValidation.TryReadInt32InRange(compPerYear, 1, MaxCompoundingPeriodsPerYear, out int m) ||
            effectiveRate <= -1m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (effectiveRate == 0m)
        {
            return MathResult<decimal>.Ok(0m);
        }

        double rEff = (double)effectiveRate;
        return DslElementaryMath.NarrowDouble(m * DslElementaryMath.Expm1Double(DslElementaryMath.Log1pDouble(rEff) / m));
    }

    // ---- Time Value of Money (Excel Signed Cash-Flow Convention) ----

    public static MathResult<decimal> TvmFv(decimal rate, decimal nper, decimal pmt, decimal pv = 0m, decimal type = 0m)
    {
        if (rate <= -1m || nper < 0m || !DslMathValidation.TryReadInt32InRange(type, 0, 1, out int typeInt))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return DslElementaryMath.NarrowDouble(ComputeTvmFv((double)rate, (double)nper, (double)pmt, (double)pv, typeInt));
    }

    public static MathResult<decimal> TvmPv(decimal rate, decimal nper, decimal pmt, decimal fv = 0m, decimal type = 0m)
    {
        if (rate <= -1m || nper < 0m || !DslMathValidation.TryReadInt32InRange(type, 0, 1, out int typeInt))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double r = (double)rate;
        double n = (double)nper;
        double dPmt = (double)pmt;
        double dFv = (double)fv;
        if (n == 0.0)
        {
            return DslElementaryMath.NarrowDouble(-dFv);
        }

        if (r == 0.0)
        {
            return DslElementaryMath.NarrowDouble(-(dFv + (dPmt * n)));
        }

        if (r > 0.0)
        {
            double negNLog = -n * DslElementaryMath.Log1pDouble(r);
            double gNeg = Math.Exp(negNLog);
            double annPv = (1.0 + (r * typeInt)) * (-DslElementaryMath.Expm1Double(negNLog) / r);
            return DslElementaryMath.NarrowDouble(-(dFv * gNeg) - (dPmt * annPv));
        }

        double nLog = n * DslElementaryMath.Log1pDouble(r);
        double g = Math.Exp(nLog);
        double ann = (1.0 + (r * typeInt)) * (DslElementaryMath.Expm1Double(nLog) / r);
        return DslElementaryMath.NarrowDouble(-(dFv + (dPmt * ann)) / g);
    }

    public static MathResult<decimal> TvmPmt(decimal rate, decimal nper, decimal pv, decimal fv = 0m, decimal type = 0m)
    {
        if (rate <= -1m || nper < 0m || !DslMathValidation.TryReadInt32InRange(type, 0, 1, out int typeInt))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (nper == 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DivisionByZero);
        }

        double pmt = ComputeTvmPmt((double)rate, (double)nper, (double)pv, (double)fv, typeInt, out MathErrorCode err);
        return err == MathErrorCode.None
            ? DslElementaryMath.NarrowDouble(pmt)
            : MathResult<decimal>.Fail(err);
    }

    public static MathResult<decimal> TvmNper(decimal rate, decimal pmt, decimal pv, decimal fv = 0m, decimal type = 0m)
    {
        if (rate <= -1m || !DslMathValidation.TryReadInt32InRange(type, 0, 1, out int typeInt))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double r = (double)rate;
        double dPmt = (double)pmt;
        double dPv = (double)pv;
        double dFv = (double)fv;

        if (r == 0.0)
        {
            if (pmt == 0m)
            {
                return MathResult<decimal>.Fail(MathErrorCode.DivisionByZero);
            }

            return DslElementaryMath.NarrowDouble(-(dPv + dFv) / dPmt);
        }

        double adjPmt = dPmt * (1.0 + (r * typeInt));
        double denom = adjPmt + (r * dPv);
        double denomScale = Math.Max(1.0, Math.Max(Math.Abs(adjPmt), Math.Abs(r * dPv)));
        if (!double.IsFinite(denom) || Math.Abs(denom) <= DegenerateDenominatorThreshold * denomScale)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double delta = (-r * (dPv + dFv)) / denom;
        double q = (adjPmt - (r * dFv)) / denom;
        if (!double.IsFinite(q) || q <= 0.0)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double logQ = double.IsFinite(delta) && Math.Abs(delta) <= 0.5
            ? DslElementaryMath.Log1pDouble(delta)
            : Math.Log(q);
        return DslElementaryMath.NarrowDouble(logQ / DslElementaryMath.Log1pDouble(r));
    }

    public static MathResult<decimal> TvmRate(
        decimal nper,
        decimal pmt,
        decimal pv,
        decimal fv,
        decimal type,
        decimal guess,
        ref DslExecutionBudget budget)
    {
        if (nper <= 0m || guess <= -1m || !DslMathValidation.TryReadInt32InRange(type, 0, 1, out int typeInt))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        bool hasPos = pv > 0m || pmt > 0m || fv > 0m;
        bool hasNeg = pv < 0m || pmt < 0m || fv < 0m;
        if (!hasPos || !hasNeg)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double maxAbs = Math.Max(Math.Abs((double)pv), Math.Max(Math.Abs((double)pmt), Math.Abs((double)fv)));
        double n = (double)nper;
        double normPv = (double)pv / maxAbs;
        double normPmt = (double)pmt / maxAbs;
        double normFv = (double)fv / maxAbs;
        double rate = (double)guess;

        for (int iter = 0; iter < TvmMaxNewtonIterations; iter++)
        {
            if (!budget.TryConsume(1))
            {
                return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
            }

            double f = TvmResidualAndDerivative(rate, n, normPmt, normPv, normFv, typeInt, out double deriv);
            if (!double.IsFinite(f))
            {
                break;
            }

            if (Math.Abs(f) <= TvmFunctionTolerance)
            {
                return DslElementaryMath.NarrowDouble(rate);
            }

            if (!double.IsFinite(deriv) || Math.Abs(deriv) <= TvmDerivativeFloor)
            {
                break;
            }

            double next = rate - (f / deriv);
            if (!double.IsFinite(next) || next <= TvmScanMinRate || next >= TvmScanMaxRate)
            {
                break;
            }

            double fNext = TvmResidualAndDerivative(next, n, normPmt, normPv, normFv, typeInt, out _);
            if (Math.Abs(fNext) <= TvmFunctionTolerance ||
                (Math.Abs(next - rate) <= TvmStepTolerance * Math.Max(1.0, Math.Abs(next)) && Math.Abs(fNext) <= TvmLooseFunctionTolerance))
            {
                return DslElementaryMath.NarrowDouble(next);
            }

            rate = next;
        }

        double logLow = DslElementaryMath.Log1pDouble(TvmScanMinRate);
        double logHigh = DslElementaryMath.Log1pDouble(TvmScanMaxRate);
        double uGuess = DslElementaryMath.Log1pDouble((double)guess);
        double prevU = logLow;
        double prevRate = TvmScanMinRate;
        double prevVal = TvmResidualAndDerivative(prevRate, n, normPmt, normPv, normFv, typeInt, out _);
        bool hasBracket = false;
        double bestLeft = 0.0;
        double bestRight = 0.0;
        double bestDistance = double.PositiveInfinity;

        for (int i = 1; i <= TvmScanIntervals; i++)
        {
            if (!budget.TryConsume(1))
            {
                return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
            }

            double currU = i == TvmScanIntervals
                ? logHigh
                : logLow + ((logHigh - logLow) * i / TvmScanIntervals);
            double currRate = i == TvmScanIntervals
                ? TvmScanMaxRate
                : DslElementaryMath.Expm1Double(currU);
            double currVal = TvmResidualAndDerivative(currRate, n, normPmt, normPv, normFv, typeInt, out _);
            if (double.IsFinite(prevVal) && double.IsFinite(currVal) && Math.Sign(prevVal) * Math.Sign(currVal) <= 0)
            {
                double dist = Math.Abs((0.5 * (prevU + currU)) - uGuess);
                if (!hasBracket || dist < bestDistance - TvmTieBreakEpsilon)
                {
                    hasBracket = true;
                    bestLeft = prevRate;
                    bestRight = currRate;
                    bestDistance = dist;
                }
            }

            prevU = currU;
            prevRate = currRate;
            prevVal = currVal;
        }

        if (hasBracket)
        {
            var objective = new TvmRateObjective(n, normPmt, normPv, normFv, typeInt);
            MathResult<double> brent = DslNumericalAnalysisMath.RootBrent(objective, bestLeft, bestRight, TvmSolverTolerance, ref budget);
            return brent.IsOk
                ? DslElementaryMath.NarrowDouble(brent.Value)
                : MathResult<decimal>.Fail(brent.Error);
        }

        return MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure);
    }

    // ---- Loan Amortization ----

    public static MathResult<decimal> LoanIpmt(decimal rate, decimal per, decimal nper, decimal pv, decimal fv = 0m, decimal type = 0m)
    {
        if (!TryPrepareLoanInputs(rate, per, 1, nper, type, out int k, out int n, out int typeInt))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double r = (double)rate;
        double dPv = (double)pv;
        double dFv = (double)fv;
        double pmt = ComputeTvmPmt(r, n, dPv, dFv, typeInt, out MathErrorCode pmtErr);
        if (pmtErr != MathErrorCode.None)
        {
            return MathResult<decimal>.Fail(pmtErr);
        }

        double ipmt = ComputeLoanIpmtCore(r, k, n, pmt, dPv, dFv, typeInt);
        return DslElementaryMath.NarrowDouble(ipmt);
    }

    public static MathResult<decimal> LoanPpmt(decimal rate, decimal per, decimal nper, decimal pv, decimal fv = 0m, decimal type = 0m)
    {
        if (!TryPrepareLoanInputs(rate, per, 1, nper, type, out int k, out int n, out int typeInt))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double r = (double)rate;
        double dPv = (double)pv;
        double dFv = (double)fv;
        double pmt = ComputeTvmPmt(r, n, dPv, dFv, typeInt, out MathErrorCode pmtErr);
        if (pmtErr != MathErrorCode.None)
        {
            return MathResult<decimal>.Fail(pmtErr);
        }

        double ppmt = ComputeLoanPpmtCore(r, k, n, pmt, dPv, dFv, typeInt);
        return DslElementaryMath.NarrowDouble(ppmt);
    }

    public static MathResult<decimal> LoanBalance(decimal rate, decimal per, decimal nper, decimal pv, decimal fv = 0m, decimal type = 0m)
    {
        if (!TryPrepareLoanInputs(rate, per, 0, nper, type, out int k, out int n, out int typeInt))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (k == 0)
        {
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(pv));
        }

        double r = (double)rate;
        double dPv = (double)pv;
        double dFv = (double)fv;
        double pmt = ComputeTvmPmt(r, n, dPv, dFv, typeInt, out MathErrorCode pmtErr);
        if (pmtErr != MathErrorCode.None)
        {
            return MathResult<decimal>.Fail(pmtErr);
        }

        double balance = ComputeLoanBalanceCore(r, k, n, pmt, dPv, dFv, typeInt);
        return DslElementaryMath.NarrowDouble(balance);
    }

    public static MathErrorCode LoanEqualPrincipalPmt(
        decimal principal,
        decimal rate,
        decimal per,
        decimal nper,
        scoped Span<decimal> result)
    {
        if (result.Length < 4)
        {
            return MathErrorCode.DimensionMismatch;
        }

        if (principal < 0m ||
            rate < 0m ||
            !DslMathValidation.TryReadInt32InRange(nper, 1, MaxLoanPeriods, out int n) ||
            !DslMathValidation.TryReadInt32InRange(per, 1, n, out int k))
        {
            return MathErrorCode.DomainError;
        }

        try
        {
            decimal basePrinPmt = checked(principal / n);
            decimal remBefore = checked(principal - checked(basePrinPmt * (k - 1)));
            decimal prinPmt = k == n ? remBefore : basePrinPmt;
            decimal intPmt = checked(remBefore * rate);
            decimal totalPmt = checked(prinPmt + intPmt);
            decimal remAfter = k == n ? 0m : checked(remBefore - prinPmt);

            result[0] = DslElementaryMath.NormalizeZero(totalPmt);
            result[1] = DslElementaryMath.NormalizeZero(prinPmt);
            result[2] = DslElementaryMath.NormalizeZero(intPmt);
            result[3] = DslElementaryMath.NormalizeZero(remAfter);
            return MathErrorCode.None;
        }
        catch (OverflowException)
        {
            return MathErrorCode.ArithmeticOverflow;
        }
    }

    // ---- Equities & Valuation (Pure System.Decimal + CAGR) ----

    public static MathResult<decimal> Per(decimal price, decimal eps)
    {
        if (price < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return SafeDecimalDivide(price, eps);
    }

    public static MathResult<decimal> Pbr(decimal price, decimal bps)
    {
        if (price < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return SafeDecimalDivide(price, bps);
    }

    public static MathResult<decimal> Roe(decimal netIncome, decimal equity)
        => SafeDecimalDivide(netIncome, equity);

    public static MathResult<decimal> Roa(decimal netIncome, decimal totalAssets)
    {
        if (totalAssets == 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DivisionByZero);
        }

        if (totalAssets < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return SafeDecimalDivide(netIncome, totalAssets);
    }

    public static MathResult<decimal> DivYield(decimal annualDividend, decimal price)
    {
        if (annualDividend < 0m || price < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return SafeDecimalDivide(annualDividend, price);
    }

    public static MathResult<decimal> DdmGordon(decimal d1, decimal r, decimal g)
    {
        if (d1 < 0m || r <= g)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return SafeDecimalDivide(d1, checked(r - g));
    }

    public static MathResult<decimal> CapmExpectedReturn(decimal rf, decimal beta, decimal marketReturn)
    {
        try
        {
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(checked(rf + checked(beta * checked(marketReturn - rf)))));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<decimal> Wacc(decimal eVal, decimal dVal, decimal re, decimal rd, decimal taxRate)
    {
        if (eVal < 0m || dVal < 0m || taxRate < 0m || taxRate > 1m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        try
        {
            decimal totalV = checked(eVal + dVal);
            if (totalV == 0m)
            {
                return MathResult<decimal>.Fail(MathErrorCode.DivisionByZero);
            }

            decimal eqTerm = checked((eVal / totalV) * re);
            decimal debtTerm = checked((dVal / totalV) * checked(rd * (1m - taxRate)));
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(checked(eqTerm + debtTerm)));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<decimal> Cagr(decimal beginVal, decimal endVal, decimal years)
    {
        if (beginVal <= 0m || endVal <= 0m || years <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (beginVal == endVal)
        {
            return MathResult<decimal>.Ok(0m);
        }

        double growth = DslElementaryMath.Expm1Double(Math.Log((double)endVal / (double)beginVal) / (double)years);
        return DslElementaryMath.NarrowDouble(growth);
    }

    // ---- Risk-Adjusted Performance ----

    public static MathResult<double> SharpeRatio(
        scoped ReadOnlySpan<double> returns,
        double rfPerPeriod,
        double periodsPerYear)
    {
        int n = returns.Length;
        if (n < 2 || !double.IsFinite(rfPerPeriod) || !double.IsFinite(periodsPerYear) || periodsPerYear <= 0.0)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        if (!WindowMoments.TryCompute(returns, FlatWindowRule.Series, out WindowMoments mom))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        if (mom.IsFlat)
        {
            return MathResult<double>.Fail(MathErrorCode.DivisionByZero);
        }

        double stdev = Math.Sqrt(mom.M2 / (n - 1));
        if (!double.IsFinite(stdev) || stdev <= DegenerateDenominatorThreshold)
        {
            return MathResult<double>.Fail(MathErrorCode.DivisionByZero);
        }

        double sharpe = ((mom.Mean - rfPerPeriod) / stdev) * Math.Sqrt(periodsPerYear);
        return double.IsFinite(sharpe)
            ? MathResult<double>.Ok(sharpe == 0.0 ? 0.0 : sharpe)
            : MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
    }

    public static MathResult<decimal> SortinoRatio(
        scoped ReadOnlySpan<decimal> returns,
        decimal targetReturn,
        decimal periodsPerYear)
    {
        if (returns.Length < 2 || periodsPerYear <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (!AsymmetricRiskMath.TryComputeDownsideStatistics(returns, targetReturn, out DownsideStatistics ds))
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }

        if (ds.RollingSortino is not { } sortino)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DivisionByZero);
        }

        if (periodsPerYear == 1m)
        {
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(sortino));
        }

        return DslElementaryMath.NarrowDouble((double)sortino * Math.Sqrt((double)periodsPerYear));
    }

    public static MathResult<double> BetaOls(
        scoped ReadOnlySpan<double> assetReturns,
        scoped ReadOnlySpan<double> benchReturns)
    {
        if (assetReturns.Length != benchReturns.Length)
        {
            return MathResult<double>.Fail(MathErrorCode.DimensionMismatch);
        }

        if (assetReturns.Length < 2)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        return TryComputeBeta(assetReturns, benchReturns, out _, out _, out double beta) is var err && err == MathErrorCode.None
            ? MathResult<double>.Ok(beta == 0.0 ? 0.0 : beta)
            : MathResult<double>.Fail(err);
    }

    public static MathResult<double> TreynorRatio(
        scoped ReadOnlySpan<double> assetReturns,
        scoped ReadOnlySpan<double> benchReturns,
        double rfPerPeriod,
        double periodsPerYear)
    {
        if (assetReturns.Length != benchReturns.Length)
        {
            return MathResult<double>.Fail(MathErrorCode.DimensionMismatch);
        }

        if (assetReturns.Length < 2 || !double.IsFinite(rfPerPeriod) || !double.IsFinite(periodsPerYear) || periodsPerYear <= 0.0)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        MathErrorCode err = TryComputeBeta(assetReturns, benchReturns, out double meanAsset, out _, out double beta);
        if (err != MathErrorCode.None)
        {
            return MathResult<double>.Fail(err);
        }

        if (Math.Abs(beta) <= BetaZeroThreshold)
        {
            return MathResult<double>.Fail(MathErrorCode.DivisionByZero);
        }

        double treynor = ((meanAsset - rfPerPeriod) * periodsPerYear) / beta;
        return double.IsFinite(treynor)
            ? MathResult<double>.Ok(treynor == 0.0 ? 0.0 : treynor)
            : MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
    }

    public static MathResult<double> InfoRatio(
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
            return MathResult<double>.Fail(MathErrorCode.DivisionByZero);
        }

        double stdev = Math.Sqrt(mom.M2 / (portReturns.Length - 1));
        double ir = (mom.Mean / stdev) * Math.Sqrt(periodsPerYear);
        return double.IsFinite(ir)
            ? MathResult<double>.Ok(ir == 0.0 ? 0.0 : ir)
            : MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
    }

    public static MathResult<double> AlphaJensen(
        scoped ReadOnlySpan<double> assetReturns,
        scoped ReadOnlySpan<double> benchReturns,
        double rfPerPeriod,
        double periodsPerYear)
    {
        if (assetReturns.Length != benchReturns.Length)
        {
            return MathResult<double>.Fail(MathErrorCode.DimensionMismatch);
        }

        if (assetReturns.Length < 2 || !double.IsFinite(rfPerPeriod) || !double.IsFinite(periodsPerYear) || periodsPerYear <= 0.0)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        MathErrorCode err = TryComputeBeta(assetReturns, benchReturns, out double meanAsset, out double meanBench, out double beta);
        if (err != MathErrorCode.None)
        {
            return MathResult<double>.Fail(err);
        }

        double alpha = ((meanAsset - rfPerPeriod) - (beta * (meanBench - rfPerPeriod))) * periodsPerYear;
        return double.IsFinite(alpha)
            ? MathResult<double>.Ok(alpha == 0.0 ? 0.0 : alpha)
            : MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
    }

    // ---- Internal Helpers ----

    private static MathErrorCode TryComputeDifferenceMoments(
        scoped ReadOnlySpan<double> portReturns,
        scoped ReadOnlySpan<double> benchReturns,
        double periodsPerYear,
        out WindowMoments moments)
    {
        moments = default;
        if (portReturns.Length != benchReturns.Length)
        {
            return MathErrorCode.DimensionMismatch;
        }

        int n = portReturns.Length;
        if (n < 2 || !double.IsFinite(periodsPerYear) || periodsPerYear <= 0.0)
        {
            return MathErrorCode.DomainError;
        }

        double[]? rented = null;
        try
        {
            Span<double> diff = n <= MathBufferLimits.StackAllocThreshold
                ? stackalloc double[n]
                : (rented = ArrayPool<double>.Shared.Rent(n)).AsSpan(0, n);
            for (int i = 0; i < n; i++)
            {
                diff[i] = portReturns[i] - benchReturns[i];
            }

            return WindowMoments.TryCompute(diff, FlatWindowRule.Series, out moments)
                ? MathErrorCode.None
                : MathErrorCode.NonFiniteResult;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<double>.Shared.Return(rented);
            }
        }
    }

    private static MathResult<decimal> SafeDecimalDivide(decimal numerator, decimal denominator)
    {
        if (denominator == 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DivisionByZero);
        }

        try
        {
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(checked(numerator / denominator)));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    private static MathErrorCode TryComputeBeta(
        scoped ReadOnlySpan<double> assetReturns,
        scoped ReadOnlySpan<double> benchReturns,
        out double meanAsset,
        out double meanBench,
        out double beta)
    {
        meanAsset = 0.0;
        meanBench = 0.0;
        beta = 0.0;

        if (!WindowMoments.TryCompute(assetReturns, FlatWindowRule.Series, out WindowMoments aMom) ||
            !WindowMoments.TryCompute(benchReturns, FlatWindowRule.Series, out WindowMoments bMom))
        {
            return MathErrorCode.NonFiniteResult;
        }

        if (bMom.IsFlat || !(bMom.M2 > 0.0))
        {
            return MathErrorCode.DivisionByZero;
        }

        meanAsset = aMom.Mean;
        meanBench = bMom.Mean;
        double cross = 0.0;
        for (int i = 0; i < assetReturns.Length; i++)
        {
            cross += (assetReturns[i] - meanAsset) * (benchReturns[i] - meanBench);
        }

        beta = cross / bMom.M2;
        return double.IsFinite(beta) ? MathErrorCode.None : MathErrorCode.ArithmeticOverflow;
    }

    private static double ComputeTvmFv(double r, double n, double pmt, double pv, int typeInt)
    {
        if (n == 0.0)
        {
            return -pv;
        }

        if (r == 0.0)
        {
            return -(pv + (pmt * n));
        }

        double nLog = n * DslElementaryMath.Log1pDouble(r);
        double g = Math.Exp(nLog);
        double ann = (1.0 + (r * typeInt)) * (DslElementaryMath.Expm1Double(nLog) / r);
        return -(pv * g) - (pmt * ann);
    }

    private static double ComputeTvmPmt(double r, double n, double pv, double fv, int typeInt, out MathErrorCode error)
    {
        error = MathErrorCode.None;
        if (r == 0.0)
        {
            return -(pv + fv) / n;
        }

        if (r > 0.0)
        {
            double negNLog = -n * DslElementaryMath.Log1pDouble(r);
            double gNeg = Math.Exp(negNLog);
            double annPv = (1.0 + (r * typeInt)) * (-DslElementaryMath.Expm1Double(negNLog) / r);
            if (!double.IsFinite(annPv))
            {
                error = MathErrorCode.ArithmeticOverflow;
                return double.NaN;
            }

            if (Math.Abs(annPv) <= DegenerateDenominatorThreshold)
            {
                error = MathErrorCode.DivisionByZero;
                return double.NaN;
            }

            return -(pv + (fv * gNeg)) / annPv;
        }

        double nLog = n * DslElementaryMath.Log1pDouble(r);
        double g = Math.Exp(nLog);
        double ann = (1.0 + (r * typeInt)) * (DslElementaryMath.Expm1Double(nLog) / r);
        if (!double.IsFinite(g) || !double.IsFinite(ann))
        {
            error = MathErrorCode.ArithmeticOverflow;
            return double.NaN;
        }

        if (Math.Abs(ann) <= DegenerateDenominatorThreshold)
        {
            error = MathErrorCode.DivisionByZero;
            return double.NaN;
        }

        return -((pv * g) + fv) / ann;
    }

    private static bool TryPrepareLoanInputs(
        decimal rate,
        decimal per,
        int minPer,
        decimal nper,
        decimal type,
        out int k,
        out int n,
        out int typeInt)
    {
        k = 0;
        typeInt = 0;
        if (rate <= -1m ||
            !DslMathValidation.TryReadInt32InRange(nper, 1, MaxLoanPeriods, out n) ||
            !DslMathValidation.TryReadInt32InRange(per, minPer, n, out k) ||
            !DslMathValidation.TryReadInt32InRange(type, 0, 1, out typeInt))
        {
            n = 0;
            return false;
        }

        return true;
    }

    private static double ComputeLoanBalanceCore(
        double r,
        int per,
        int nper,
        double pmt,
        double pv,
        double fv,
        int typeInt)
    {
        // Use prospective PV discounting for r > 0 so (1+r)^per neither overflows nor cancels at high period counts.
        if (r > 0.0)
        {
            int remaining = nper - per;
            double logOnePlusR = DslElementaryMath.Log1pDouble(r);
            double negMLog = -remaining * logOnePlusR;
            double gFv = typeInt == 1
                ? Math.Exp(-(remaining + 1) * logOnePlusR)
                : Math.Exp(negMLog);
            double annPv = -DslElementaryMath.Expm1Double(negMLog) / r;
            return -(fv * gFv) - (pmt * annPv);
        }

        return typeInt == 1
            ? -(ComputeTvmFv(r, per - 1, pmt, pv, 1) - pmt)
            : -ComputeTvmFv(r, per, pmt, pv, 0);
    }

    private static double ComputeLoanIpmtCore(
        double r,
        int per,
        int nper,
        double pmt,
        double pv,
        double fv,
        int typeInt)
    {
        if (typeInt == 1 && per == 1)
        {
            return 0.0;
        }

        if (r > 0.0)
        {
            if (per == 1)
            {
                return -pv * r;
            }

            int remainingBefore = nper - per + 1;
            double logOnePlusR = DslElementaryMath.Log1pDouble(r);
            double negM1Log = -remainingBefore * logOnePlusR;
            double gFv = typeInt == 1
                ? Math.Exp(-(remainingBefore + 1) * logOnePlusR)
                : Math.Exp(negM1Log);
            return (fv * r * gFv) + (pmt * (-DslElementaryMath.Expm1Double(negM1Log)));
        }

        if (typeInt == 1)
        {
            return (ComputeTvmFv(r, per - 2, pmt, pv, 1) - pmt) * r;
        }

        return ComputeTvmFv(r, per - 1, pmt, pv, 0) * r;
    }

    private static double ComputeLoanPpmtCore(
        double r,
        int per,
        int nper,
        double pmt,
        double pv,
        double fv,
        int typeInt)
    {
        if (typeInt == 1 && per == 1)
        {
            return pmt;
        }

        if (r > 0.0)
        {
            int remainingBefore = nper - per + 1;
            double logOnePlusR = DslElementaryMath.Log1pDouble(r);
            double discountFactor = Math.Exp(-remainingBefore * logOnePlusR);
            double fvInterest = typeInt == 1
                ? fv * r * Math.Exp(-logOnePlusR)
                : fv * r;
            return (pmt - fvInterest) * discountFactor;
        }

        return pmt - ComputeLoanIpmtCore(r, per, nper, pmt, pv, fv, typeInt);
    }

    private static double TvmResidualAndDerivative(
        double rate,
        double n,
        double pmt,
        double pv,
        double fv,
        int typeInt,
        out double derivative)
    {
        if (rate <= -1.0)
        {
            derivative = double.NaN;
            return double.NaN;
        }

        double logOnePlusR = DslElementaryMath.Log1pDouble(rate);
        bool smallProduct = Math.Abs(rate) * Math.Max(1.0, Math.Abs(n)) <= TvmSmallProductThreshold;

        // Hybrid evaluation avoids exponential overflow on both high-n positive rates (PV form)
        // and high-n negative rates (FV form), while joining continuously at rate == 0.
        if (rate >= 0.0)
        {
            double negNLog = -n * logOnePlusR;
            double gNeg = Math.Exp(negNLog);
            double dgNeg = -n * Math.Exp((-n - 1.0) * logOnePlusR);
            double hPv;
            double dhPv;
            if (smallProduct)
            {
                double p1 = n;
                double p2 = -0.5 * n * (n + 1.0);
                double p3 = p2 * ((-n - 2.0) / 3.0);
                double p4 = p3 * ((-n - 3.0) / 4.0);
                double p5 = p4 * ((-n - 4.0) / 5.0);
                hPv = p1 + (rate * (p2 + (rate * (p3 + (rate * (p4 + (rate * p5)))))));
                dhPv = p2 + (rate * ((2.0 * p3) + (rate * ((3.0 * p4) + (rate * (4.0 * p5))))));
            }
            else
            {
                double expm1Neg = DslElementaryMath.Expm1Double(negNLog);
                hPv = -expm1Neg / rate;
                dhPv = ((-rate * dgNeg) + expm1Neg) / (rate * rate);
            }

            double annPv = (1.0 + (rate * typeInt)) * hPv;
            double dAnnPv = (typeInt * hPv) + ((1.0 + (rate * typeInt)) * dhPv);
            derivative = (pmt * dAnnPv) + (fv * dgNeg);
            return pv + (pmt * annPv) + (fv * gNeg);
        }

        double nLog = n * logOnePlusR;
        double g = Math.Exp(nLog);
        double dg = n * Math.Exp((n - 1.0) * logOnePlusR);
        double hFv;
        double dhFv;
        if (smallProduct)
        {
            double c1 = n;
            double c2 = 0.5 * n * (n - 1.0);
            double c3 = c2 * ((n - 2.0) / 3.0);
            double c4 = c3 * ((n - 3.0) / 4.0);
            double c5 = c4 * ((n - 4.0) / 5.0);
            hFv = c1 + (rate * (c2 + (rate * (c3 + (rate * (c4 + (rate * c5)))))));
            dhFv = c2 + (rate * ((2.0 * c3) + (rate * ((3.0 * c4) + (rate * (4.0 * c5))))));
        }
        else
        {
            double expm1Pos = DslElementaryMath.Expm1Double(nLog);
            hFv = expm1Pos / rate;
            dhFv = ((rate * dg) - expm1Pos) / (rate * rate);
        }

        double annFv = (1.0 + (rate * typeInt)) * hFv;
        double dAnnFv = (typeInt * hFv) + ((1.0 + (rate * typeInt)) * dhFv);
        derivative = (pv * dg) + (pmt * dAnnFv);
        return (pv * g) + (pmt * annFv) + fv;
    }

    private readonly struct TvmRateObjective(double n, double pmt, double pv, double fv, int typeInt) : IScalarFunction
    {
        public bool TryEvaluate(double x, ref DslExecutionBudget budget, out double y)
        {
            y = TvmResidualAndDerivative(x, n, pmt, pv, fv, typeInt, out _);
            return double.IsFinite(y);
        }
    }
}
