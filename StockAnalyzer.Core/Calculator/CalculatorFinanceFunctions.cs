using System;
using System.Buffers;
using StockAnalyzer.Core.MathUtils;

namespace StockAnalyzer.Core.Calculator;

/// <summary>
/// Evaluator dispatch for DSL v6 financial, TVM, loan amortization, equity valuation,
/// and risk-adjusted performance functions.
/// </summary>
internal static partial class CalculatorFinanceFunctions
{
    public static bool TryEvaluate(
        string name,
        int position,
        scoped ReadOnlySpan<decimal> args,
        ref DslExecutionBudget budget,
        out CalculatorOutcome outcome)
    {
        switch (name)
        {
            case "simple_interest":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.SimpleInterest(args[0], args[1], args[2]),
                    position,
                    name);
                return true;

            case "compound_amount":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.CompoundAmount(args[0], args[1], args[2], args.Length >= 4 ? args[3] : 1m),
                    position,
                    name);
                return true;

            case "compound_interest":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.CompoundInterest(args[0], args[1], args[2], args.Length >= 4 ? args[3] : 1m),
                    position,
                    name);
                return true;

            case "continuous_compound":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.ContinuousCompound(args[0], args[1], args[2]),
                    position,
                    name);
                return true;

            case "eff_rate":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.EffectiveRate(args[0], args[1]),
                    position,
                    name);
                return true;

            case "nom_rate":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.NominalRate(args[0], args[1]),
                    position,
                    name);
                return true;

            case "tvm_fv":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.TvmFv(
                        args[0],
                        args[1],
                        args[2],
                        args.Length >= 4 ? args[3] : 0m,
                        args.Length >= 5 ? args[4] : 0m),
                    position,
                    name);
                return true;

            case "tvm_pv":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.TvmPv(
                        args[0],
                        args[1],
                        args[2],
                        args.Length >= 4 ? args[3] : 0m,
                        args.Length >= 5 ? args[4] : 0m),
                    position,
                    name);
                return true;

            case "tvm_pmt":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.TvmPmt(
                        args[0],
                        args[1],
                        args[2],
                        args.Length >= 4 ? args[3] : 0m,
                        args.Length >= 5 ? args[4] : 0m),
                    position,
                    name);
                return true;

            case "tvm_nper":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.TvmNper(
                        args[0],
                        args[1],
                        args[2],
                        args.Length >= 4 ? args[3] : 0m,
                        args.Length >= 5 ? args[4] : 0m),
                    position,
                    name);
                return true;

            case "tvm_rate":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.TvmRate(
                        args[0],
                        args[1],
                        args[2],
                        args.Length >= 4 ? args[3] : 0m,
                        args.Length >= 5 ? args[4] : 0m,
                        args.Length >= 6 ? args[5] : DslFinanceMath.TvmDefaultGuess,
                        ref budget),
                    position,
                    name);
                return true;

            case "loan_ipmt":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.LoanIpmt(
                        args[0],
                        args[1],
                        args[2],
                        args[3],
                        args.Length >= 5 ? args[4] : 0m,
                        args.Length >= 6 ? args[5] : 0m),
                    position,
                    name);
                return true;

            case "loan_ppmt":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.LoanPpmt(
                        args[0],
                        args[1],
                        args[2],
                        args[3],
                        args.Length >= 5 ? args[4] : 0m,
                        args.Length >= 6 ? args[5] : 0m),
                    position,
                    name);
                return true;

            case "loan_balance":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.LoanBalance(
                        args[0],
                        args[1],
                        args[2],
                        args[3],
                        args.Length >= 5 ? args[4] : 0m,
                        args.Length >= 6 ? args[5] : 0m),
                    position,
                    name);
                return true;

            case "per":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.Per(args[0], args[1]),
                    position,
                    name);
                return true;

            case "pbr":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.Pbr(args[0], args[1]),
                    position,
                    name);
                return true;

            case "roe":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.Roe(args[0], args[1]),
                    position,
                    name);
                return true;

            case "roa":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.Roa(args[0], args[1]),
                    position,
                    name);
                return true;

            case "div_yield":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.DivYield(args[0], args[1]),
                    position,
                    name);
                return true;

            case "ddm_gordon":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.DdmGordon(args[0], args[1], args[2]),
                    position,
                    name);
                return true;

            case "capm_er":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.CapmExpectedReturn(args[0], args[1], args[2]),
                    position,
                    name);
                return true;

            case "wacc":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.Wacc(args[0], args[1], args[2], args[3], args[4]),
                    position,
                    name);
                return true;

            case "cagr":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.Cagr(args[0], args[1], args[2]),
                    position,
                    name);
                return true;

            default:
                return TryEvaluateExtended(name, position, args, ref budget, out outcome);
        }
    }

    public static bool TryEvaluateValue(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena,
        out DslEvalOutcome outcome)
    {
        switch (name)
        {
            case "loan_equal_principal_pmt":
                outcome = EvaluateLoanEqualPrincipalPmt(name, position, args, ref arena);
                return true;

            case "sharpe_ratio":
                outcome = EvaluateSharpeRatio(name, position, args, in arena);
                return true;

            case "sortino_ratio":
                outcome = EvaluateSortinoRatio(name, position, args, in arena);
                return true;

            case "beta_ols":
                outcome = EvaluateBetaOls(name, position, args, in arena);
                return true;

            case "treynor_ratio":
                outcome = EvaluateTreynorRatio(name, position, args, in arena);
                return true;

            case "info_ratio":
                outcome = EvaluateInfoRatio(name, position, args, in arena);
                return true;

            case "alpha_jensen":
                outcome = EvaluateAlphaJensen(name, position, args, in arena);
                return true;

            default:
                return TryEvaluateValueExtended(name, position, args, ref budget, ref arena, out outcome);
        }
    }

    private static DslEvalOutcome EvaluateLoanEqualPrincipalPmt(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslArrayArena arena)
    {
        if (args[0].IsArray || args[1].IsArray || args[2].IsArray || args[3].IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' expects scalar arguments (principal, rate, per, nper).", name);
        }

        if (!arena.TryAllocate(4, out DslArrayHandle handle, out Span<decimal> dst))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        MathErrorCode err = DslFinanceMath.LoanEqualPrincipalPmt(args[0].Scalar, args[1].Scalar, args[2].Scalar, args[3].Scalar, dst);
        return err == MathErrorCode.None
            ? DslEvalOutcome.OkArray(handle)
            : CalculatorMatrixFunctions.FailMath(err, position, name);
    }

    private static DslEvalOutcome EvaluateSharpeRatio(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        in DslArrayArena arena)
    {
        if (!args[0].IsArray ||
            (args.Length >= 2 && args[1].IsArray) ||
            (args.Length >= 3 && args[2].IsArray))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' expects (returns[, rfPerPeriod, periodsPerYear]).", name);
        }

        ReadOnlySpan<decimal> retDec = arena.GetSlice(args[0].Array);
        if (retDec.Length < 2)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        double rf = args.Length >= 2 ? (double)args[1].Scalar : 0.0;
        double periodsPerYear = args.Length >= 3 ? (double)args[2].Scalar : 1.0;

        Span<double> retBuf = stackalloc double[retDec.Length];
        CalculatorMatrixFunctions.CopyToDouble(retDec, retBuf);
        return CalculatorMatrixFunctions.NarrowScalarResult(
            DslFinanceMath.SharpeRatio(retBuf, rf, periodsPerYear),
            position,
            name);
    }

    private static DslEvalOutcome EvaluateSortinoRatio(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        in DslArrayArena arena)
    {
        if (!args[0].IsArray ||
            (args.Length >= 2 && args[1].IsArray) ||
            (args.Length >= 3 && args[2].IsArray))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' expects (returns[, targetReturn, periodsPerYear]).", name);
        }

        ReadOnlySpan<decimal> retDec = arena.GetSlice(args[0].Array);
        decimal target = args.Length >= 2 ? args[1].Scalar : 0m;
        decimal periodsPerYear = args.Length >= 3 ? args[2].Scalar : 1m;
        return CalculatorArrayFunctions.ToEvalOutcome(
            CalculatorElementaryFunctions.FromMathResult(
                DslFinanceMath.SortinoRatio(retDec, target, periodsPerYear),
                position,
                name));
    }

    private static DslEvalOutcome EvaluateBetaOls(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        in DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' expects (assetReturns, benchReturns) 1D arrays.", name);
        }

        return EvaluateTwoSeriesMetric(
            name,
            position,
            arena.GetSlice(args[0].Array),
            arena.GetSlice(args[1].Array),
            TwoSeriesMetricKind.BetaOls,
            0.0,
            0.0);
    }

    private static DslEvalOutcome EvaluateTreynorRatio(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        in DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray ||
            (args.Length >= 3 && args[2].IsArray) ||
            (args.Length >= 4 && args[3].IsArray))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' expects (assetReturns, benchReturns[, rfPerPeriod, periodsPerYear]).", name);
        }

        double rf = args.Length >= 3 ? (double)args[2].Scalar : 0.0;
        double periodsPerYear = args.Length >= 4 ? (double)args[3].Scalar : 1.0;
        return EvaluateTwoSeriesMetric(
            name,
            position,
            arena.GetSlice(args[0].Array),
            arena.GetSlice(args[1].Array),
            TwoSeriesMetricKind.TreynorRatio,
            rf,
            periodsPerYear);
    }

    private static DslEvalOutcome EvaluateInfoRatio(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        in DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray || (args.Length >= 3 && args[2].IsArray))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' expects (portReturns, benchReturns[, periodsPerYear]).", name);
        }

        double periodsPerYear = args.Length >= 3 ? (double)args[2].Scalar : 1.0;
        return EvaluateTwoSeriesMetric(
            name,
            position,
            arena.GetSlice(args[0].Array),
            arena.GetSlice(args[1].Array),
            TwoSeriesMetricKind.InfoRatio,
            periodsPerYear,
            0.0);
    }

    private static DslEvalOutcome EvaluateAlphaJensen(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        in DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray ||
            (args.Length >= 3 && args[2].IsArray) ||
            (args.Length >= 4 && args[3].IsArray))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' expects (assetReturns, benchReturns[, rfPerPeriod, periodsPerYear]).", name);
        }

        double rf = args.Length >= 3 ? (double)args[2].Scalar : 0.0;
        double periodsPerYear = args.Length >= 4 ? (double)args[3].Scalar : 1.0;
        return EvaluateTwoSeriesMetric(
            name,
            position,
            arena.GetSlice(args[0].Array),
            arena.GetSlice(args[1].Array),
            TwoSeriesMetricKind.AlphaJensen,
            rf,
            periodsPerYear);
    }

    private enum TwoSeriesMetricKind : byte
    {
        BetaOls,
        TreynorRatio,
        InfoRatio,
        AlphaJensen,
        TrackingError,
    }

    private static DslEvalOutcome EvaluateTwoSeriesMetric(
        string name,
        int position,
        scoped ReadOnlySpan<decimal> aDec,
        scoped ReadOnlySpan<decimal> bDec,
        TwoSeriesMetricKind kind,
        double param1,
        double param2)
    {
        if (aDec.Length != bDec.Length)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        int n = aDec.Length;
        if (n < 2)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        int needed = n * 2;
        double[]? rented = needed <= MathBufferLimits.StackAllocThreshold
            ? null
            : ArrayPool<double>.Shared.Rent(needed);
        Span<double> work = rented is null
            ? stackalloc double[needed]
            : rented.AsSpan(0, needed);
        try
        {
            Span<double> aBuf = work.Slice(0, n);
            Span<double> bBuf = work.Slice(n, n);
            CalculatorMatrixFunctions.CopyToDouble(aDec, aBuf);
            CalculatorMatrixFunctions.CopyToDouble(bDec, bBuf);
            MathResult<double> result = kind switch
            {
                TwoSeriesMetricKind.BetaOls => DslFinanceMath.BetaOls(aBuf, bBuf),
                TwoSeriesMetricKind.TreynorRatio => DslFinanceMath.TreynorRatio(aBuf, bBuf, param1, param2),
                TwoSeriesMetricKind.InfoRatio => DslFinanceMath.InfoRatio(aBuf, bBuf, param1),
                TwoSeriesMetricKind.AlphaJensen => DslFinanceMath.AlphaJensen(aBuf, bBuf, param1, param2),
                _ => DslFinanceMath.TrackingError(aBuf, bBuf, param1),
            };
            return CalculatorMatrixFunctions.NarrowScalarResult(result, position, name);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<double>.Shared.Return(rented);
            }
        }
    }
}
