using System;
using StockAnalyzer.Core.MathUtils;

namespace StockAnalyzer.Core.Calculator;

internal static partial class CalculatorFinanceFunctions
{
    private static bool TryEvaluateExtended(
        string name,
        int position,
        scoped ReadOnlySpan<decimal> args,
        ref DslExecutionBudget budget,
        out CalculatorOutcome outcome)
    {
        switch (name)
        {
            // ---- Bonds ----
            case "bond_price":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.BondPrice(args[0], args[1], args[2], args[3], args.Length >= 5 ? args[4] : 2m, ref budget),
                    position,
                    name);
                return true;

            case "bond_ytm":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.BondYtm(
                        args[0],
                        args[1],
                        args[2],
                        args[3],
                        args.Length >= 5 ? args[4] : 2m,
                        args.Length >= 6 ? args[5] : CalculatorHigherOrderFunctions.DefaultToleranceDecimal,
                        ref budget),
                    position,
                    name);
                return true;

            case "bond_current_yield":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.BondCurrentYield(args[0], args[1], args[2]),
                    position,
                    name);
                return true;

            case "bond_accrued_interest":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.BondAccruedInterest(args[0], args[1], args[2], args[3], args.Length >= 5 ? args[4] : 2m),
                    position,
                    name);
                return true;

            case "bond_mac_duration":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.BondMacDuration(args[0], args[1], args[2], args[3], args.Length >= 5 ? args[4] : 2m, ref budget),
                    position,
                    name);
                return true;

            case "bond_mod_duration":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.BondModDuration(args[0], args[1], args[2], args[3], args.Length >= 5 ? args[4] : 2m, ref budget),
                    position,
                    name);
                return true;

            case "bond_convexity":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.BondConvexity(args[0], args[1], args[2], args[3], args.Length >= 5 ? args[4] : 2m, ref budget),
                    position,
                    name);
                return true;

            case "bond_dv01":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.BondDv01(args[0], args[1], args[2], args[3], args.Length >= 5 ? args[4] : 2m, ref budget),
                    position,
                    name);
                return true;

            case "bond_price_dirty":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.BondPriceDirty(
                        args[0],
                        args[1],
                        args[2],
                        args[3],
                        args[4],
                        args[5],
                        args.Length >= 7 ? args[6] : 2m,
                        ref budget),
                    position,
                    name);
                return true;

            // ---- Foreign Exchange / FX ----
            case "fx_cross_rate":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.FxCrossRate(args[0], args[1], args.Length >= 3 ? args[2] : 0m),
                    position,
                    name);
                return true;

            case "fx_forward_rate":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.FxForwardRate(args[0], args[1], args[2], args[3], args.Length >= 5 ? args[4] : 360m),
                    position,
                    name);
                return true;

            case "fx_swap_points":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.FxSwapPoints(
                        args[0],
                        args[1],
                        args[2],
                        args[3],
                        args.Length >= 5 ? args[4] : 360m,
                        args.Length >= 6 ? args[5] : 0.0001m),
                    position,
                    name);
                return true;

            case "fx_pip_value":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.FxPipValue(args[0], args[1], args.Length >= 3 ? args[2] : 1m),
                    position,
                    name);
                return true;

            case "fx_required_margin":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.FxRequiredMargin(args[0], args[1], args[2]),
                    position,
                    name);
                return true;

            case "fx_effective_leverage":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.FxEffectiveLeverage(args[0], args[1]),
                    position,
                    name);
                return true;

            case "fx_stopout_rate":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.FxStopoutRate(args[0], args[1], args[2], args[3], args[4], args[5]),
                    position,
                    name);
                return true;

            // ---- Portfolio Scalar & Extended Derivatives ----
            case "kelly_fraction":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.KellyFraction(args[0], args[1]),
                    position,
                    name);
                return true;

            case "binomial_option_price":
                outcome = EvaluateBinomialOptionPrice(name, position, args, ref budget);
                return true;

            case "binomial_option_price_div":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.BinomialOptionPriceDiv(
                        args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], ref budget),
                    position,
                    name);
                return true;

            case "futures_fair_value":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.FuturesFairValue(args[0], args[1], args[2], args[3]),
                    position,
                    name);
                return true;

            case "put_call_parity_put":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.PutCallParityPut(args[0], args[1], args[2], args[3], args[4], args.Length >= 6 ? args[5] : 0m),
                    position,
                    name);
                return true;

            case "put_call_parity_call":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslFinanceMath.PutCallParityCall(args[0], args[1], args[2], args[3], args[4], args.Length >= 6 ? args[5] : 0m),
                    position,
                    name);
                return true;

            default:
                outcome = default;
                return false;
        }
    }

    private static bool TryEvaluateValueExtended(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena,
        out DslEvalOutcome outcome)
    {
        switch (name)
        {
            case "port_return":
                outcome = EvaluatePortReturn(name, position, args, in arena);
                return true;

            case "port_variance":
                outcome = EvaluatePortfolioVarianceOrVol(returnVol: false, name, position, args, ref budget, in arena);
                return true;

            case "port_vol":
                outcome = EvaluatePortfolioVarianceOrVol(returnVol: true, name, position, args, ref budget, in arena);
                return true;

            case "port_mrc":
                outcome = EvaluatePortfolioRiskContribution(isCrc: false, name, position, args, ref budget, ref arena);
                return true;

            case "port_crc":
                outcome = EvaluatePortfolioRiskContribution(isCrc: true, name, position, args, ref budget, ref arena);
                return true;

            case "tracking_error":
                outcome = EvaluateTrackingError(name, position, args, in arena);
                return true;

            case "port_min_var_weights":
                outcome = EvaluatePortMinVarWeights(name, position, args, ref budget, ref arena);
                return true;

            case "irs_fixed_rate":
                outcome = EvaluateIrsFixedRate(name, position, args, in arena);
                return true;

            default:
                outcome = default;
                return false;
        }
    }

    private static CalculatorOutcome EvaluateBinomialOptionPrice(
        string name,
        int position,
        scoped ReadOnlySpan<decimal> args,
        ref DslExecutionBudget budget)
    {
        if (!DslMathValidation.TryReadInt32InRange(args[5], 0, 1, out int isCallInt) ||
            !DslMathValidation.TryReadInt32InRange(args[6], 0, 1, out int isAmericanInt) ||
            !DslMathValidation.TryReadInt32InRange(args[7], 1, 256, out int steps))
        {
            return CalculatorElementaryFunctions.FromMathResult(
                MathResult<decimal>.Fail(MathErrorCode.DomainError),
                position,
                name);
        }

        MathResult<decimal> result = DslFinanceMath.BinomialOptionPrice(
            (double)args[0],
            (double)args[1],
            (double)args[2],
            (double)args[3],
            (double)args[4],
            isCallInt == 1,
            isAmericanInt == 1,
            steps,
            ref budget);
        return CalculatorElementaryFunctions.FromMathResult(result, position, name);
    }

    private static DslEvalOutcome ToDslScalarOutcome(MathResult<decimal> result, int position, string name)
        => CalculatorArrayFunctions.ToEvalOutcome(CalculatorElementaryFunctions.FromMathResult(result, position, name));

    private static DslEvalOutcome EvaluatePortReturn(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        in DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' expects (weights, returns).", name);
        }

        ReadOnlySpan<decimal> wDec = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> rDec = arena.GetSlice(args[1].Array);
        return ToDslScalarOutcome(DslFinanceMath.PortReturn(wDec, rDec), position, name);
    }

    private static bool TryValidatePortfolioDimensions(
        ReadOnlySpan<decimal> wDec,
        ReadOnlySpan<decimal> covDec,
        int position,
        string name,
        out DslEvalOutcome errorOutcome)
    {
        int n = wDec.Length;
        if (n == 0)
        {
            errorOutcome = CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
            return false;
        }

        if (n > DslLinearAlgebraMath.MaxMatrixDim || covDec.Length != n * n)
        {
            errorOutcome = CalculatorMatrixFunctions.FailMath(MathErrorCode.DimensionMismatch, position, name);
            return false;
        }

        errorOutcome = default;
        return true;
    }

    private static DslEvalOutcome EvaluatePortfolioVarianceOrVol(
        bool returnVol,
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        in DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray || (returnVol && args.Length >= 3 && args[2].IsArray))
        {
            string sig = returnVol ? "(weights, covMatrix[, periodsPerYear])" : "(weights, covMatrix)";
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' expects {sig}.", name);
        }

        ReadOnlySpan<decimal> wDec = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> covDec = arena.GetSlice(args[1].Array);
        if (!TryValidatePortfolioDimensions(wDec, covDec, position, name, out DslEvalOutcome errOutcome))
        {
            return errOutcome;
        }

        int n = wDec.Length;
        Span<double> wBuf = stackalloc double[n];
        Span<double> covBuf = stackalloc double[n * n];
        CalculatorMatrixFunctions.CopyToDouble(wDec, wBuf);
        CalculatorMatrixFunctions.CopyToDouble(covDec, covBuf);

        MathResult<decimal> result = returnVol
            ? DslFinanceMath.PortVol(wBuf, covBuf, args.Length >= 3 ? (double)args[2].Scalar : 1.0, ref budget)
            : DslFinanceMath.PortVariance(wBuf, covBuf, ref budget);
        return ToDslScalarOutcome(result, position, name);
    }

    private static DslEvalOutcome EvaluatePortfolioRiskContribution(
        bool isCrc,
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' expects (weights, covMatrix).", name);
        }

        ReadOnlySpan<decimal> wDec = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> covDec = arena.GetSlice(args[1].Array);
        if (!TryValidatePortfolioDimensions(wDec, covDec, position, name, out DslEvalOutcome errOutcome))
        {
            return errOutcome;
        }

        int n = wDec.Length;
        Span<double> wBuf = stackalloc double[n];
        Span<double> covBuf = stackalloc double[n * n];
        Span<double> outBuf = stackalloc double[n];
        CalculatorMatrixFunctions.CopyToDouble(wDec, wBuf);
        CalculatorMatrixFunctions.CopyToDouble(covDec, covBuf);

        MathErrorCode err = isCrc
            ? DslFinanceMath.PortCrc(wBuf, covBuf, outBuf, ref budget)
            : DslFinanceMath.PortMrc(wBuf, covBuf, outBuf, ref budget);
        return err == MathErrorCode.None
            ? CalculatorMatrixFunctions.NarrowArrayResult(outBuf, position, name, ref arena)
            : CalculatorMatrixFunctions.FailMath(err, position, name);
    }

    private static DslEvalOutcome EvaluateTrackingError(
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
            TwoSeriesMetricKind.TrackingError,
            periodsPerYear,
            0.0);
    }

    private static DslEvalOutcome EvaluatePortMinVarWeights(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' expects (covMatrix).", name);
        }

        ReadOnlySpan<decimal> covDec = arena.GetSlice(args[0].Array);
        if (covDec.Length == 0)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        int n = (int)Math.Round(Math.Sqrt(covDec.Length));
        if (n < 1 || n > DslLinearAlgebraMath.MaxMatrixDim || n * n != covDec.Length)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        Span<double> covBuf = stackalloc double[n * n];
        Span<double> outBuf = stackalloc double[n];
        CalculatorMatrixFunctions.CopyToDouble(covDec, covBuf);

        MathErrorCode err = DslFinanceMath.PortMinVarWeights(covBuf, outBuf, out int dim, ref budget);
        return err == MathErrorCode.None
            ? CalculatorMatrixFunctions.NarrowArrayResult(outBuf.Slice(0, dim), position, name, ref arena)
            : CalculatorMatrixFunctions.FailMath(err, position, name);
    }

    private static DslEvalOutcome EvaluateIrsFixedRate(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        in DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' expects (discountFactors, accrualFractions).", name);
        }

        ReadOnlySpan<decimal> dfDec = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> alphaDec = arena.GetSlice(args[1].Array);
        return ToDslScalarOutcome(DslFinanceMath.IrsFixedRate(dfDec, alphaDec), position, name);
    }
}
