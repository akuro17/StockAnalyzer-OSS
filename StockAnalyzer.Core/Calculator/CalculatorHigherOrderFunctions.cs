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
        "quad_romberg";

    public static CalculatorOutcome Evaluate(
        CallNode call,
        IReadOnlyDictionary<string, decimal> names,
        CalculatorLimits limits,
        ref DslExecutionBudget budget,
        in BoundVariableScope boundScope)
    {
        IReadOnlyList<CalcNode> rawArgs = call.Arguments;
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
            CalculatorOutcome scalarOutcome = CalculatorEvaluator.Evaluate(rawArgs[i + 2], names, limits, ref budget, in boundScope);
            if (scalarOutcome.Error is not null)
            {
                return scalarOutcome;
            }

            scalars[i] = scalarOutcome.Value!.Value;
        }

        var fn = new AstScalarFunction(rawArgs[0], dummyVar.Name, names, limits, in boundScope);

        MathResult<double> rawResult;
        switch (call.Name)
        {
            case "diff_central":
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

            default:
                throw new InvalidOperationException($"Unsupported higher-order function '{call.Name}'.");
        }

        if (!rawResult.IsOk)
        {
            return CalculatorElementaryFunctions.FromMathError(rawResult.Error, call.NamePosition, call.Name);
        }

        return CalculatorElementaryFunctions.FromMathResult(
            DslElementaryMath.NarrowDouble(rawResult.Value),
            call.NamePosition,
            call.Name);
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
