using System;
using System.Collections.Generic;
using StockAnalyzer.Core.MathUtils;

namespace StockAnalyzer.Core.Calculator;

/// <summary>
/// Evaluator dispatch for Phase 4 AST-bound 1D higher-order numerical analysis and quadrature functions
/// (<c>diff_central</c>, <c>root_brent</c>, <c>root_newton</c>, <c>root_secant</c>,
/// <c>quad_trapz</c>, <c>quad_simpson</c>, <c>quad_de</c>, <c>quad_gauss_legendre</c>, <c>quad_gk21</c>, <c>quad_romberg</c>).
/// Argument 0 is an expression AST and argument 1 is the dummy variable identifier.
/// </summary>
internal static class CalculatorHigherOrderFunctions
{
    private const double DefaultTolerance = 1e-10;

    public static bool IsHigherOrderFunction(string name) => name is
        "diff_central" or
        "root_brent" or
        "root_newton" or
        "root_secant" or
        "quad_trapz" or
        "quad_simpson" or
        "quad_de" or
        "quad_gauss_legendre" or
        "quad_gk21" or
        "quad_romberg" or
        "mc_integrate" or
        "mc_integrate_se" or
        "mc_integrate_nd" or
        "mc_integrate_nd_se" or
        "fd_forward" or
        "fd_backward" or
        "fd_central" or
        "fd_second" or
        "fd_richardson" or
        "quad_gauss_chebyshev" or
        "quad_gauss_laguerre" or
        "quad_gauss_hermite" or
        "quad_gauss_jacobi" or
        "quad_gauss_lobatto" or
        "ode_rk4" or
        "ode_rk45" or
        "opt_brent" or
        "opt_golden" or
        "opt_nelder_mead" or
        "opt_bfgs";

    /// <summary>True for the higher-order functions whose argument 2 is a literal array declaring the state variables (<c>yVars</c>).</summary>
    public static bool DeclaresStateVariables(string name) => name is "ode_rk4" or "ode_rk45";

    /// <summary>
    /// Evaluates a higher-order call. Scalar-valued functions return a scalar; <c>ode_rk4</c>, <c>ode_rk45</c>,
    /// <c>opt_nelder_mead</c> and <c>opt_bfgs</c> return a 1D array allocated in <paramref name="arena"/>.
    /// </summary>
    public static DslEvalOutcome Evaluate(
        CallNode call,
        in DslEvalScope scope,
        CalculatorLimits limits,
        ref DslExecutionBudget budget,
        in BoundVariableScope boundScope,
        ref DslArrayArena arena)
    {
        // A failure detail left by an earlier call must never be read as the result of this one.
        FunctorFailureBridge.Clear(ref budget);

        switch (call.Name)
        {
            case "ode_rk4":
            case "ode_rk45":
                return EvaluateOde(call, in scope, limits, ref budget, in boundScope, ref arena);

            case "opt_nelder_mead":
            case "opt_bfgs":
                return EvaluateMultivariateOptimization(call, in scope, limits, ref budget, in boundScope, ref arena);

            default:
                return CalculatorArrayFunctions.ToEvalOutcome(
                    EvaluateScalarResult(call, in scope, limits, ref budget, in boundScope, ref arena));
        }
    }

    private static CalculatorOutcome EvaluateScalarResult(
        CallNode call,
        in DslEvalScope scope,
        CalculatorLimits limits,
        ref DslExecutionBudget budget,
        in BoundVariableScope boundScope,
        ref DslArrayArena arena)
    {
        IReadOnlyList<CalcNode> rawArgs = call.Arguments;
        if (call.Name is "mc_integrate_nd" or "mc_integrate_nd_se")
        {
            return EvaluateMcNd(call, in scope, limits, ref budget, in boundScope, ref arena);
        }

        if (rawArgs[1] is not IdentifierNode dummyVar)
        {
            return CalculatorEvaluator.Fail(
                CalculatorErrorCode.DomainError,
                CalculatorEvaluator.GetNodePosition(rawArgs[1]),
                $"Higher-order function '{call.Name}' requires a dummy variable identifier as its second argument.",
                call.Name);
        }

        if (dummyVar.Name is "nan" or "inf")
        {
            return CalculatorEvaluator.Fail(
                CalculatorErrorCode.DomainError,
                dummyVar.Position,
                $"Non-finite identifier '{dummyVar.Name}' cannot be used as a bound variable.",
                dummyVar.Name);
        }

        int scalarCount = rawArgs.Count - 2;
        Span<decimal> scalars = stackalloc decimal[scalarCount];
        for (int i = 0; i < scalarCount; i++)
        {
            if (!TryEvaluateScalarArg(rawArgs[i + 2], in scope, limits, ref budget, in boundScope, ref arena, call.Name, out scalars[i], out CalculatorOutcome argErr))
            {
                return argErr;
            }
        }

        var fn = new AstScalarFunction(rawArgs[0], dummyVar.Name, scope.ScalarNames, limits, in boundScope);

        MathResult<double> rawResult;
        switch (call.Name)
        {
            case "diff_central":
            case "fd_central":
            {
                double x = (double)scalars[0];
                double h = scalarCount >= 2 ? (double)scalars[1] : 0.0;
                if (scalarCount >= 2 && scalars[1] <= 0m)
                {
                    return CalculatorEvaluator.Fail(
                        CalculatorErrorCode.DomainError,
                        call.NamePosition,
                        $"Step size h must be within [1e-9, 1e-2] in '{call.Name}'.",
                        call.Name);
                }

                rawResult = DslNumericalAnalysisMath.DiffCentral(fn, x, h, ref budget);
                break;
            }

            case "fd_forward":
                rawResult = DslFiniteDifferenceMath.FdForward(fn, (double)scalars[0], (double)scalars[1], ref budget);
                break;

            case "fd_backward":
                rawResult = DslFiniteDifferenceMath.FdBackward(fn, (double)scalars[0], (double)scalars[1], ref budget);
                break;

            case "fd_second":
                rawResult = DslFiniteDifferenceMath.FdSecond(fn, (double)scalars[0], (double)scalars[1], ref budget);
                break;

            case "fd_richardson":
            {
                int order = 2;
                if (scalarCount >= 3 && !DslMathValidation.TryReadInt32InRange(scalars[2], 1, DslFiniteDifferenceMath.MaxDiffOrder, out order))
                {
                    return CalculatorElementaryFunctions.FromMathError(MathErrorCode.DomainError, call.NamePosition, call.Name);
                }

                rawResult = DslFiniteDifferenceMath.FdRichardson(fn, (double)scalars[0], (double)scalars[1], order, ref budget);
                break;
            }

            case "mc_integrate":
            case "mc_integrate_se":
            {
                if (!DslMathValidation.TryReadInt32InRange(scalars[2], DslMonteCarloMath.MinSamples, DslMonteCarloMath.MaxSamples, out int nSamples) ||
                    !DslMathValidation.TryReadUInt64Seed(scalars[3], out ulong seed))
                {
                    return CalculatorElementaryFunctions.FromMathError(MathErrorCode.DomainError, call.NamePosition, call.Name);
                }

                int modeCode = 0;
                if (scalarCount >= 5 && !DslMathValidation.TryReadInt32InRange(scalars[4], 0, (int)MonteCarloMode.QuasiHalton, out modeCode))
                {
                    return CalculatorElementaryFunctions.FromMathError(MathErrorCode.DomainError, call.NamePosition, call.Name);
                }

                MathResult<McIntegrateResult> mcRes = DslMonteCarloMath.McIntegrate(
                    fn,
                    (double)scalars[0],
                    (double)scalars[1],
                    nSamples,
                    seed,
                    (MonteCarloMode)modeCode,
                    ref budget,
                    requireStandardError: call.Name == "mc_integrate_se");
                rawResult = mcRes.IsOk
                    ? MathResult<double>.Ok(call.Name == "mc_integrate_se" ? mcRes.Value.StandardError : mcRes.Value.Value)
                    : MathResult<double>.Fail(mcRes.Error);
                break;
            }

            case "root_brent":
            {
                double a = (double)scalars[0];
                double b = (double)scalars[1];
                double tol = scalarCount >= 3 ? (double)scalars[2] : DefaultTolerance;
                rawResult = DslNumericalAnalysisMath.RootBrent(fn, a, b, tol, ref budget);
                break;
            }

            case "root_newton":
            {
                double x0 = (double)scalars[0];
                double tol = scalarCount >= 2 ? (double)scalars[1] : DefaultTolerance;
                rawResult = DslNumericalAnalysisMath.RootNewtonNumeric(fn, x0, tol, ref budget);
                break;
            }

            case "root_secant":
            {
                double x0 = (double)scalars[0];
                double x1 = (double)scalars[1];
                double tol = scalarCount >= 3 ? (double)scalars[2] : DefaultTolerance;
                rawResult = DslNumericalAnalysisMath.RootSecant(fn, x0, x1, tol, ref budget);
                break;
            }

            case "quad_trapz":
            {
                if (!TryGetPositiveInt(scalars[2], out int n))
                {
                    return CalculatorElementaryFunctions.FromMathError(MathErrorCode.DomainError, call.NamePosition, call.Name);
                }

                rawResult = DslQuadratureMath.QuadTrapz(fn, (double)scalars[0], (double)scalars[1], n, ref budget);
                break;
            }

            case "quad_simpson":
            {
                if (!TryGetPositiveInt(scalars[2], out int n))
                {
                    return CalculatorElementaryFunctions.FromMathError(MathErrorCode.DomainError, call.NamePosition, call.Name);
                }

                rawResult = DslQuadratureMath.QuadSimpson(fn, (double)scalars[0], (double)scalars[1], n, ref budget);
                break;
            }

            case "quad_de":
            {
                double tol = scalarCount >= 3 ? (double)scalars[2] : DefaultTolerance;
                rawResult = DslQuadratureMath.QuadDe(fn, (double)scalars[0], (double)scalars[1], tol, ref budget);
                break;
            }

            case "quad_gauss_legendre":
            {
                if (!TryGetPositiveInt(scalars[2], out int n))
                {
                    return CalculatorElementaryFunctions.FromMathError(MathErrorCode.DomainError, call.NamePosition, call.Name);
                }

                rawResult = DslQuadratureMath.QuadGaussLegendre(fn, (double)scalars[0], (double)scalars[1], n, ref budget);
                break;
            }

            case "quad_gk21":
            {
                double tol = scalarCount >= 3 ? (double)scalars[2] : DefaultTolerance;
                rawResult = DslQuadratureMath.QuadGk21(fn, (double)scalars[0], (double)scalars[1], tol, ref budget);
                break;
            }

            case "quad_romberg":
            {
                double tol = scalarCount >= 3 ? (double)scalars[2] : DefaultTolerance;
                rawResult = DslQuadratureMath.QuadRomberg(fn, (double)scalars[0], (double)scalars[1], tol, ref budget);
                break;
            }

            case "quad_gauss_chebyshev":
            {
                if (!DslMathValidation.TryReadInt32InRange(scalars[0], 1, 2, out int kind) ||
                    !DslMathValidation.TryReadInt32InRange(scalars[1], 1, DslQuadratureMath.MaxChebyshevOrder, out int n))
                {
                    return CalculatorElementaryFunctions.FromMathError(MathErrorCode.DomainError, call.NamePosition, call.Name);
                }

                rawResult = DslQuadratureMath.QuadGaussChebyshevDsl(fn, kind, n, ref budget);
                break;
            }

            case "quad_gauss_laguerre":
            {
                if (!DslMathValidation.TryReadInt32InRange(scalars[1], 1, DslQuadratureMath.MaxDslGaussOrder, out int n))
                {
                    return CalculatorElementaryFunctions.FromMathError(MathErrorCode.DomainError, call.NamePosition, call.Name);
                }

                rawResult = DslQuadratureMath.QuadGaussLaguerreDsl(fn, (double)scalars[0], n, ref budget);
                break;
            }

            case "quad_gauss_hermite":
            {
                if (!DslMathValidation.TryReadInt32InRange(scalars[0], 1, DslQuadratureMath.MaxDslGaussOrder, out int n))
                {
                    return CalculatorElementaryFunctions.FromMathError(MathErrorCode.DomainError, call.NamePosition, call.Name);
                }

                rawResult = DslQuadratureMath.QuadGaussHermiteDsl(fn, n, ref budget);
                break;
            }

            case "quad_gauss_jacobi":
            {
                if (!DslMathValidation.TryReadInt32InRange(scalars[2], 1, DslQuadratureMath.MaxDslGaussOrder, out int n))
                {
                    return CalculatorElementaryFunctions.FromMathError(MathErrorCode.DomainError, call.NamePosition, call.Name);
                }

                rawResult = DslQuadratureMath.QuadGaussJacobiDsl(fn, (double)scalars[0], (double)scalars[1], n, ref budget);
                break;
            }

            case "quad_gauss_lobatto":
            {
                if (!DslMathValidation.TryReadInt32InRange(scalars[2], DslQuadratureMath.MinLobattoOrder, DslQuadratureMath.MaxDslGaussOrder, out int n))
                {
                    return CalculatorElementaryFunctions.FromMathError(MathErrorCode.DomainError, call.NamePosition, call.Name);
                }

                rawResult = DslQuadratureMath.QuadGaussLobattoDsl(fn, (double)scalars[0], (double)scalars[1], n, ref budget);
                break;
            }

            case "opt_brent":
            {
                double tol = scalarCount >= 3 ? (double)scalars[2] : DslOptimizationMath.DefaultTol;
                rawResult = DslOptimizationMath.OptBrent(fn, (double)scalars[0], (double)scalars[1], tol, ref budget);
                break;
            }

            case "opt_golden":
            {
                double tol = scalarCount >= 3 ? (double)scalars[2] : DslOptimizationMath.DefaultTol;
                rawResult = DslOptimizationMath.OptGolden(fn, (double)scalars[0], (double)scalars[1], tol, ref budget);
                break;
            }

            default:
                throw new InvalidOperationException($"Unsupported higher-order function '{call.Name}'.");
        }

        if (!rawResult.IsOk)
        {
            return FunctorFailureBridge.ToOutcome(rawResult.Error, ref budget, call.NamePosition, call.Name);
        }

        return CalculatorElementaryFunctions.FromMathResult(
            DslElementaryMath.NarrowDouble(rawResult.Value),
            call.NamePosition,
            call.Name);
    }

    /// <summary>
    /// <c>ode_rk4(rhsExprs, tVar, yVars, t0, t1, y0, nSteps)</c> and <c>ode_rk45(rhsExprs, tVar, yVars, t0, t1, y0[, tol])</c>.
    /// The kernels reject <c>t0 == t1</c>; the DSL contract returns <c>y0</c> unchanged after all other arguments validated, so that case is answered here.
    /// </summary>
    private static DslEvalOutcome EvaluateOde(
        CallNode call,
        in DslEvalScope scope,
        CalculatorLimits limits,
        ref DslExecutionBudget budget,
        in BoundVariableScope boundScope,
        ref DslArrayArena arena)
    {
        IReadOnlyList<CalcNode> args = call.Arguments;
        bool isRk4 = call.Name == "ode_rk4";

        if (args[0] is not ArrayLiteralNode rhsLiteral ||
            rhsLiteral.Elements.Count < 1 ||
            rhsLiteral.Elements.Count > DslNumericalAnalysisMath.MaxOdeDimension)
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.DomainError,
                CalculatorEvaluator.GetNodePosition(args[0]),
                $"'{call.Name}' requires a literal array of 1..{DslNumericalAnalysisMath.MaxOdeDimension} right-hand-side expressions as its first argument.",
                call.Name);
        }

        int dim = rhsLiteral.Elements.Count;
        if (args[1] is not IdentifierNode tVar || tVar.Name is "nan" or "inf")
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.DomainError,
                CalculatorEvaluator.GetNodePosition(args[1]),
                $"'{call.Name}' requires a valid finite identifier as its independent variable (second argument).",
                call.Name);
        }

        if (args[2] is not ArrayLiteralNode stateLiteral)
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.DomainError,
                CalculatorEvaluator.GetNodePosition(args[2]),
                $"'{call.Name}' requires a literal array of state variable identifiers as its third argument.",
                call.Name);
        }

        if (stateLiteral.Elements.Count != dim)
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.DimensionMismatch,
                CalculatorEvaluator.GetNodePosition(args[2]),
                $"'{call.Name}' has {dim} right-hand sides but {stateLiteral.Elements.Count} state variables.",
                call.Name);
        }

        if (!TryReadIdentifierList(call, stateLiteral, tVar.Name, out string[] stateNames, out DslEvalOutcome nameFailure))
        {
            return nameFailure;
        }

        if (!TryEvaluateScalarArg(args[3], in scope, limits, ref budget, in boundScope, ref arena, call.Name, out decimal t0, out CalculatorOutcome t0Error))
        {
            return CalculatorArrayFunctions.ToEvalOutcome(t0Error);
        }

        if (!TryEvaluateScalarArg(args[4], in scope, limits, ref budget, in boundScope, ref arena, call.Name, out decimal t1, out CalculatorOutcome t1Error))
        {
            return CalculatorArrayFunctions.ToEvalOutcome(t1Error);
        }

        DslEvalOutcome y0Outcome = CalculatorEvaluator.EvaluateValue(args[5], in scope, limits, ref budget, in boundScope, ref arena);
        if (y0Outcome.Error is not null)
        {
            return y0Outcome;
        }

        if (!y0Outcome.Value.IsArray)
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.DomainError,
                CalculatorEvaluator.GetNodePosition(args[5]),
                $"'{call.Name}' requires a 1D initial state array as its sixth argument.",
                call.Name);
        }

        DslArrayHandle y0Handle = y0Outcome.Value.Array;
        if (y0Handle.Length != dim)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DimensionMismatch, call.NamePosition, call.Name);
        }

        // TEMPORARY SHARING: ode_rk45 reuses the optimizer tolerance contract (DslOptimizationMath.MinTol/MaxTol/DefaultTol); separation is planned (docs/ja/Future_Improvements.md).
        int nSteps = 0;
        double tol = DslOptimizationMath.DefaultTol;
        if (args.Count >= 7)
        {
            if (!TryEvaluateScalarArg(args[6], in scope, limits, ref budget, in boundScope, ref arena, call.Name, out decimal lastArg, out CalculatorOutcome lastError))
            {
                return CalculatorArrayFunctions.ToEvalOutcome(lastError);
            }

            if (isRk4)
            {
                if (!DslMathValidation.TryReadInt32InRange(lastArg, 1, DslNumericalAnalysisMath.MaxOdeSteps, out nSteps))
                {
                    return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, call.NamePosition, call.Name);
                }
            }
            else
            {
                tol = (double)lastArg;
                if (tol < DslOptimizationMath.MinTol || tol > DslOptimizationMath.MaxTol)
                {
                    return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, call.NamePosition, call.Name);
                }
            }
        }
        else if (isRk4)
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.WrongArgumentCount,
                call.NamePosition,
                $"Function '{call.Name}' expects 7 arguments, got {args.Count}.",
                call.Name);
        }

        if (t0 == t1)
        {
            if (!arena.TryAllocate(dim, out DslArrayHandle copyHandle, out Span<decimal> copy))
            {
                return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, call.NamePosition, call.Name);
            }

            arena.GetSlice(y0Handle).CopyTo(copy);
            return DslEvalOutcome.OkArray(copyHandle);
        }

        Span<double> y0 = stackalloc double[DslNumericalAnalysisMath.MaxOdeDimension];
        Span<double> yOut = stackalloc double[DslNumericalAnalysisMath.MaxOdeDimension];
        CalculatorMatrixFunctions.CopyToDouble(arena.GetSlice(y0Handle), y0.Slice(0, dim));

        var system = new AstOdeSystem(rhsLiteral.Elements, tVar.Name, stateNames, in scope, limits, in boundScope);
        MathErrorCode err = isRk4
            ? DslNumericalAnalysisMath.OdeRk4(system, (double)t0, (double)t1, y0.Slice(0, dim), nSteps, yOut.Slice(0, dim), ref budget)
            : DslNumericalAnalysisMath.OdeRk45(system, (double)t0, (double)t1, y0.Slice(0, dim), tol, yOut.Slice(0, dim), ref budget);
        if (err != MathErrorCode.None)
        {
            return CalculatorArrayFunctions.ToEvalOutcome(FunctorFailureBridge.ToOutcome(err, ref budget, call.NamePosition, call.Name));
        }

        return CalculatorMatrixFunctions.NarrowArrayResult(yOut.Slice(0, dim), call.NamePosition, call.Name, ref arena);
    }

    /// <summary><c>opt_nelder_mead(expr, vars, x0[, tol])</c> and <c>opt_bfgs(expr, vars, x0[, tol])</c>; the result is the minimizer as a 1D array.</summary>
    private static DslEvalOutcome EvaluateMultivariateOptimization(
        CallNode call,
        in DslEvalScope scope,
        CalculatorLimits limits,
        ref DslExecutionBudget budget,
        in BoundVariableScope boundScope,
        ref DslArrayArena arena)
    {
        IReadOnlyList<CalcNode> args = call.Arguments;
        if (args[1] is not ArrayLiteralNode varsLiteral ||
            varsLiteral.Elements.Count < 1 ||
            varsLiteral.Elements.Count > DslOptimizationMath.MaxOptDimensions)
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.DomainError,
                CalculatorEvaluator.GetNodePosition(args[1]),
                $"'{call.Name}' requires a literal array of 1..{DslOptimizationMath.MaxOptDimensions} dummy variable identifiers.",
                call.Name);
        }

        int dim = varsLiteral.Elements.Count;
        if (!TryReadIdentifierList(call, varsLiteral, null, out string[] varNames, out DslEvalOutcome nameFailure))
        {
            return nameFailure;
        }

        DslEvalOutcome x0Outcome = CalculatorEvaluator.EvaluateValue(args[2], in scope, limits, ref budget, in boundScope, ref arena);
        if (x0Outcome.Error is not null)
        {
            return x0Outcome;
        }

        if (!x0Outcome.Value.IsArray)
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.DomainError,
                CalculatorEvaluator.GetNodePosition(args[2]),
                $"'{call.Name}' requires a 1D start vector as its third argument.",
                call.Name);
        }

        if (x0Outcome.Value.Array.Length != dim)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DimensionMismatch, call.NamePosition, call.Name);
        }

        double tol = DslOptimizationMath.DefaultTol;
        if (args.Count >= 4)
        {
            if (!TryEvaluateScalarArg(args[3], in scope, limits, ref budget, in boundScope, ref arena, call.Name, out decimal tolDecimal, out CalculatorOutcome tolError))
            {
                return CalculatorArrayFunctions.ToEvalOutcome(tolError);
            }

            tol = (double)tolDecimal;
        }

        Span<double> x0 = stackalloc double[DslOptimizationMath.MaxOptDimensions];
        Span<double> xOut = stackalloc double[DslOptimizationMath.MaxOptDimensions];
        CalculatorMatrixFunctions.CopyToDouble(arena.GetSlice(x0Outcome.Value.Array), x0.Slice(0, dim));

        var objective = new AstNdScalarFunction(args[0], varNames, in scope, limits, in boundScope);
        MathErrorCode err = call.Name == "opt_bfgs"
            ? DslOptimizationMath.OptBfgs(objective, x0.Slice(0, dim), tol, xOut.Slice(0, dim), ref budget)
            : DslOptimizationMath.OptNelderMead(objective, x0.Slice(0, dim), tol, xOut.Slice(0, dim), ref budget);
        if (err != MathErrorCode.None)
        {
            return CalculatorArrayFunctions.ToEvalOutcome(FunctorFailureBridge.ToOutcome(err, ref budget, call.NamePosition, call.Name));
        }

        return CalculatorMatrixFunctions.NarrowArrayResult(xOut.Slice(0, dim), call.NamePosition, call.Name, ref arena);
    }

    /// <summary>Reads a literal array of pairwise distinct, finite identifiers (optionally also distinct from <paramref name="reservedName"/>).</summary>
    private static bool TryReadIdentifierList(
        CallNode call,
        ArrayLiteralNode literal,
        string? reservedName,
        out string[] names,
        out DslEvalOutcome failure)
    {
        int count = literal.Elements.Count;
        names = new string[count];
        for (int i = 0; i < count; i++)
        {
            if (literal.Elements[i] is not IdentifierNode id || id.Name is "nan" or "inf")
            {
                failure = DslEvalOutcome.Fail(
                    CalculatorErrorCode.DomainError,
                    CalculatorEvaluator.GetNodePosition(literal.Elements[i]),
                    $"Dummy variable {i} in '{call.Name}' must be a valid finite identifier.",
                    call.Name);
                return false;
            }

            bool duplicate = reservedName is not null && string.Equals(reservedName, id.Name, StringComparison.Ordinal);
            for (int prev = 0; prev < i && !duplicate; prev++)
            {
                duplicate = string.Equals(names[prev], id.Name, StringComparison.Ordinal);
            }

            if (duplicate)
            {
                failure = DslEvalOutcome.Fail(
                    CalculatorErrorCode.DomainError,
                    id.Position,
                    $"Duplicate dummy variable '{id.Name}' in '{call.Name}'.",
                    id.Name);
                return false;
            }

            names[i] = id.Name;
        }

        failure = default;
        return true;
    }

    private static CalculatorOutcome EvaluateMcNd(
        CallNode call,
        in DslEvalScope scope,
        CalculatorLimits limits,
        ref DslExecutionBudget budget,
        in BoundVariableScope boundScope,
        ref DslArrayArena arena)
    {
        IReadOnlyList<CalcNode> rawArgs = call.Arguments;
        if (rawArgs[1] is not ArrayLiteralNode varsLiteral ||
            varsLiteral.Elements.Count < 1 ||
            varsLiteral.Elements.Count > DslMonteCarloMath.MaxDimensions)
        {
            return CalculatorEvaluator.Fail(
                CalculatorErrorCode.DomainError,
                CalculatorEvaluator.GetNodePosition(rawArgs[1]),
                $"Higher-order function '{call.Name}' requires a literal array of 1..{DslMonteCarloMath.MaxDimensions} dummy variable identifiers.",
                call.Name);
        }

        int dim = varsLiteral.Elements.Count;
        var varNames = new string[dim];
        for (int i = 0; i < dim; i++)
        {
            if (varsLiteral.Elements[i] is not IdentifierNode id || id.Name is "nan" or "inf")
            {
                return CalculatorEvaluator.Fail(
                    CalculatorErrorCode.DomainError,
                    CalculatorEvaluator.GetNodePosition(varsLiteral.Elements[i]),
                    $"Dummy variable {i} in '{call.Name}' must be a valid finite identifier.",
                    call.Name);
            }

            for (int prev = 0; prev < i; prev++)
            {
                if (string.Equals(varNames[prev], id.Name, StringComparison.Ordinal))
                {
                    return CalculatorEvaluator.Fail(
                        CalculatorErrorCode.DomainError,
                        id.Position,
                        $"Duplicate dummy variable '{id.Name}' in '{call.Name}'.",
                        id.Name);
                }
            }

            varNames[i] = id.Name;
        }

        DslEvalOutcome loOutcome = CalculatorEvaluator.EvaluateValue(rawArgs[2], in scope, limits, ref budget, in boundScope, ref arena);
        if (loOutcome.Error is { } loErr) return new CalculatorOutcome(null, loErr);
        DslEvalOutcome hiOutcome = CalculatorEvaluator.EvaluateValue(rawArgs[3], in scope, limits, ref budget, in boundScope, ref arena);
        if (hiOutcome.Error is { } hiErr) return new CalculatorOutcome(null, hiErr);

        if (!loOutcome.Value.IsArray || !hiOutcome.Value.IsArray)
        {
            return CalculatorEvaluator.Fail(
                CalculatorErrorCode.DomainError,
                call.NamePosition,
                $"'{call.Name}' requires 1D lower and upper bound arrays.",
                call.Name);
        }

        ReadOnlySpan<decimal> loDec = arena.GetSlice(loOutcome.Value.Array);
        ReadOnlySpan<decimal> hiDec = arena.GetSlice(hiOutcome.Value.Array);
        if (loDec.Length != dim || hiDec.Length != dim)
        {
            return CalculatorElementaryFunctions.FromMathError(MathErrorCode.DimensionMismatch, call.NamePosition, call.Name);
        }

        int tailCount = rawArgs.Count - 4;
        Span<decimal> tail = stackalloc decimal[tailCount];
        for (int i = 0; i < tailCount; i++)
        {
            if (!TryEvaluateScalarArg(rawArgs[4 + i], in scope, limits, ref budget, in boundScope, ref arena, call.Name, out tail[i], out CalculatorOutcome tailErr))
            {
                return tailErr;
            }
        }

        if (!DslMathValidation.TryReadInt32InRange(tail[0], DslMonteCarloMath.MinSamples, DslMonteCarloMath.MaxSamples, out int nSamples) ||
            !DslMathValidation.TryReadUInt64Seed(tail[1], out ulong seed))
        {
            return CalculatorElementaryFunctions.FromMathError(MathErrorCode.DomainError, call.NamePosition, call.Name);
        }

        int modeCode = 0;
        if (tailCount >= 3 && !DslMathValidation.TryReadInt32InRange(tail[2], 0, (int)MonteCarloMode.QuasiHalton, out modeCode))
        {
            return CalculatorElementaryFunctions.FromMathError(MathErrorCode.DomainError, call.NamePosition, call.Name);
        }

        Span<double> lower = stackalloc double[dim];
        Span<double> upper = stackalloc double[dim];
        CalculatorMatrixFunctions.CopyToDouble(loDec, lower);
        CalculatorMatrixFunctions.CopyToDouble(hiDec, upper);

        var fnNd = new AstNdScalarFunction(rawArgs[0], varNames, in scope, limits, in boundScope);
        MathResult<McIntegrateResult> mcRes = DslMonteCarloMath.McIntegrateNd(
            fnNd,
            lower,
            upper,
            nSamples,
            seed,
            (MonteCarloMode)modeCode,
            ref budget,
            requireStandardError: call.Name == "mc_integrate_nd_se");
        if (!mcRes.IsOk)
        {
            return FunctorFailureBridge.ToOutcome(mcRes.Error, ref budget, call.NamePosition, call.Name);
        }

        double val = call.Name == "mc_integrate_nd_se" ? mcRes.Value.StandardError : mcRes.Value.Value;
        return CalculatorElementaryFunctions.FromMathResult(
            DslElementaryMath.NarrowDouble(val),
            call.NamePosition,
            call.Name);
    }

    private static bool TryEvaluateScalarArg(
        CalcNode node,
        in DslEvalScope scope,
        CalculatorLimits limits,
        ref DslExecutionBudget budget,
        in BoundVariableScope boundScope,
        ref DslArrayArena arena,
        string callerName,
        out decimal value,
        out CalculatorOutcome errorOutcome)
    {
        DslEvalOutcome outcome = CalculatorEvaluator.EvaluateValue(node, in scope, limits, ref budget, in boundScope, ref arena);
        if (outcome.Error is { } err)
        {
            value = 0m;
            errorOutcome = new CalculatorOutcome(null, err);
            return false;
        }

        if (outcome.Value.IsArray)
        {
            value = 0m;
            errorOutcome = CalculatorEvaluator.Fail(
                CalculatorErrorCode.DomainError,
                CalculatorEvaluator.GetNodePosition(node),
                $"Scalar argument in '{callerName}' cannot evaluate to an array.",
                callerName);
            return false;
        }

        value = outcome.Value.Scalar;
        errorOutcome = default;
        return true;
    }

    private static bool TryGetPositiveInt(decimal value, out int integer)
    {
        if (value == decimal.Truncate(value) && value >= 1m && value <= int.MaxValue)
        {
            integer = (int)value;
            return true;
        }

        integer = 0;
        return false;
    }
}
