using System;
using StockAnalyzer.Core.MathUtils;

namespace StockAnalyzer.Core.Calculator;

/// <summary>
/// Evaluator dispatch for Phase 1 DSL elementary, trigonometric, combinatorial,
/// polynomial-projection, geometric, complex-projection, sample-statistic, and activation functions.
/// </summary>
internal static class CalculatorElementaryFunctions
{
    public static bool TryEvaluate(
        string name,
        int position,
        scoped ReadOnlySpan<decimal> args,
        out CalculatorOutcome outcome)
    {
        switch (name)
        {
            case "cbrt":
                outcome = FromMathResult(DslElementaryMath.Cbrt(args[0]), position, name);
                return true;

            case "log10":
                outcome = FromMathResult(DslElementaryMath.Log10(args[0]), position, name);
                return true;

            case "log2":
                outcome = FromMathResult(DslElementaryMath.Log2(args[0]), position, name);
                return true;

            case "log":
                outcome = FromMathResult(DslElementaryMath.LogBase(args[0], args[1]), position, name);
                return true;

            case "expm1":
                outcome = FromMathResult(DslElementaryMath.Expm1(args[0]), position, name);
                return true;

            case "log1p":
                outcome = FromMathResult(DslElementaryMath.Log1p(args[0]), position, name);
                return true;

            case "sin":
                outcome = FromMathResult(DslElementaryMath.Sin(args[0]), position, name);
                return true;

            case "cos":
                outcome = FromMathResult(DslElementaryMath.Cos(args[0]), position, name);
                return true;

            case "tan":
                outcome = FromMathResult(DslElementaryMath.Tan(args[0]), position, name);
                return true;

            case "sind":
                outcome = FromMathResult(DslElementaryMath.SinDeg(args[0]), position, name);
                return true;

            case "cosd":
                outcome = FromMathResult(DslElementaryMath.CosDeg(args[0]), position, name);
                return true;

            case "tand":
                outcome = FromMathResult(DslElementaryMath.TanDeg(args[0]), position, name);
                return true;

            case "asin":
                outcome = FromMathResult(DslElementaryMath.Asin(args[0]), position, name);
                return true;

            case "acos":
                outcome = FromMathResult(DslElementaryMath.Acos(args[0]), position, name);
                return true;

            case "atan":
                outcome = FromMathResult(DslElementaryMath.Atan(args[0]), position, name);
                return true;

            case "atan2":
                outcome = FromMathResult(DslElementaryMath.Atan2(args[0], args[1]), position, name);
                return true;

            case "sinh":
                outcome = FromMathResult(DslElementaryMath.Sinh(args[0]), position, name);
                return true;

            case "cosh":
                outcome = FromMathResult(DslElementaryMath.Cosh(args[0]), position, name);
                return true;

            case "tanh":
                outcome = FromMathResult(DslElementaryMath.Tanh(args[0]), position, name);
                return true;

            case "asinh":
                outcome = FromMathResult(DslElementaryMath.Asinh(args[0]), position, name);
                return true;

            case "acosh":
                outcome = FromMathResult(DslElementaryMath.Acosh(args[0]), position, name);
                return true;

            case "atanh":
                outcome = FromMathResult(DslElementaryMath.Atanh(args[0]), position, name);
                return true;

            case "sum_arith":
                outcome = FromMathResult(DslElementaryMath.SumArith(args[0], args[1], args[2]), position, name);
                return true;

            case "sum_geom":
                outcome = FromMathResult(DslElementaryMath.SumGeom(args[0], args[1], args[2]), position, name);
                return true;

            case "fact":
                outcome = FromMathResult(DslElementaryMath.Fact(args[0]), position, name);
                return true;

            case "double_fact":
                outcome = FromMathResult(DslElementaryMath.DoubleFact(args[0]), position, name);
                return true;

            case "perm":
                outcome = FromMathResult(DslElementaryMath.Perm(args[0], args[1]), position, name);
                return true;

            case "comb":
                outcome = FromMathResult(DslElementaryMath.Comb(args[0], args[1]), position, name);
                return true;

            case "lcomb":
                outcome = FromMathResult(DslElementaryMath.LComb(args[0], args[1]), position, name);
                return true;

            case "fib":
                outcome = FromMathResult(DslElementaryMath.Fib(args[0]), position, name);
                return true;

            case "lucas":
                outcome = FromMathResult(DslElementaryMath.Lucas(args[0]), position, name);
                return true;

            case "solve_linear":
                outcome = FromMathResult(DslElementaryMath.SolveLinear(args[0], args[1]), position, name);
                return true;

            case "quad_discriminant":
                outcome = FromMathResult(DslElementaryMath.QuadraticDiscriminant(args[0], args[1], args[2]), position, name);
                return true;

            case "quad_root_min":
                outcome = EvaluateQuadraticRoot(position, name, args[0], args[1], args[2], takeMax: false);
                return true;

            case "quad_root_max":
                outcome = EvaluateQuadraticRoot(position, name, args[0], args[1], args[2], takeMax: true);
                return true;

            case "hypot":
                outcome = FromMathResult(DslElementaryMath.Hypot(args[0], args[1]), position, name);
                return true;

            case "dist2d":
                outcome = FromMathResult(DslElementaryMath.Dist2D(args[0], args[1], args[2], args[3]), position, name);
                return true;

            case "dist3d":
                outcome = FromMathResult(DslElementaryMath.Dist3D(args[0], args[1], args[2], args[3], args[4], args[5]), position, name);
                return true;

            case "tri_area_sss":
                outcome = EvaluateTriangleMetric(position, name, args[0], args[1], args[2], 0);
                return true;

            case "tri_inradius":
                outcome = EvaluateTriangleMetric(position, name, args[0], args[1], args[2], 1);
                return true;

            case "tri_circumradius":
                outcome = EvaluateTriangleMetric(position, name, args[0], args[1], args[2], 2);
                return true;

            case "sphere_vol":
                outcome = FromMathResult(DslElementaryMath.SphereVolume(args[0]), position, name);
                return true;

            case "cone_area":
                outcome = FromMathResult(DslElementaryMath.ConeSurfaceArea(args[0], args[1]), position, name);
                return true;

            case "torus_vol":
                outcome = FromMathResult(DslElementaryMath.TorusVolume(args[0], args[1]), position, name);
                return true;

            case "c_abs":
                outcome = FromMathResult(DslElementaryMath.ComplexAbs(new DslComplex(args[0], args[1])), position, name);
                return true;

            case "c_arg":
                outcome = FromMathResult(DslElementaryMath.ComplexArg(new DslComplex(args[0], args[1])), position, name);
                return true;

            case "c_exp_re":
            {
                MathResult<DslComplex> res = DslElementaryMath.ComplexExp(new DslComplex(args[0], args[1]));
                outcome = res.IsOk
                    ? CalculatorEvaluator.Ok(res.Value.Re)
                    : FromMathError(res.Error, position, name);
                return true;
            }

            case "c_exp_im":
            {
                MathResult<DslComplex> res = DslElementaryMath.ComplexExp(new DslComplex(args[0], args[1]));
                outcome = res.IsOk
                    ? CalculatorEvaluator.Ok(res.Value.Im)
                    : FromMathError(res.Error, position, name);
                return true;
            }

            case "c_mul_re":
            case "c_mul_im":
                outcome = ComplexComponent(
                    DslElementaryMath.ComplexMultiply(new DslComplex(args[0], args[1]), new DslComplex(args[2], args[3])),
                    takeReal: name == "c_mul_re",
                    position,
                    name);
                return true;

            case "c_div_re":
            case "c_div_im":
                outcome = ComplexComponent(
                    DslElementaryMath.ComplexDivide(new DslComplex(args[0], args[1]), new DslComplex(args[2], args[3])),
                    takeReal: name == "c_div_re",
                    position,
                    name);
                return true;

            case "c_ln_re":
            case "c_ln_im":
                outcome = ComplexComponent(
                    DslElementaryMath.ComplexLn(new DslComplex(args[0], args[1])),
                    takeReal: name == "c_ln_re",
                    position,
                    name);
                return true;

            case "c_sqrt_re":
            case "c_sqrt_im":
                outcome = ComplexComponent(
                    DslElementaryMath.ComplexSqrt(new DslComplex(args[0], args[1])),
                    takeReal: name == "c_sqrt_re",
                    position,
                    name);
                return true;

            case "c_pow_re":
            case "c_pow_im":
                // 0^w = 0 for every w with Re(w) > 0 (including Im(w) != 0); every other case is the principal-branch exp(w ln z).
                outcome = args[0] == 0m && args[1] == 0m && args[2] > 0m
                    ? CalculatorEvaluator.Ok(0m)
                    : ComplexComponent(
                        DslElementaryMath.ComplexPow(new DslComplex(args[0], args[1]), new DslComplex(args[2], args[3])),
                        takeReal: name == "c_pow_re",
                        position,
                        name);
                return true;

            case "if_gt":
                outcome = CalculatorEvaluator.Ok(args[0] > args[1] ? args[2] : args[3]);
                return true;

            case "if_ge":
                outcome = CalculatorEvaluator.Ok(args[0] >= args[1] ? args[2] : args[3]);
                return true;

            case "if_eq":
                outcome = EvaluateIfEqual(position, args);
                return true;

            case "step":
                outcome = CalculatorEvaluator.Ok(args[0] >= 0m ? 1m : 0m);
                return true;

            case "bayes":
                outcome = FromMathResult(DslElementaryMath.Bayes(args[0], args[1], args[2]), position, name);
                return true;

            case "var_p":
                outcome = FromMathResult(DslElementaryMath.Variance(args, ddof: 0), position, name);
                return true;

            case "var_s":
                outcome = FromMathResult(DslElementaryMath.Variance(args, ddof: 1), position, name);
                return true;

            case "stdev_p":
            {
                MathResult<decimal> varRes = DslElementaryMath.Variance(args, ddof: 0);
                outcome = varRes.IsOk
                    ? CalculatorTranscendentals.EvaluateSqrt(position, varRes.Value)
                    : FromMathError(varRes.Error, position, name);
                return true;
            }

            case "stdev_s":
            {
                MathResult<decimal> varRes = DslElementaryMath.Variance(args, ddof: 1);
                outcome = varRes.IsOk
                    ? CalculatorTranscendentals.EvaluateSqrt(position, varRes.Value)
                    : FromMathError(varRes.Error, position, name);
                return true;
            }

            case "median":
                outcome = FromMathResult(DslElementaryMath.Median(args), position, name);
                return true;

            case "sigmoid":
                outcome = FromMathResult(DslElementaryMath.Sigmoid(args[0]), position, name);
                return true;

            case "relu":
                outcome = CalculatorEvaluator.Ok(args[0] > 0m ? args[0] : 0m);
                return true;

            case "leaky_relu":
                outcome = CalculatorEvaluator.Ok(args[0] >= 0m ? args[0] : checked(args[1] * args[0]));
                return true;

            case "elu":
                if (args[0] >= 0m)
                {
                    outcome = CalculatorEvaluator.Ok(args[0]);
                    return true;
                }
                else
                {
                    MathResult<decimal> ex = DslElementaryMath.Expm1(args[0]);
                    outcome = ex.IsOk
                        ? CalculatorEvaluator.Ok(checked(args[1] * ex.Value))
                        : FromMathError(ex.Error, position, name);
                    return true;
                }

            case "gelu":
                outcome = FromMathResult(DslElementaryMath.Gelu(args[0]), position, name);
                return true;

            case "silu":
                if (args[0] == 0m)
                {
                    outcome = CalculatorEvaluator.Ok(0m);
                    return true;
                }
                else
                {
                    MathResult<decimal> sig = DslElementaryMath.Sigmoid(args[0]);
                    outcome = sig.IsOk
                        ? CalculatorEvaluator.Ok(checked(args[0] * sig.Value))
                        : FromMathError(sig.Error, position, name);
                    return true;
                }

            case "softplus":
                if (args[0] > 30m)
                {
                    outcome = CalculatorEvaluator.Ok(args[0]);
                    return true;
                }
                else if (args[0] < -30m)
                {
                    outcome = CalculatorTranscendentals.EvaluateExp(position, args[0]);
                    return true;
                }
                else
                {
                    CalculatorOutcome expOutcome = CalculatorTranscendentals.EvaluateExp(position, args[0]);
                    outcome = expOutcome.Error is not null
                        ? expOutcome
                        : FromMathResult(DslElementaryMath.Log1p(expOutcome.Value!.Value), position, name);
                    return true;
                }

            case "swish":
            {
                decimal bx = checked(args[1] * args[0]);
                if (args[0] == 0m)
                {
                    outcome = CalculatorEvaluator.Ok(0m);
                    return true;
                }

                MathResult<decimal> sig = DslElementaryMath.Sigmoid(bx);
                outcome = sig.IsOk
                    ? CalculatorEvaluator.Ok(checked(args[0] * sig.Value))
                    : FromMathError(sig.Error, position, name);
                return true;
            }

            default:
                outcome = default;
                return false;
        }
    }

    private static CalculatorOutcome ComplexComponent(
        MathResult<DslComplex> result,
        bool takeReal,
        int position,
        string name)
        => result.IsOk
            ? CalculatorEvaluator.Ok(takeReal ? result.Value.Re : result.Value.Im)
            : FromMathError(result.Error, position, name);

    /// <summary><c>if_eq(a, b, whenTrue, whenFalse[, eps])</c>: exact equality, or <c>|a - b| &lt;= eps</c> when <c>eps</c> (non-negative) is given.</summary>
    private static CalculatorOutcome EvaluateIfEqual(int position, scoped ReadOnlySpan<decimal> args)
    {
        bool equal;
        if (args.Length >= 5)
        {
            if (args[4] < 0m)
            {
                return FromMathError(MathErrorCode.DomainError, position, "if_eq");
            }

            equal = Math.Abs(checked(args[0] - args[1])) <= args[4];
        }
        else
        {
            equal = args[0] == args[1];
        }

        return CalculatorEvaluator.Ok(equal ? args[2] : args[3]);
    }

    private static CalculatorOutcome EvaluateQuadraticRoot(
        int position,
        string name,
        decimal a,
        decimal b,
        decimal c,
        bool takeMax)
    {
        Span<decimal> roots = stackalloc decimal[2];
        MathErrorCode err = DslElementaryMath.SolveQuadratic(a, b, c, roots, out int count);
        if (err != MathErrorCode.None)
        {
            return FromMathError(err, position, name);
        }

        if (count == 0)
        {
            return CalculatorEvaluator.Fail(
                CalculatorErrorCode.DomainError,
                position,
                $"Quadratic equation has no real roots in '{name}' at position {position}.",
                name);
        }

        return CalculatorEvaluator.Ok(takeMax ? roots[count - 1] : roots[0]);
    }

    private static CalculatorOutcome EvaluateTriangleMetric(
        int position,
        string name,
        decimal a,
        decimal b,
        decimal c,
        int metricIndex)
    {
        MathErrorCode err = DslElementaryMath.TriangleMetricsSss(a, b, c, out decimal area, out decimal inradius, out decimal circumradius);
        if (err != MathErrorCode.None)
        {
            return FromMathError(err, position, name);
        }

        return CalculatorEvaluator.Ok(metricIndex switch
        {
            0 => area,
            1 => inradius,
            _ => circumradius,
        });
    }

    internal static CalculatorOutcome FromMathResult(
        MathResult<decimal> result,
        int position,
        string functionName)
        => result.IsOk
            ? CalculatorEvaluator.Ok(result.Value)
            : FromMathError(result.Error, position, functionName);

    internal static CalculatorOutcome FromMathError(
        MathErrorCode error,
        int position,
        string functionName) => error switch
    {
        MathErrorCode.DomainError => CalculatorEvaluator.Fail(
            CalculatorErrorCode.DomainError,
            position,
            $"Domain error in '{functionName}' at position {position}.",
            functionName),
        MathErrorCode.DivisionByZero => CalculatorEvaluator.Fail(
            CalculatorErrorCode.DivisionByZero,
            position,
            $"Division by zero in '{functionName}' at position {position}."),
        MathErrorCode.ArithmeticOverflow => CalculatorEvaluator.Fail(
            CalculatorErrorCode.ArithmeticOverflow,
            position,
            $"Arithmetic overflow in '{functionName}' at position {position}."),
        MathErrorCode.NonFiniteResult => CalculatorEvaluator.Fail(
            CalculatorErrorCode.NonFiniteResult,
            position,
            $"Non-finite result in '{functionName}' at position {position}."),
        MathErrorCode.ConvergenceFailure => CalculatorEvaluator.Fail(
            CalculatorErrorCode.ConvergenceFailure,
            position,
            $"Convergence failure in '{functionName}' at position {position}."),
        MathErrorCode.SingularMatrix => CalculatorEvaluator.Fail(
            CalculatorErrorCode.SingularMatrix,
            position,
            $"Singular matrix in '{functionName}' at position {position}."),
        MathErrorCode.DimensionMismatch => CalculatorEvaluator.Fail(
            CalculatorErrorCode.DimensionMismatch,
            position,
            $"Dimension mismatch in '{functionName}' at position {position}."),
        MathErrorCode.BudgetExceeded => CalculatorEvaluator.Fail(
            CalculatorErrorCode.BudgetExceeded,
            position,
            $"Execution budget exceeded in '{functionName}' at position {position}."),
        _ => throw new InvalidOperationException($"Unsupported math error '{error}'."),
    };
}
